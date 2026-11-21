using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrailFollow
{
    /// <summary>
    /// 같은 PC의 모든 DDO 클라이언트(=플러그인 인스턴스)가 공유하는 메모리.
    ///
    /// [헤더]
    ///  - 지금 조작 중인(포커스된) 캐릭터 ID
    ///  - 전체 명령 (리더 창의 "전체 ▶ / 전체 ■")
    ///  - 공통 설정: 가까이/멀리 모드, 가까이 거리, 멀리 거리, 대상 따라 하기
    /// [캐릭터 칸] 캐릭터마다 1칸
    ///  - 캐릭터 ID, 이름, 생존 신호, 선택한 대상, 탈것/리콜 신호 번호
    /// </summary>
    internal sealed class SharedState : IDisposable
    {
        private const string Name = "TrailFollow.Shared.v2";
        private const int Size = 8192;
        private const int MagicValue = 0x54464C32; // "TFL2"

        // 헤더
        private const int OffMagic = 0;
        private const int OffFocused = 8;     // ulong
        private const int OffCmdSeq = 16;     // int
        private const int OffCmdFollow = 20;  // int
        private const int OffFarMode = 24;    // int
        private const int OffNearGap = 28;    // float
        private const int OffFarGap = 32;     // float
        private const int OffTargetMimic = 36;// int
        private const int OffAntiAfk = 40;    // int  (AFK 방지 켜짐)
        private const int OffAfkMinutes = 44; // int
        private const int OffAfkBar = 48;     // int
        private const int OffAfkSlot = 52;    // int
        private const int OffBuffSeq = 56;    // int  (메인 창 [버프] 버튼을 누른 횟수)
        private const int OffAtkSeq = 60;     // int  (메인 창 [공격] 버튼을 누른 횟수)
        private const int OffAtkState = 64;   // int  (서브 오토어택: 1 켜기 / 0 끄기)
        private const int OffBuffInterval = 68;// float (버프 버튼: 칸 사이 간격, 초)
        private const int OffAtkBar = 72;     // int  (서브 오토어택 버튼 위치: 핫바)
        private const int OffAtkSlot = 76;    // int  (서브 오토어택 버튼 위치: 칸)
        private const int OffBuffTarget = 80; // ulong (버프 버튼을 누를 때 메인이 선택해 둔 대상)
        private const int OffLastLeader = 88; // ulong (마지막으로 리더가 된 캐릭터 = 메인 계정)

        // 캐릭터 칸
        private const int SlotBase = 128;
        private const int SlotSize = 160;
        public const int SlotCount = 24;
        private const int SId = 0;          // ulong
        private const int SBeat = 8;        // long (UTC ticks)
        private const int STarget = 16;     // ulong
        private const int SMountSeq = 24;   // int
        private const int SRecallSeq = 28;  // int
        private const int SPortalSeq = 32;  // int  (입장창 Enter)
        private const int STeleSeq = 36;    // int  (순간이동 아이템)
        private const int SDialogSeq = 104; // int  (퀘스트 대화 선택)
        private const int SDialogOpt = 108; // int
        private const int STeleDid = 112;   // uint
        private const int SFollowing = 116; // int  (비트 1: 따라오기 중, 비트 2: 탈것 탑승 중)
        private const int SRewardSeq = 120; // int  (보상창에서 클릭한 횟수)
        private const int SRewardElem = 124;// uint (보상창에서 클릭한 화면 요소 번호)
        private const int STalkSeq = 128;   // int  (Ctrl+클릭으로 NPC 대화를 시작한 횟수)
        private const int STalkTarget = 136;// ulong (그 NPC)
        private const int SHpPct = 144;     // float (체력 %, 0~100, 모르면 -1)
        private const int SEntrySeq = 148;  // int  (던전 입장창이 열린 횟수)
        private const int SEntryTarget = 152;// ulong (그때 선택해 둔 입구)
        private const int SName = 40;       // char[32]
        private const int NameChars = 32;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _acc;
        private readonly Mutex _lock;
        public bool FreshlyCreated { get; }

        public SharedState()
        {
            _mmf = MemoryMappedFile.CreateOrOpen(Name, Size, MemoryMappedFileAccess.ReadWrite);
            _acc = _mmf.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            _lock = new Mutex(false, Name + ".lock");

            WithLock(() =>
            {
                if (_acc.ReadInt32(OffMagic) != MagicValue)
                {
                    _acc.Write(OffMagic, MagicValue);
                    _acc.Write(OffNearGap, 1f);
                    _acc.Write(OffFarGap, 15f);
                    _acc.Write(OffAfkMinutes, 10);
                    _acc.Write(OffAfkBar, 1);
                    _acc.Write(OffAfkSlot, 1);
                    _acc.Write(OffBuffInterval, 5f);
                    _acc.Write(OffAtkBar, 10);
                    _acc.Write(OffAtkSlot, 8);
                    FreshlyCreatedInternal = true;
                }
            });
            FreshlyCreated = FreshlyCreatedInternal;
        }
        private bool FreshlyCreatedInternal;

        private void WithLock(Action a)
        {
            bool got = false;
            try
            {
                try { got = _lock.WaitOne(500); } catch (AbandonedMutexException) { got = true; }
                a();
            }
            finally { if (got) try { _lock.ReleaseMutex(); } catch { } }
        }

        // ---------------- 헤더 ----------------
        public ulong FocusedCharId
        {
            get => _acc.ReadUInt64(OffFocused);
            set { if (_acc.ReadUInt64(OffFocused) != value) _acc.Write(OffFocused, value); }
        }

        public int CommandSeq => _acc.ReadInt32(OffCmdSeq);
        public int CommandFollow => _acc.ReadInt32(OffCmdFollow);

        public void PostFollowCommand(bool on)
        {
            WithLock(() =>
            {
                _acc.Write(OffCmdFollow, on ? 1 : 0);
                _acc.Write(OffCmdSeq, _acc.ReadInt32(OffCmdSeq) + 1);
            });
        }

        public bool FarMode { get => _acc.ReadInt32(OffFarMode) == 1; set => _acc.Write(OffFarMode, value ? 1 : 0); }
        public float NearGap { get => _acc.ReadSingle(OffNearGap); set => _acc.Write(OffNearGap, value); }
        public float FarGap { get => _acc.ReadSingle(OffFarGap); set => _acc.Write(OffFarGap, value); }
        public bool TargetMimic { get => _acc.ReadInt32(OffTargetMimic) == 1; set => _acc.Write(OffTargetMimic, value ? 1 : 0); }
        public bool AntiAfk { get => _acc.ReadInt32(OffAntiAfk) == 1; set => _acc.Write(OffAntiAfk, value ? 1 : 0); }
        public int AfkMinutes { get => Math.Clamp(_acc.ReadInt32(OffAfkMinutes), 1, 60); set => _acc.Write(OffAfkMinutes, value); }
        public int AfkBar { get => Math.Clamp(_acc.ReadInt32(OffAfkBar), 1, 10); set => _acc.Write(OffAfkBar, value); }
        public int AfkSlot { get => Math.Clamp(_acc.ReadInt32(OffAfkSlot), 1, 10); set => _acc.Write(OffAfkSlot, value); }
        public int BuffSeq => _acc.ReadInt32(OffBuffSeq);
        public int AtkBar { get { int v = _acc.ReadInt32(OffAtkBar); return v >= 1 && v <= 10 ? v : 10; } set => _acc.Write(OffAtkBar, Math.Clamp(value, 1, 10)); }
        public int AtkSlot { get { int v = _acc.ReadInt32(OffAtkSlot); return v >= 1 && v <= 10 ? v : 8; } set => _acc.Write(OffAtkSlot, Math.Clamp(value, 1, 10)); }
        public float BuffInterval
        {
            get { float v = _acc.ReadSingle(OffBuffInterval); return v >= 0.5f && v <= 30f ? v : 5f; }
            set => _acc.Write(OffBuffInterval, Math.Clamp(value, 0.5f, 30f));
        }
        public int AttackSeq => _acc.ReadInt32(OffAtkSeq);
        public bool AttackOn => _acc.ReadInt32(OffAtkState) == 1;
        public void PostAttack(bool on) => WithLock(() =>
        {
            _acc.Write(OffAtkState, on ? 1 : 0);
            _acc.Write(OffAtkSeq, _acc.ReadInt32(OffAtkSeq) + 1);
        });
        public void PostBuff(ulong target) => WithLock(() =>
        {
            _acc.Write(OffBuffTarget, target);
            _acc.Write(OffBuffSeq, _acc.ReadInt32(OffBuffSeq) + 1);
        });
        public ulong BuffTarget => _acc.ReadUInt64(OffBuffTarget);
        public ulong LastLeader { get => _acc.ReadUInt64(OffLastLeader); set => _acc.Write(OffLastLeader, value); }

        // ---------------- 캐릭터 칸 ----------------
        private static int SlotOff(int i) => SlotBase + i * SlotSize;

        /// <summary>내 캐릭터의 칸 번호를 찾거나 새로 잡는다</summary>
        public int ClaimSlot(ulong charId)
        {
            int result = -1;
            WithLock(() =>
            {
                long now = DateTime.UtcNow.Ticks;
                int free = -1;
                for (int i = 0; i < SlotCount; i++)
                {
                    ulong id = _acc.ReadUInt64(SlotOff(i) + SId);
                    if (id == charId) { result = i; break; }
                    long beat = _acc.ReadInt64(SlotOff(i) + SBeat);
                    bool stale = id == 0 || TimeSpan.FromTicks(now - beat).TotalSeconds > 30;
                    if (stale && free < 0) free = i;
                }
                if (result < 0 && free >= 0)
                {
                    int o = SlotOff(free);
                    _acc.Write(o + SId, charId);
                    _acc.Write(o + STarget, 0UL);
                    _acc.Write(o + SMountSeq, 0);
                    _acc.Write(o + SRecallSeq, 0);
                    _acc.Write(o + SPortalSeq, 0);
                    _acc.Write(o + STeleSeq, 0);
                    _acc.Write(o + SDialogSeq, 0);
                    result = free;
                }
                if (result >= 0) _acc.Write(SlotOff(result) + SBeat, now);
            });
            return result;
        }

        public void ReleaseSlot(int slot, ulong charId)
        {
            if (slot < 0) return;
            WithLock(() =>
            {
                if (_acc.ReadUInt64(SlotOff(slot) + SId) == charId) _acc.Write(SlotOff(slot) + SId, 0UL);
            });
        }

        public void Heartbeat(int slot, string name, ulong selectedTarget, bool following, float hpPct, bool mounted)
        {
            if (slot < 0) return;
            int o = SlotOff(slot);
            _acc.Write(o + SBeat, DateTime.UtcNow.Ticks);
            _acc.Write(o + STarget, selectedTarget);
            _acc.Write(o + SFollowing, (following ? 1 : 0) | (mounted ? 2 : 0));
            _acc.Write(o + SHpPct, hpPct);
            WriteName(o, name ?? "");
        }

        public void BumpMount(int slot) { if (slot >= 0) _acc.Write(SlotOff(slot) + SMountSeq, _acc.ReadInt32(SlotOff(slot) + SMountSeq) + 1); }
        public void BumpPortal(int slot) { if (slot >= 0) _acc.Write(SlotOff(slot) + SPortalSeq, _acc.ReadInt32(SlotOff(slot) + SPortalSeq) + 1); }

        public void BumpTeleport(int slot, uint did)
        {
            if (slot < 0) return;
            _acc.Write(SlotOff(slot) + STeleDid, did);
            _acc.Write(SlotOff(slot) + STeleSeq, _acc.ReadInt32(SlotOff(slot) + STeleSeq) + 1);
        }

        public void BumpTalk(int slot, ulong npc)
        {
            if (slot < 0) return;
            _acc.Write(SlotOff(slot) + STalkTarget, npc);
            _acc.Write(SlotOff(slot) + STalkSeq, _acc.ReadInt32(SlotOff(slot) + STalkSeq) + 1);
        }

        public void BumpReward(int slot, uint element)
        {
            if (slot < 0) return;
            _acc.Write(SlotOff(slot) + SRewardElem, element);
            _acc.Write(SlotOff(slot) + SRewardSeq, _acc.ReadInt32(SlotOff(slot) + SRewardSeq) + 1);
        }

        public void BumpDialog(int slot, int option)
        {
            if (slot < 0) return;
            _acc.Write(SlotOff(slot) + SDialogOpt, option);
            _acc.Write(SlotOff(slot) + SDialogSeq, _acc.ReadInt32(SlotOff(slot) + SDialogSeq) + 1);
        }

        public void BumpEntry(int slot, ulong door)
        {
            if (slot < 0) return;
            _acc.Write(SlotOff(slot) + SEntryTarget, door);
            _acc.Write(SlotOff(slot) + SEntrySeq, _acc.ReadInt32(SlotOff(slot) + SEntrySeq) + 1);
        }

        public void BumpRecall(int slot) { if (slot >= 0) _acc.Write(SlotOff(slot) + SRecallSeq, _acc.ReadInt32(SlotOff(slot) + SRecallSeq) + 1); }

        /// <summary>특정 캐릭터(리더)의 칸 정보 읽기. 없거나 오래됐으면 false</summary>
        public bool TryReadChar(ulong charId, out LeaderInfo info)
        {
            info = default;
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < SlotCount; i++)
            {
                int o = SlotOff(i);
                if (_acc.ReadUInt64(o + SId) != charId) continue;
                if (TimeSpan.FromTicks(now - _acc.ReadInt64(o + SBeat)).TotalSeconds > 30) return false;
                info.Target = _acc.ReadUInt64(o + STarget);
                info.MountSeq = _acc.ReadInt32(o + SMountSeq);
                info.RecallSeq = _acc.ReadInt32(o + SRecallSeq);
                info.PortalSeq = _acc.ReadInt32(o + SPortalSeq);
                info.TeleSeq = _acc.ReadInt32(o + STeleSeq);
                info.TeleDid = _acc.ReadUInt32(o + STeleDid);
                info.DialogSeq = _acc.ReadInt32(o + SDialogSeq);
                info.DialogOpt = _acc.ReadInt32(o + SDialogOpt);
                info.RewardSeq = _acc.ReadInt32(o + SRewardSeq);
                info.RewardElem = _acc.ReadUInt32(o + SRewardElem);
                info.TalkSeq = _acc.ReadInt32(o + STalkSeq);
                info.TalkTarget = _acc.ReadUInt64(o + STalkTarget);
                info.HpPct = _acc.ReadSingle(o + SHpPct);
                info.Mounted = (_acc.ReadInt32(o + SFollowing) & 2) != 0;
                info.EntrySeq = _acc.ReadInt32(o + SEntrySeq);
                info.EntryTarget = _acc.ReadUInt64(o + SEntryTarget);
                return true;
            }
            return false;
        }

        /// <summary>공유 메모리에 있는 모든 캐릭터(최근 30초 안에 신호가 있는)의 체력 % 목록</summary>
        public List<(ulong id, float hp)> ReadAllHp()
        {
            var list = new List<(ulong, float)>();
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < SlotCount; i++)
            {
                int o = SlotOff(i);
                ulong id = _acc.ReadUInt64(o + SId);
                if (id == 0) continue;
                if (TimeSpan.FromTicks(now - _acc.ReadInt64(o + SBeat)).TotalSeconds > 30) continue;
                list.Add((id, _acc.ReadSingle(o + SHpPct)));
            }
            return list;
        }

        /// <summary>따라오기 중인 캐릭터가 하나라도 있는지 (메인 창의 따라오기 버튼 표시용)</summary>
        public bool AnyFollowing()
        {
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < SlotCount; i++)
            {
                int o = SlotOff(i);
                if (_acc.ReadUInt64(o + SId) == 0) continue;
                if (TimeSpan.FromTicks(now - _acc.ReadInt64(o + SBeat)).TotalSeconds > 30) continue;
                if ((_acc.ReadInt32(o + SFollowing) & 1) != 0) return true;
            }
            return false;
        }

        /// <summary>이 PC에서 돌고 있는 내 캐릭터인지 (ID)</summary>
        public bool IsKnownChar(ulong charId)
        {
            if (charId == 0) return false;
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < SlotCount; i++)
            {
                int o = SlotOff(i);
                if (_acc.ReadUInt64(o + SId) == charId)
                    return TimeSpan.FromTicks(now - _acc.ReadInt64(o + SBeat)).TotalSeconds <= 30;
            }
            return false;
        }

        /// <summary>이 PC에서 돌고 있는 내 캐릭터인지 (이름, 대소문자 무시 / 첫 단어 비교)</summary>
        public bool IsKnownName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string want = FirstWord(name);
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < SlotCount; i++)
            {
                int o = SlotOff(i);
                if (_acc.ReadUInt64(o + SId) == 0) continue;
                if (TimeSpan.FromTicks(now - _acc.ReadInt64(o + SBeat)).TotalSeconds > 30) continue;
                if (string.Equals(FirstWord(ReadName(o)), want, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public string NameOf(ulong charId)
        {
            for (int i = 0; i < SlotCount; i++)
                if (_acc.ReadUInt64(SlotOff(i) + SId) == charId) return ReadName(SlotOff(i));
            return "";
        }

        private static string FirstWord(string s)
        {
            s = s.Trim();
            int sp = s.IndexOf(' ');
            return sp > 0 ? s.Substring(0, sp) : s;
        }

        private void WriteName(int o, string name)
        {
            var chars = new char[NameChars];
            for (int i = 0; i < NameChars && i < name.Length; i++) chars[i] = name[i];
            _acc.WriteArray(o + SName, chars, 0, NameChars);
        }

        private string ReadName(int o)
        {
            var chars = new char[NameChars];
            _acc.ReadArray(o + SName, chars, 0, NameChars);
            int len = Array.IndexOf(chars, '\0');
            return new string(chars, 0, len < 0 ? NameChars : len);
        }

        public void Dispose()
        {
            _acc.Dispose();
            _mmf.Dispose();
            _lock.Dispose();
        }
    }

    /// <summary>리더가 공유 메모리에 남기는 신호들</summary>
    internal struct LeaderInfo
    {
        public ulong Target;
        public int MountSeq, RecallSeq, PortalSeq, TeleSeq, DialogSeq, DialogOpt, RewardSeq, TalkSeq;
        public uint TeleDid, RewardElem;
        public ulong TalkTarget;
        public float HpPct;
        public bool Mounted;
        public int EntrySeq;
        public ulong EntryTarget;
    }

    internal static class Native
    {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        public const int VK_CONTROL = 0x11;
        /// <summary>지금 Ctrl 키가 눌려 있는지 (PC 전체 기준)</summary>
        public static bool CtrlDown => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        /// <summary>마우스 왼쪽/오른쪽 버튼이 눌려 있는지</summary>
        public static bool MouseButtonDown => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TOPMOST = 0x00000008;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;

        /// <summary>특정 창에만 키를 보냄 (다른 게임 창이 앞에 있어도 그 창은 영향 없음)</summary>
        public static void PostKey(IntPtr hwnd, int vk)
        {
            if (hwnd == IntPtr.Zero || vk <= 0) return;
            uint scan = MapVirtualKey((uint)vk, 0);
            long down = 1 | ((long)scan << 16);
            long up = down | (1L << 30) | (1L << 31);
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)down);
            System.Threading.Thread.Sleep(40);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)up);
        }
    }
}
