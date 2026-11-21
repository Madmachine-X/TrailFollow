using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrailFollow
{
    /// <summary>버프/힐 타이머 한 줄: 지정한 핫바 슬롯을 N초마다 사용</summary>
    public sealed class BuffTimer
    {
        public bool Enabled { get; set; } = true;
        public int Bar { get; set; } = 1;          // 게임 화면 기준 핫바 번호 (1부터)
        public int Slot { get; set; } = 1;         // 게임 화면 기준 슬롯 번호 (1부터)
        public int IntervalSec { get; set; } = 60;
        public bool TargetLeader { get; set; } = false;   // 켜면 리더를 선택한 뒤 사용 (리더에게 버프/힐)
        public string Note { get; set; } = "";
    }

    /// <summary>캐릭터별 설정. 플러그인 폴더에 TrailFollow.{캐릭터ID}.json 으로 저장</summary>
    public sealed class CharSettings
    {
        // 화면 창
        public bool ShowBar { get; set; } = true;
        public int BarX { get; set; } = -1;
        public int BarY { get; set; } = -1;
        public int HotbarNumber { get; set; } = 7;     // 메인 화면에 띄울 이 캐릭터의 핫바 번호 (1~10)
        public int BuffHotbar { get; set; } = 8;       // 메인 창 [버프] 버튼: 이 핫바의 1~10번 칸을 5초 간격으로 사용

        // 따라오기
        public bool ReplayJumps { get; set; } = true;
        public bool MountWithLeader { get; set; } = true;
        public bool RecallWithLeader { get; set; } = true;

        // 2·3차: 상호작용 따라 하기
        public bool MimicCtrlUse { get; set; } = true;        // 리더가 Ctrl+사용한 대상 사용
        public bool MimicDoorUse { get; set; } = true;        // 리더가 그냥 사용해도 문·입구·포탈 종류는 같이 사용 (Awesome AF 기본 동작)
        public bool UseTraps { get; set; } = false;           // Ctrl+사용 따라 하기에 함정 포함
        public bool EnterWithLeader { get; set; } = true;     // 입장창에서 리더가 Enter를 누르면 같이 입장
        public bool MimicTeleportItems { get; set; } = true;  // 리더가 쓴 순간이동 아이템 같이 사용
        public bool MimicQuestDialog { get; set; } = true;
        public bool MimicRewardChoice { get; set; } = true;   // 리더가 보상창에서 클릭한 것을 같이 클릭 (실험)    // 리더가 Ctrl+클릭한 대화 선택지 같이 선택
        public bool CloseExplorerJournals { get; set; } = true;

        // 자동 수락
        public bool AcceptPartyInvites { get; set; } = true;
        public bool AcceptQuestShares { get; set; } = true;
        public bool AcceptResurrections { get; set; } = true;
        public bool AcceptAirshipAndTeleports { get; set; } = true;

        // 타이머
        public bool TimersOnlyWhileFollowing { get; set; } = true;
        public bool TimersPauseWhileMounted { get; set; } = true;   // 탈것 탑승 중에는 타이머 쉬기 (내리면 바로 사용)

        // 리더 체력 비상 힐: 리더 체력이 기준 아래면 리더를 선택하고 지정한 칸들을 차례로 연타 ([+AUTO] 켜진 서브만)
        public bool EmergencyHeal { get; set; } = false;
        public int EmergencyHpPercent { get; set; } = 50;
        public int EmergencyIntervalMs { get; set; } = 600;
        public string EmergencySlots { get; set; } = "8.1";          // "핫바.칸" 쉼표로 구분 (예: 8.1, 8.2)
        public bool EmergencyIncludeParty { get; set; } = true;      // 리더가 괜찮으면 다른 파티원(TrailFollow 캐릭터)도 확인
        public List<BuffTimer> Timers { get; set; } = new List<BuffTimer>();
        public bool TimersPauseInTown { get; set; } = true;     // 마을(공공 지역)에서는 타이머 쉬기
        public bool TimersOnlyNearEnemies { get; set; } = true; // 근처에 살아 있는 적이 있을 때만 타이머 사용 (적이 사라진 뒤 설정한 시간까지 유지)
        public bool PassLootToLeader { get; set; } = true;    // v0.8.18: 상자 속 내 몫 아이템을 리더에게 넘기기
        public int EnemyRadiusM { get; set; } = 20;             // "근처"의 거리(m)
        public int EnemyLingerSec { get; set; } = 5;            // v0.8.9: 적이 사라진 뒤 타이머를 유지하는 시간(초)
        public bool FaceTarget { get; set; } = true;            // v0.8.9: 멈춰 있을 때 근처의 대상 몬스터를 정면으로 바라봄
        public int TimersDungeonDelaySec { get; set; } = 15;    // 던전(퀘스트)에 들어간 뒤 이 시간 동안은 타이머 쉬기
        // 리더와 멀리 떨어지면 누를 핫바 칸 (예: 이동 속도 증가 스킬)
        public bool FarHotkeyEnabled { get; set; } = true;
        public int FarHotkeyDistance { get; set; } = 30;
        public string FarHotkeySlot { get; set; } = "3.1";
        public int SettingsRev { get; set; } = 0;               // 기본값을 바꿀 때 예전 설정 파일을 한 번 고치기 위한 번호
        public bool TimersHoldWhileCatchingUp { get; set; } = true; // 리더에게서 많이 뒤처져 따라가는 중에는 타이머 미루기
        public double TimerMinGapSec { get; set; } = 1.5;       // 타이머끼리 최소 간격(초) - 겹쳐서 입력이 씹히지 않게
        public bool TimerSequenceMode { get; set; } = false;    // 순서 모드: 표 순서대로 1→2→3… 반복 (간격 = 그 줄 다음까지 기다리는 시간)
        public float TrackRange { get; set; } = 3.0f;          // v0.8.20: 트랙 인식 범위 (m) - 선에서 이 거리 이내일 때만 진행 갱신
        public bool TrackRangeEnabled { get; set; } = true;    // v0.8.23: 트랙 인식 범위 기능 사용 여부

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        public static string PathFor(string folder, ulong charId) =>
            Path.Combine(folder, $"TrailFollow.{charId}.json");

        public static CharSettings Load(string folder, ulong charId)
        {
            try
            {
                var p = PathFor(folder, charId);
                if (File.Exists(p))
                {
                    var s = JsonSerializer.Deserialize<CharSettings>(File.ReadAllText(p)) ?? new CharSettings();
                    if (s.SettingsRev < 1)   // v0.8.5: 적 거리 기본값 30m → 20m
                    {
                        if (s.EnemyRadiusM == 30) s.EnemyRadiusM = 20;
                        s.SettingsRev = 1;
                    }
                    if (s.SettingsRev < 2)   // v0.8.18: 상자 아이템 넘기기 기본 켜짐
                    {
                        s.PassLootToLeader = true;
                        s.SettingsRev = 2;
                    }
                    if (s.SettingsRev < 3)   // v0.8.20: 트랙 인식 범위 기본값
                    {
                        s.TrackRange = 3.0f;
                        s.SettingsRev = 3;
                    }
                    return s;
                }
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 캐릭터 설정을 읽지 못해 기본값 사용", ex); }
            return new CharSettings();
        }

        public void Save(string folder, ulong charId)
        {
            try { File.WriteAllText(PathFor(folder, charId), JsonSerializer.Serialize(this, JsonOpts)); }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 캐릭터 설정 저장 실패", ex); }
        }
    }

    /// <summary>모든 캐릭터 공통 설정 (메인 창에서 조정). TrailFollow.global.json</summary>
    public sealed class GlobalSettings
    {
        public float NearGap { get; set; } = 1f;
        public float FarGap { get; set; } = 15f;
        public bool FarMode { get; set; } = false;
        public bool TargetMimic { get; set; } = false;
        // AFK 방지: 켜짐 여부는 저장하지 않음 (Dungeon Helper를 다시 켜면 항상 꺼진 상태로 시작)
        public int AfkMinutes { get; set; } = 10;
        public int AfkBar { get; set; } = 1;
        public int AfkSlot { get; set; } = 1;
        public float BuffIntervalSec { get; set; } = 5f;   // [버프] 버튼: 칸 사이 간격(초)
        public int AtkBar { get; set; } = 10;              // [공격] 버튼: 서브들이 누를 오토어택 칸 (핫바)
        public int AtkSlot { get; set; } = 8;              //               (칸)

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };
        private static string PathFor(string folder) => Path.Combine(folder, "TrailFollow.global.json");

        public static GlobalSettings Load(string folder)
        {
            try
            {
                var p = PathFor(folder);
                if (File.Exists(p)) return JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(p)) ?? new GlobalSettings();
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 공통 설정을 읽지 못해 기본값 사용", ex); }
            return new GlobalSettings();
        }

        public void Save(string folder)
        {
            try { File.WriteAllText(PathFor(folder), JsonSerializer.Serialize(this, JsonOpts)); }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 공통 설정 저장 실패", ex); }
        }

        internal static void SaveFrom(SharedState s, string folder) => new GlobalSettings
        {
            NearGap = s.NearGap,
            FarGap = s.FarGap,
            FarMode = s.FarMode,
            TargetMimic = s.TargetMimic,
            AfkMinutes = s.AfkMinutes,
            AfkBar = s.AfkBar,
            AfkSlot = s.AfkSlot,
            BuffIntervalSec = s.BuffInterval,
            AtkBar = s.AtkBar,
            AtkSlot = s.AtkSlot,
        }.Save(folder);

        internal void ApplyTo(SharedState s)
        {
            s.NearGap = Math.Max(0f, NearGap);
            s.FarGap = Math.Max(0.5f, FarGap);
            s.FarMode = FarMode;
            s.TargetMimic = TargetMimic;
            s.AfkMinutes = Math.Clamp(AfkMinutes, 1, 60);
            s.AfkBar = Math.Clamp(AfkBar, 1, 10);
            s.AfkSlot = Math.Clamp(AfkSlot, 1, 10);
            s.BuffInterval = BuffIntervalSec <= 0 ? 5f : BuffIntervalSec;
            s.AtkBar = AtkBar is >= 1 and <= 10 ? AtkBar : 10;
            s.AtkSlot = AtkSlot is >= 1 and <= 10 ? AtkSlot : 8;
        }
    }
}
