using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Events;

namespace TrailFollow
{
    internal struct TrailPoint
    {
        public Vector3 P;
        public float Arc;   // 발자취 시작점부터 이 점까지 누적 거리
        public bool Jump;
        public bool Fast;   // 이 점까지 리더가 비정상적으로 빠르게 이동함 (대시·순간이동 기술 등) - 걸어서 못 지날 수 있음
    }

    /// <summary>
    /// 클라이언트 1개(=캐릭터 1명)를 담당하는 엔진.
    ///  - 리더(따라오기를 켠 순간 조작 중이던 캐릭터)를 고정해서 따라간다
    ///  - 리더가 지나간 발자취를 "이어진 선"으로 보고, 선 위에서 어디까지 왔는지(진행 거리)로 이동한다
    ///  - 진행 거리는 절대 뒤로 가지 않는다 (걸림 탈출 중 의도적 후퇴만 예외)
    /// </summary>
    internal sealed class Follower : IDisposable
    {
        // ---- 튜닝 값 ----
        private const float LandblockSize = 160f;   // DDO 랜드블록 크기 (Awesome AF와 동일)
        private const int TickMs = 50;
        private const float RecordStep = 0.6f;      // 리더가 이만큼 움직일 때마다 발자취 1개 기록
        private const float Lookahead = 1.5f;       // 평소: 선 위에서 이만큼 앞을 바라봄
        private const float TightLookahead = 0.5f;  // 걸림 직후: 선에 바짝 붙어서 이동
        private const float OnPathLateral = 0.8f;   // 선에서 이 거리 이내면 "선 위에 있음"
        private const float ProjectLateral = 3.0f;  // 선에서 이 거리 이내일 때만 진행 거리 갱신
        private const float ShortcutLateral = 1.0f; // 선이 다시 내 곁을 지나가면 그 지점으로 건너뜀
        private const float TeleportJump = 20f;     // 한 틱에 이 이상 이동하면 순간이동
        private const float MaxLeaderRange = 400f;  // 이보다 멀면 다른 지역
        private const float StuckIgnoreZone = 2.5f; // 목표 거리까지 이만큼 이내면 걸림 판단 안 함
        private const float InstantJumpGap = 3.0f;  // 이 거리 이하로 따라올 땐 리더와 동시에 점프
        // 리더 점프 감지: 게임의 점프 동작 신호(FX 번호, Awesome AF 분석값)
        private static readonly HashSet<uint> JumpFx = new HashSet<uint> { 268435475, 268436355, 268438884 };
        private const int JumpInputAction = 179;    // 점프 (AltInputActions.MovementLongJump)
        private const int MaxTrail = 4000;

        // 캐릭터 속성 번호 (Awesome AF 분석값)
        private const uint PropMounted = 268484148;   // byte: 0이 아니면 탈것 탑승 중
        private const uint ShortcutTypeMount = 50;

        private readonly IDdoGameDataProvider _ddo;
        private readonly IGameDataProvider _g;
        private readonly string _folder;
        /// <summary>플러그인이 설치된 폴더 (설정 파일과 TrailFollow.dll이 있는 곳)</summary>
        public string PluginFolder => _folder;
        internal readonly SharedState Shared;
        private readonly Thread _thread;
        private volatile bool _alive = true;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double Now => _clock.Elapsed.TotalSeconds;

        public IGameDataProvider Game => _g;
        public readonly object SettingsLock = new object();
        public ulong? CharId { get; private set; }
        public string CharName { get; private set; } = "";
        public CharSettings Settings { get; private set; }
        public int Slot { get; private set; } = -1;
        public string Status { get; private set; } = Lang.T("시작 중", "Starting");
        public event Action CharacterChanged;

        // 따라오기 상태
        private volatile bool _followOn;
        public bool FollowOn => _followOn;
        public ulong LockedLeader { get; private set; }
        public string LockedLeaderName => LockedLeader == 0 ? "" : Shared.NameOf(LockedLeader);
        public bool IsFocused { get; private set; }
        /// <summary>서브 창의 [자동 힐/버프] 버튼. 켜져 있으면 타이머 설정대로 자동 사용 (접속할 때마다 꺼진 상태로 시작)</summary>
        public volatile bool AutoTimersOn;
        /// <summary>메인(리더) 조작 창을 보여줄지: 지금 조작 중이고 따라오기 꺼짐</summary>
        public bool ShowLeaderPanel => IsMain && !_followOn;
        /// <summary>마지막으로 조작한 게임 창의 캐릭터인지 (Alt+Tab으로 다른 프로그램을 눌러도 유지)</summary>
        public bool IsMain => CharId.HasValue && Shared.FocusedCharId == CharId.Value;

        // 발자취
        private readonly List<TrailPoint> _trail = new List<TrailPoint>();
        private ulong _trailLeader;
        private volatile bool _leaderJumped;   // 이벤트 스레드에서 세팅, 작업 스레드에서 소비
        private float _leaderSpeed;
        private bool _waitTeleport;
        private Vector3 _waitMyPos;
        private double _waitSince;

        // 선 위 진행 상황
        private float _prog;          // 진행 거리 (Arc 기준)
        private int _seg;             // 지금 있는 구간 번호 (trail[_seg] → trail[_seg+1])
        private float _lateral;       // 선에서 떨어진 거리
        private Vector3 _lastOnPath;  // 마지막으로 선 위에 있었던 위치
        private float _lastOnPathArc;
        private bool _hasLastOnPath;  // _lastOnPath가 유효한지
        private float _tightUntil;    // 이 진행 거리까지는 선에 바짝 붙어서 이동
        private float _jumpCheckedArc;

        // 이동/걸림
        private bool _moving;
        private readonly Queue<(double t, float eff)> _hist = new Queue<(double, float)>();
        private double _movingSince;
        private int _recoverStep;
        private float _effAtRecovery;   // 마지막 탈출 시도 때의 진행 거리
        private Vector3? _backtrackTo;
        private double _backtrackUntil;
        private double _phaseEnd;
        private bool _inPhase;
        private bool _gaveUp;
        private const float RejoinDistance = 15f;
        private const int RecoverLoops = 3;   // 탈출 시도 바퀴 수 (한 바퀴 = 앞 → 왼쪽 → 오른쪽 → 뒤, 모두 움직이며 점프)

        private void GiveUpHere()
        {
            Plugin.Log.Info($"[TrailFollow] {CharName}: 탈출 {RecoverLoops}바퀴 실패 - 그 자리에서 대기 (리더가 {RejoinDistance:0}m 안으로 오면 다시 따라감)");
            _gaveUp = true;
            _skillBlocked = false;
            _gaveUpDist = _lastLeaderDist;
            StopMoving();
        }
        private float _gaveUpDist, _lastLeaderDist = float.MaxValue;
        private double _holdUntil;       // 탈것/리콜 시전 중에는 움직이지 않음
        private double _lastJump = -10;
        private double _lastHeadingSet = -10;
        private double _lastHeadingValue = double.NaN;
        private bool _wasPaused = true;
        private bool _inputOwned;

        // 리더 신호
        private int _seenMount = int.MinValue, _seenRecall = int.MinValue, _seenPortal, _seenTele, _seenDialog, _seenReward, _seenTalk, _seenEntry;
        private bool _npcDialogWasOpen;
        private double _nextDialogPoll;
        private long _pendingUse;          // 리더가 Ctrl+사용한 대상 (이벤트 스레드 → 작업 스레드)
        private long _pendingAutoUse;      // 리더가 그냥 사용한 대상 (문·입구 종류만 따라 사용)
        private double _lastCtrlAt = -10;  // 마지막으로 Ctrl이 눌려 있던 시각
        private double _nextJournalCheck;
        private double _nextAfk = -1;        // 다음 AFK 방지 입력 시각 (-1: 꺼짐)
        private const uint UseFx = 268439164;   // "사용" 동작 신호 (Awesome AF 분석값)
        private ulong _lastMimicTarget;

        // 공유 명령 / 타이머
        private int _lastSeq;
        private readonly Dictionary<BuffTimer, double> _nextFire = new Dictionary<BuffTimer, double>();
        private readonly Random _rng = new Random();
        private double _nameRetry;
        private double _nullSince = -1;

        public Follower(IDdoGameDataProvider ddo, string folder, SharedState shared)
        {
            _ddo = ddo;
            _g = (IGameDataProvider)ddo;
            _folder = folder;
            Shared = shared;
            _thread = new Thread(Run) { IsBackground = true, Name = "TrailFollow Worker" };
            _thread.Start();
            _watchdog = new Thread(Watchdog) { IsBackground = true, Name = "TrailFollow Watchdog" };
            _watchdog.Start();
        }

        // =====================================================================
        //  외부(버튼)에서 호출
        // =====================================================================
        public void SetFollow(bool on)
        {
            _followOn = on;
            if (!on) LockedLeader = 0;
        }

        /// <summary>게임 동작 신호 이벤트 (Features에서 호출). 리더의 점프 동작이면 기록</summary>
        /// <summary>리더 쪽: 내가 Ctrl+사용으로 대화를 시작한 시각 (이후 60초 동안 대화 선택지는 Ctrl 없이도 서브에게 알림)</summary>
        public double SelfCtrlUseAt { get; private set; } = -1000;
        /// <summary>Ctrl로 시작한 대화가 이어지는 중인지 (마지막 대화 동작 후 60초)</summary>
        public bool InConversation => Now - SelfCtrlUseAt < 60;
        /// <summary>대화 선택·말 걸기를 할 때마다 60초를 새로 시작</summary>
        public void TouchConversation() => SelfCtrlUseAt = Now;
        /// <summary>리더가 마지막으로 대화 선택지를 알린 시각</summary>
        public double LastDialogBroadcastAt { get; set; } = -1000;
        public const int DialogCloseSignal = 99;   // 대화 선택지 대신 "대화창 닫기"를 뜻하는 값
        public double NowSeconds => Now;
        public volatile bool RewardWindowOpen;

        /// <summary>
        /// 리더 쪽: NPC 대화창이 새로 열린 순간 Ctrl이 눌려 있었으면, 선택한 NPC를 서브들에게 알림.
        /// (NPC에게 말을 거는 동작은 "사용" 신호가 나오지 않아서 대화창으로 감지)
        /// </summary>
        private void DetectCtrlTalk(ulong selected)
        {
            if (Now < _nextDialogPoll) return;
            _nextDialogPoll = Now + 0.15;
            bool open = Features.IsNpcDialogOpen(_ddo);
            // Ctrl 키는 PC 전체에서 읽히므로, 지금 조작 중인 창(리더)만 알림 - 서브가 따라 하면서 다시 알리지 않게
            if (open && !_npcDialogWasOpen && IsFocused && selected != 0)
            {
                bool ctrl = Native.CtrlDown || Now - _lastCtrlAt < 3.0;
                bool continuing = InConversation;   // Ctrl로 시작한 대화가 이어지는 중이면 Ctrl 없이 다시 말 걸어도 알림
                if (ctrl || continuing)
                {
                    TouchConversation();
                    Shared.BumpTalk(Slot, selected);
                    Plugin.Log.Info($"[TrailFollow] {CharName}: {(ctrl ? "Ctrl+클릭으로" : "대화 이어서")} NPC 대화 시작 - 서브들에게 알림");
                }
            }
            // 리더 대화창이 선택지 신호 없이 닫힘 (예: 선택지가 "창 닫기" 하나뿐인 경우) → 서브들도 대화창 닫기
            if (!open && _npcDialogWasOpen && IsFocused && InConversation && Now - LastDialogBroadcastAt > 1.0)
            {
                Shared.BumpDialog(Slot, DialogCloseSignal);
                Plugin.Log.Info($"[TrailFollow] {CharName}: 대화창 닫힘 - 서브들에게 알림");
            }
            _npcDialogWasOpen = open;
        }

        public void OnScript(ulong sourceId, ulong targetId, uint fxId)
        {
            if (sourceId != 0 && CharId.HasValue && sourceId == CharId.Value && fxId == UseFx && IsFocused && (Native.CtrlDown || Now - _lastCtrlAt < 1.0))
                SelfCtrlUseAt = Now;
            if (!_followOn || sourceId == 0 || sourceId != LockedLeader) return;
            if (JumpFx.Contains(fxId)) _leaderJumped = true;
            else if (fxId == UseFx && targetId != 0)
            {
                if (Native.CtrlDown || Now - _lastCtrlAt < 1.0) Interlocked.Exchange(ref _pendingUse, (long)targetId);
                else Interlocked.Exchange(ref _pendingAutoUse, (long)targetId);
            }
        }

        // v0.8.27: 가까이 모드는 0m(리더와 같은 자리)까지 허용
        public float CurrentGap => Shared.FarMode ? Math.Max(0.5f, Shared.FarGap) : Math.Max(0f, Shared.NearGap);

        public void AdjustGlobalGap(float delta)
        {
            if (Shared.FarMode) Shared.FarGap = Math.Clamp((float)Math.Round((Shared.FarGap + delta) * 2) / 2, 0.5f, 120f);
            else Shared.NearGap = Math.Clamp((float)Math.Round((Shared.NearGap + delta) * 2) / 2, 0f, 50f);
            GlobalSettings.SaveFrom(Shared, _folder);
        }

        public void ToggleFarMode() { Shared.FarMode = !Shared.FarMode; GlobalSettings.SaveFrom(Shared, _folder); }
        /// <summary>
        /// 서브 창의 액션 버튼: 메인이 지금 선택해 둔 대상을 이 서브가 선택해서 사용.
        /// 메인 = 따라오기 중이면 고정된 리더, 아니면 마지막으로 조작한 게임 창의 캐릭터.
        /// </summary>
        public void UseMainTarget()
        {
            ulong me = CharId ?? 0;
            ulong main = LockedLeader != 0 ? LockedLeader : Shared.FocusedCharId;
            if (me == 0) return;
            if (main == 0 || main == me) { Plugin.Log.Info($"[TrailFollow] {CharName}: 액션 - 메인 창을 먼저 클릭해 주세요"); return; }
            if (!Shared.TryReadChar(main, out LeaderInfo li) || li.Target == 0)
            {
                Plugin.Log.Info($"[TrailFollow] {CharName}: 액션 - 메인이 선택한 대상이 없음");
                return;
            }
            if (li.Target == me) return;   // 메인이 이 서브 자신을 선택한 경우
            Features.UseObject(_ddo, li.Target, true, false, CharName);
        }

        public void ToggleAntiAfk() { Shared.AntiAfk = !Shared.AntiAfk; }
        public void SaveGlobal() => GlobalSettings.SaveFrom(Shared, _folder);
        public void ToggleTargetMimic() { Shared.TargetMimic = !Shared.TargetMimic; GlobalSettings.SaveFrom(Shared, _folder); }
        public void BroadcastFollow(bool on)
        {
            if (on && CharId.HasValue) Shared.LastLeader = CharId.Value;   // 메인 창에서 따라오기를 켠 캐릭터가 리더
            Shared.PostFollowCommand(on);
        }

        // =====================================================================
        //  메인 루프
        // =====================================================================
        private void Run()
        {
            _lastSeq = Shared.CommandSeq;
            while (_alive)
            {
                long start = _clock.ElapsedMilliseconds;
                long slept = _lastTickEndMs > 0 ? start - _lastTickEndMs : 0;
                Volatile.Write(ref _tickBeatMs, start);
                _markAtMs = start; _marks.Clear();
                try { Tick(); }
                catch (Exception ex)
                {
                    Plugin.Log.Error("[TrailFollow] 루프 오류", ex);
                    SafeRelease();
                    Thread.Sleep(1000);
                }
                long end = _clock.ElapsedMilliseconds;
                Volatile.Write(ref _tickBeatMs, end);
                // v0.8.25: 진단 - 루프가 늦어진 경우 어느 단계에서 얼마나 걸렸는지 기록
                if (Diag.Enabled && (end - start > 120 || slept > 150))
                    Diag.Write(CharName, $"SLOW tick={end - start}ms sleep={slept}ms [{_marks}]");
                _lastTickEndMs = end;
                Thread.Sleep(TickMs);
            }
            SafeRelease();
        }

        // v0.8.25: 루프가 멈춘 동안 이동키가 눌린 채로 남아 서브가 리더를 지나쳐 달리는 문제 방지
        //          별도 스레드가 루프를 지켜보다가 0.2초 넘게 멈추면 이동키를 뗌 (루프가 돌아오면 필요한 만큼 다시 누름)
        private Thread _watchdog;
        private long _tickBeatMs;
        private long _lastTickEndMs;
        private readonly System.Text.StringBuilder _marks = new System.Text.StringBuilder();
        private long _markAtMs;

        private void Watchdog()
        {
            while (_alive)
            {
                Thread.Sleep(30);
                try
                {
                    long late = _clock.ElapsedMilliseconds - Volatile.Read(ref _tickBeatMs);
                    if (late > 200 && _inputOwned)
                    {
                        SetFlags(false, false, false, false);
                        Diag.Write(CharName, $"WATCHDOG 루프가 {late}ms 멈춤 - 이동키 뗌");
                    }
                }
                catch { }
            }
        }

        /// <summary>진단: 직전 표시 이후 20ms 넘게 걸린 단계만 이름과 시간을 남김</summary>
        private void Mark(string name)
        {
            long now = _clock.ElapsedMilliseconds;
            long d = now - _markAtMs;
            _markAtMs = now;
            if (d >= 20) _marks.Append(name).Append('=').Append(d).Append("ms ");
        }

        private void Tick()
        {
            ulong? id = null;
            try { id = _g.GetCurrentCharacterId(); } catch { }

            // 캐릭터 정보가 잠깐 비는 경우(사망·로딩 등)에는 따라오기 상태를 유지하고 기다림
            if (id == null && CharId != null)
            {
                if (_nullSince < 0) _nullSince = Now;
                if (_entryClosedAt > 0 && Now - _entryClosedAt < 10) BroadcastEnterFallback("지역 이동 중");   // 입장창이 닫히고 곧바로 로딩 → 입장한 것
                if (Now - _nullSince < 20) { StopMoving(); SetStatus(Lang.T("캐릭터 정보를 읽는 중...", "Reading character info...")); return; }
            }
            else _nullSince = -1;

            if (id != CharId) OnCharacterChanged(id);
            if (id == null || Settings == null) { SetStatus(Lang.T("캐릭터 선택 화면", "Character selection screen")); StopMoving(); return; }
            ulong me = id.Value;

            if (!IsRealName(CharName) && Now > _nameRetry)
            {
                _nameRetry = Now + 3;
                string n = ReadName();
                if (IsRealName(n)) { CharName = n; CharacterChanged?.Invoke(); }
                else if (string.IsNullOrEmpty(CharName)) CharName = n;
            }

            // 공유 메모리: 내 칸 갱신 (이름, 선택한 대상)
            if (Slot < 0) Slot = Shared.ClaimSlot(me);
            ulong sel = 0;
            try { sel = _g.GetSelectedObjectId() ?? 0; } catch { }
            Shared.Heartbeat(Slot, CharName, sel, _followOn, ReadHpPercent(), IsMounted());

            // 지금 조작 중인 창인지
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = _g.GetGameWindowHandle(); } catch { }
            IsFocused = hwnd != IntPtr.Zero && Native.GetForegroundWindow() == hwnd;
            if (IsFocused) Shared.FocusedCharId = me;
            DetectCtrlTalk(sel);
            DetectCtrlUseTeleport(sel);
            DetectEntryWindow(sel);
            if (Native.CtrlDown) _lastCtrlAt = Now;

            ApplyBroadcast();
            Mark("준비");

            // 리더 고정: 따라오기를 켠 순간(또는 그 뒤 처음으로) 조작 중인 다른 캐릭터
            if (_followOn && LockedLeader == 0)
            {
                ulong f = Shared.FocusedCharId;
                if (f != 0 && f != me)
                {
                    LockedLeader = f;
                    Shared.LastLeader = f;
                    ResetTrail();
                    _seenMount = _seenRecall = int.MinValue;
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 리더 고정 → {Shared.NameOf(f)}");
                }
            }

            bool paused = _followOn && IsFocused;   // 이 창을 직접 조작 중이면 잠시 멈춤
            RunRecallPending();   // 리콜이 최우선
            Mark("리콜");
            RunTimers(paused);
            Mark("타이머");
            RunAntiAfk();
            Mark("AFK");
            RunBuffSequence();
            Mark("버프");
            RunAutoAttackCommand();
            Mark("공격");
            RunEmergencyHeal();
            Mark("비상힐");
            RunTargetMimic();
            Mark("대상따라하기");

            if (!_followOn)
            {
                IdleRecallCheck(me);   // 따라오기가 꺼져 있어도 메인이 리콜하면 같이 리콜
                StopMoving(); _gaveUp = false; _recoverStep = 0; _wasPaused = true;
                // v0.8.10: 따라오기가 꺼져 있어도 근처의 대상 몬스터를 바라봄 (이 창을 직접 조작 중일 때는 제외)
                bool facingIdle = false;
                if (!IsMain && !IsFocused) { var mp = MyPos(); if (mp.HasValue) facingIdle = FaceSelectedMonster(mp.Value); }
                SetStatus(IsMain ? Lang.T("메인", "Main") : facingIdle ? Lang.T("따라오기 꺼짐 · 대상 바라봄", "Follow off · facing target") : Lang.T("따라오기 꺼짐", "Follow off"));
                return;
            }
            _idleLeader = 0;
            if (LockedLeader == 0) { StopMoving(); SetStatus(Lang.T("리더 대기 - 메인 창을 클릭하세요", "Waiting for a leader - click the main game window")); return; }

            ulong leader = LockedLeader;
            LeaderSignals(leader, paused);
            Mark("리더신호");

            ILocation myLoc = GetLocation(me, false);
            Mark("내위치");
            ILocation leadLoc = GetLocation(leader, true);
            Mark("리더위치");
            if (myLoc == null) { StopMoving(); SetStatus(Lang.T("내 위치를 읽을 수 없음", "Cannot read my position")); return; }
            if (leadLoc == null) { StopMoving(); ResetTrail(); SetStatus(Lang.T("리더 위치를 찾을 수 없음", "Cannot find the leader's position")); return; }

            Vector3 myP = Global(myLoc);
            Vector3 lp = Global(leadLoc);

            if (leader != _trailLeader) { ResetTrail(); _trailLeader = leader; }
            if (leadLoc.Region != myLoc.Region || Vector3.Distance(myP, lp) > MaxLeaderRange)
            {
                StopMoving(); ResetTrail(); SetStatus(Lang.T("리더와 다른 지역", "In a different area from the leader"));
                return;
            }

            RecordLeader(leadLoc, lp, myP, paused);
            Mark("기록");

            if (paused) { StopMoving(); _wasPaused = true; SetStatus(Lang.T("직접 조작 중 - 일시정지", "Played directly - paused")); return; }
            if (_wasPaused) { JoinTrail(myP); _wasPaused = false; }   // 다시 따라가기 시작: 가장 가까운 선 위 지점에 합류

            // 죽어서 영혼 상태여도 계속 따라옴 (영혼도 움직일 수 있음). 점프·걸림 탈출 점프는 Jump()에서 생략
            if (Now < _holdUntil) { StopMoving(); SetStatus(Lang.T("탈것/리콜 대기", "Waiting for mount/recall")); return; }

            if (_waitTeleport)
            {
                bool iTeleported = Vector3.Distance(myP, _waitMyPos) > TeleportJump;
                if (iTeleported || Vector3.Distance(myP, lp) < 30f || Now - _waitSince > 20)
                {
                    _waitTeleport = false;
                    JoinTrail(myP);
                }
                else { StopMoving(); SetStatus(Lang.T("리더 순간이동 - 따라 이동 대기", "Leader teleported - waiting to follow")); return; }
            }

            Steer(myLoc, myP, lp);
            Mark("조향");
            TraceMove(myLoc, myP, lp);
        }

        // v0.8.24: 이동 진단 기록 (Downloads\TrailFollow\trace\캐릭터이름.txt)
        private double _traceAt = -10;
        private string _traceKind;
        private Vector3 _traceLp;
        private double _traceLpAt = -10;
        private float _traceLeadSpeed;
        private float _dbgRemaining;

        private void TraceMove(ILocation myLoc, Vector3 myP, Vector3 lp)
        {
            if (!Diag.Enabled) return;
            try
            {
                double dt = Now - _traceLpAt;
                if (dt >= 0.2)
                {
                    _traceLeadSpeed = dt < 2 ? Dist2D(lp, _traceLp) / (float)dt : 0f;
                    _traceLp = lp; _traceLpAt = Now;
                }

                float vx = 0, vy = 0;
                try { var mv = myLoc.Velocity; if (mv.HasValue) { vx = (float)mv.Value.X; vy = (float)mv.Value.Y; } } catch { }
                float speed = (float)Math.Sqrt(vx * vx + vy * vy);
                bool f = false, b = false, l = false, r = false;
                try { f = _ddo.RunningForward; b = _ddo.RunningBackward; l = _ddo.StrafeLeft; r = _ddo.StrafeRight; } catch { }
                string kind = (_moving ? "M" : "S") + (f ? "F" : "") + (b ? "B" : "") + (l ? "L" : "") + (r ? "R" : "");
                bool active = _moving || speed > 0.5f || f || b || l || r;
                bool changed = kind != _traceKind;
                if (!changed && (!active || Now - _traceAt < 0.2)) return;
                _traceKind = kind; _traceAt = Now;

                float dx = lp.X - myP.X, dy = lp.Y - myP.Y;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                float toward = d > 0.01f ? (vx * dx + vy * dy) / d : 0f;   // + 이면 리더 쪽으로 움직이는 중
                double velH = speed > 0.3f ? Normalize(Math.PI / 2 - Math.Atan2(vy, vx)) : double.NaN;
                Diag.Write(CharName,
                    $"MV {kind} d={_lastLeaderDist:0.0} rem={_dbgRemaining:0.0} gap={CurrentGap:0.#} near={(_nearMode ? 1 : 0)} " +
                    $"spd={speed:0.0} toLead={toward:0.0} lspd={_traceLeadSpeed:0.0} " +
                    $"me=({myP.X:0.0},{myP.Y:0.0},{myP.Z:0.0}) lead=({lp.X:0.0},{lp.Y:0.0},{lp.Z:0.0}) " +
                    $"velH={velH:0.00} setH={_lastHeadingValue:0.00} cast={(Now < _castMoveHold ? 1 : 0)} atk={(Shared.AttackOn ? 1 : 0)} " +
                    $"cf={(_combatMoving ? 1 : 0)} lat={_lateral:0.0} trail={_trail.Count} st={Status}");
            }
            catch { }
        }

        // =====================================================================
        //  던전 입장 따라 하기
        // =====================================================================
        private bool _entryWasOpen;
        private double _entryClosedAt = -1;
        private Vector3? _entryClosedPos;
        /// <summary>리더가 마지막으로 입장(Enter)을 알린 시각</summary>
        public double LastEnterBroadcastAt { get; set; } = -1000;

        private Vector3? MyPos()
        {
            try { var id = _g.GetCurrentCharacterId(); if (id == null) return null; var l = _g.GetEntityLocation(id.Value); return l == null ? (Vector3?)null : Global(l); }
            catch { return null; }
        }

        private void BroadcastEnterFallback(string why)
        {
            _entryClosedAt = -1;
            if (Now - LastEnterBroadcastAt < 10) return;
            LastEnterBroadcastAt = Now;
            Shared.BumpPortal(Slot);
            Plugin.Log.Info($"[TrailFollow] {CharName}: 입장창이 닫히고 {why} - 입장으로 보고 서브들에게 알림");
        }
        private ulong _entryDoor;
        private double _entryUntil = -1, _entryNextTry, _enterPendingUntil = -1;

        // v0.7.8: 리더가 Ctrl을 누른 채 물체(디멘션 도어 등)를 사용해서 순간이동하면, 사용 신호가 안 잡히는 물체도 서브들이 같은 물체를 사용하게 함
        private ulong _ctrlSel;
        private double _ctrlSelAt = -100, _lastCtrlTeleportBroadcast = -100;
        private Vector3? _myLastPos;

        private void DetectCtrlUseTeleport(ulong selected)
        {
            if (!IsFocused || _followOn) { _myLastPos = null; return; }
            if (Native.CtrlDown && selected != 0 && selected != (CharId ?? 0) && !Shared.IsKnownChar(selected)) { _ctrlSel = selected; _ctrlSelAt = Now; }
            Vector3? p = MyPos();
            if (p.HasValue && _myLastPos.HasValue && Vector3.Distance(p.Value, _myLastPos.Value) > TeleportJump
                && _ctrlSel != 0 && Now - _ctrlSelAt < 15 && Now - _lastCtrlTeleportBroadcast > 5)
            {
                _lastCtrlTeleportBroadcast = Now;
                Shared.BumpTalk(Slot, _ctrlSel);
                Plugin.Log.Info($"[TrailFollow] {CharName}: Ctrl로 사용한 물체로 순간이동 - 서브들도 같은 물체 사용");
                _ctrlSel = 0;
            }
            if (p.HasValue) _myLastPos = p;
        }

        /// <summary>리더 쪽: 던전 입장창이 새로 열리면 선택해 둔 입구를 서브들에게 알림</summary>
        private double _nextEntryPoll;
        private void DetectEntryWindow(ulong selected)
        {
            if (Now < _nextEntryPoll) return;
            _nextEntryPoll = Now + 0.15;
            bool open = Features.IsQuestEntryOpen(_ddo);
            if (open && !_entryWasOpen && IsFocused)
            {
                Shared.BumpEntry(Slot, selected);
                Plugin.Log.Info($"[TrailFollow] {CharName}: 던전 입장창 열림 - 서브들에게 알림");
            }
            // 리더 입장창이 닫힘: Enter 클릭 신호를 못 받았을 수 있으니(키보드 Enter 등) 곧바로 리더가 다른 곳으로 옮겨지면 입장으로 판단
            if (!open && _entryWasOpen && IsFocused && Now - LastEnterBroadcastAt > 10)
            {
                _entryClosedAt = Now;
                _entryClosedPos = MyPos();
            }
            _entryWasOpen = open;
            if (_entryClosedAt > 0)
            {
                if (Now - _entryClosedAt > 10) _entryClosedAt = -1;   // 10초 안에 이동이 없으면 취소한 것으로 봄
                else
                {
                    Vector3? p = MyPos();
                    if (p == null || _entryClosedPos == null || Vector3.Distance(p.Value, _entryClosedPos.Value) > TeleportJump)
                        BroadcastEnterFallback("리더 위치가 바뀜");
                }
            }
        }

        /// <summary>서브 쪽: 입장창이 열릴 때까지 리더가 쓴 입구를 3초마다 사용. 열렸고 리더가 이미 Enter했으면 Enter</summary>
        private void RunEntryAttempts(bool paused)
        {
            if (_entryUntil < Now && _enterPendingUntil < Now) return;
            if (Features.IsQuestEntryOpen(_ddo))
            {
                if (_entryUntil >= Now) Plugin.Log.Info($"[TrailFollow] {CharName}: 내 던전 입장창 열림");
                _entryUntil = -1;
                if (_enterPendingUntil >= Now) { _enterPendingUntil = -1; Features.EnterWithLeader(_ddo, CharName); }
                return;
            }
            if (paused || _entryDoor == 0 || _entryUntil < Now || Now < _entryNextTry) return;
            _entryNextTry = Now + 3;
            Features.UseObject(_ddo, _entryDoor, false, false, CharName);
        }

        // =====================================================================
        //  리더 신호: 탈것 / 리콜 / 대상 선택
        // =====================================================================
        private void LeaderSignals(ulong leader, bool paused)
        {
            if (!Shared.TryReadChar(leader, out LeaderInfo li)) return;

            if (_seenMount == int.MinValue)
            {
                _seenMount = li.MountSeq; _seenRecall = li.RecallSeq;
                _seenPortal = li.PortalSeq; _seenTele = li.TeleSeq; _seenDialog = li.DialogSeq; _seenReward = li.RewardSeq; _seenTalk = li.TalkSeq; _seenEntry = li.EntrySeq;
            }

            if (li.MountSeq != _seenMount)
            {
                _seenMount = li.MountSeq;
                if (!paused && Settings.MountWithLeader) DoMount();
            }
            // 리더가 탈것에 타고 있는 동안, 내가 내려 있으면 6초마다 다시 탑승 시도 (전투 등으로 실패하거나 내려진 경우)
            if (li.Mounted)
            {
                if (!paused && Settings.MountWithLeader && Now >= _nextMountRetry && !IsMounted() && IsAliveNow() && !AutoActionsBlocked)
                {
                    _nextMountRetry = Now + 6;
                    if (!_mountRetryLogged) { _mountRetryLogged = true; Plugin.Log.Info($"[TrailFollow] {CharName}: 리더가 탈것에 타고 있음 - 탑승될 때까지 다시 시도"); }
                    DoMount(quiet: true);
                }
            }
            else _mountRetryLogged = false;
            if (li.RecallSeq != _seenRecall)
            {
                _seenRecall = li.RecallSeq;
                if (Settings.RecallWithLeader) DoRecall();   // v0.8.2: 리콜은 무엇보다 우선 (직접 조작 중이어도)
            }
            // 리더의 던전 입장창이 열림 → 서브도 같은 입구를 사용해서 입장창을 엶 (멀리 있으면 가까워질 때까지 3초마다 다시 시도)
            if (li.EntrySeq != _seenEntry)
            {
                _seenEntry = li.EntrySeq;
                if (!paused && Settings.EnterWithLeader)
                {
                    _entryDoor = li.EntryTarget != 0 ? li.EntryTarget : li.Target;
                    _entryUntil = Now + 60;
                    _entryNextTry = Now + 1.5;   // Ctrl+사용 따라 하기로 이미 사용했을 수 있어 잠시 기다린 뒤 확인
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 리더의 던전 입장창이 열림 - 같은 입구 사용 시도");
                }
            }
            if (li.PortalSeq != _seenPortal)
            {
                _seenPortal = li.PortalSeq;
                if (!paused && Settings.EnterWithLeader)
                {
                    if (Features.IsQuestEntryOpen(_ddo)) Features.EnterWithLeader(_ddo, CharName);
                    else
                    {
                        // 아직 내 입장창이 안 열렸으면, 열릴 때까지 입구 사용을 계속 시도하고 열리면 Enter
                        _enterPendingUntil = Now + 60;
                        if (_entryUntil < Now) { _entryDoor = li.EntryTarget != 0 ? li.EntryTarget : li.Target; _entryUntil = Now + 60; _entryNextTry = Now; }
                        Plugin.Log.Info($"[TrailFollow] {CharName}: 리더가 입장 - 내 입장창이 아직 없어 입구부터 사용");
                    }
                }
            }
            RunEntryAttempts(paused);
            if (li.TeleSeq != _seenTele)
            {
                _seenTele = li.TeleSeq;
                if (!paused && Settings.MimicTeleportItems) DoTeleportItem(li.TeleDid);
            }
            if (li.DialogSeq != _seenDialog)
            {
                _seenDialog = li.DialogSeq;
                if (!paused && Settings.MimicQuestDialog)
                {
                    // 서브 대화창이 닫혀 있으면 리더가 선택해 둔 NPC에게 먼저 말을 건 뒤 선택
                    ulong npc = li.Target != 0 ? li.Target : li.TalkTarget;
                    if (li.DialogOpt != DialogCloseSignal && !Features.IsNpcDialogOpen(_ddo) && npc != 0 && npc != (CharId ?? 0))
                    {
                        Plugin.Log.Info($"[TrailFollow] {CharName}: 대화창이 닫혀 있어 NPC에게 다시 말 걸기");
                        Features.UseObject(_ddo, npc, true, false, CharName);
                    }
                    if (li.DialogOpt == DialogCloseSignal) Features.CloseNpcDialog(_ddo, CharName);
                    else Features.ChooseDialogOption(_ddo, li.DialogOpt, CharName);
                }
            }
            if (li.TalkSeq != _seenTalk)
            {
                _seenTalk = li.TalkSeq;
                if (!paused && Settings.MimicCtrlUse && li.TalkTarget != 0)
                {
                    if (NotUsedRecently(li.TalkTarget))
                    {
                        Plugin.Log.Info($"[TrailFollow] {CharName}: 리더가 Ctrl+클릭한 대상(NPC·물체·상자) - 같이 사용");
                        Features.UseObject(_ddo, li.TalkTarget, true, false, CharName);
                    }
                }
            }
            if (li.RewardSeq != _seenReward)
            {
                _seenReward = li.RewardSeq;
                if (!paused && RewardMimicEnabled && Settings.MimicRewardChoice) TakeSameReward(li.RewardElem);
            }

            // Ctrl+사용 따라 하기
            long use = Interlocked.Exchange(ref _pendingUse, 0);
            long autoUse = Interlocked.Exchange(ref _pendingAutoUse, 0);
            if (use != 0 && !paused && Settings.MimicCtrlUse) { if (NotUsedRecently((ulong)use)) Features.UseObject(_ddo, (ulong)use, false, false, CharName)   /* v0.8.30: 함정은 항상 제외 */; }
            else if (autoUse != 0 && !paused && Settings.MimicDoorUse) Features.UseObject(_ddo, (ulong)autoUse, false, true, CharName);

            // 탐험 일지 자동 닫기 (1초마다 확인)
            if (!paused && Settings.CloseExplorerJournals && Now >= _nextJournalCheck)
            {
                _nextJournalCheck = Now + 1;
                Features.CloseExplorerJournal(_ddo);
            }
        }

        /// <summary>
        /// 대상 따라 하기 (리더 창 [타게팅 ON]). v0.8.13: 따라오기가 꺼져 있어도 동작
        /// (따라오기가 꺼져 있으면 지금 조작 중인 캐릭터를 리더로 보고, 이 창을 직접 조작 중이면 하지 않음)
        /// </summary>
        private void RunTargetMimic()
        {
            ulong leader = LockedLeader != 0 ? LockedLeader : Shared.FocusedCharId;
            bool blocked = IsMain || IsFocused || leader == 0 || leader == (CharId ?? 0) || _emergency || BuffTargetLocked;   // 비상 힐·버프 대상 고정 중에는 잠시 멈춤
            if (!Shared.TargetMimic || blocked || !Shared.TryReadChar(leader, out LeaderInfo li)) { _lastMimicTarget = 0; return; }
            ulong target = li.Target;
            if (target != 0 && target != _lastMimicTarget && target != (CharId ?? 0))
            {
                _lastMimicTarget = target;
                SelectInBackground(target);
            }
        }

        // v0.8.26: 대상 선택은 최대 0.75초(마우스를 누르고 있으면 뗄 때까지 기다림 + 커서 지켜보기)가 걸리고,
        //          서브 셋이 한 줄로 차례를 기다려서 그동안 따라가기 루프가 멈췄음 (전진키가 눌린 채 리더를 지나쳐 달리던 원인).
        //          이제 대상 선택은 루프 밖에서 하고, 루프는 멈추지 않음.
        private int _selGen;

        private void SelectInBackground(ulong id)
        {
            int gen = Interlocked.Increment(ref _selGen);
            var ddo = _ddo;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Features.SelectSafe(ddo, id, () => gen == Volatile.Read(ref _selGen)); }
                catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 대상 선택 실패", ex); }
            });
        }

        private double _nextMountRetry;
        private bool _mountRetryLogged;

        private void DoMount(bool quiet = false)
        {
            _nextMountRetry = Math.Max(_nextMountRetry, Now + 6);
            try
            {
                byte? mounted = _g.GetCurrentCharacter()?.PropertyCollection?.GetBytePropertyValue(PropMounted);
                if (mounted.GetValueOrDefault() != 0) return;   // 이미 탑승 중

                var map = _ddo.GetHotbarMap();
                var slot = map?.FirstOrDefault(s => s != null && s.ShortcutType == ShortcutTypeMount);
                if (slot == null) { if (!quiet) Plugin.Log.Info($"[TrailFollow] {CharName}: 핫바에 탈것이 없어 탑승 생략"); return; }

                // 이동을 멈추지 않고 달리면서 탑승 (v0.6.1)
                _autoHoldUntil = Now + 3;
                UseSlot(slot);
                if (!quiet) Plugin.Log.Info($"[TrailFollow] {CharName}: 리더를 따라 탈것 탑승");
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 탈것 탑승 실패", ex); }
        }

        private void DoTeleportItem(uint did)
        {
            try
            {
                var slot = _ddo.GetHotbarMap()?.FirstOrDefault(s => s != null && s.ItemIid > 0 && s.ReferenceDid == did);
                if (slot == null) { Plugin.Log.Info($"[TrailFollow] {CharName}: 핫바에 리더와 같은 순간이동 아이템이 없어 생략"); return; }
                StopMoving();
                _holdUntil = Now + 4;
                UseSlot(slot);
                Plugin.Log.Info($"[TrailFollow] {CharName}: 리더를 따라 순간이동 아이템 사용");
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 순간이동 아이템 사용 실패", ex); }
        }

        /// <summary>
        /// v0.8.17: 리더가 Ctrl을 누른 채 상자를 열면 서브들에게 알림 (상자는 "사용" 효과 신호가 안 와서 따라 하지 못했음)
        /// 대화(NPC)와 같은 알림 칸을 씀 → 서브는 같은 상자를 선택해서 사용
        /// </summary>
        public void OnChestOpened(ulong chestId)
        {
            if (chestId == 0 || Slot < 0) return;
            if (IsFocused)
            {
                if (!(Native.CtrlDown || Now - _lastCtrlAt < 3.0)) return;
                Shared.BumpTalk(Slot, chestId);
                Plugin.Log.Info($"[TrailFollow] {CharName}: Ctrl로 상자 열기 - 서브들에게 알림");
                return;
            }
            // 서브 쪽 (v0.8.18): 상자를 열면 내 몫을 리더에게 넘김
            var s = Settings;
            ulong leader = LockedLeader != 0 ? LockedLeader : Shared.FocusedCharId;
            if (IsMain || s == null || !s.PassLootToLeader || leader == 0 || leader == (CharId ?? 0)) return;
            Features.PassLootLater(_ddo, chestId, leader, CharName);
        }

        // 같은 대상을 두 경로(사용 효과 신호 + 알림)로 연달아 받으면 두 번 사용해서 상자가 다시 닫힐 수 있으므로 3초 안의 중복은 무시
        private ulong _lastUsedTarget;
        private double _lastUsedAt = -100;
        private bool NotUsedRecently(ulong target)
        {
            if (target == _lastUsedTarget && Now - _lastUsedAt < 3.0) return false;
            _lastUsedTarget = target; _lastUsedAt = Now;
            return true;
        }

        // ---- 진행 막대(리콜, 탈것 소환 등) 동안 자동 행동 멈춤 ----
        private volatile bool _progressActive;
        private double _progressSince;
        private double _autoHoldUntil;

        /// <summary>게임의 진행 막대 시작/끝 (Features에서 호출)</summary>
        public void OnProgress(bool started, string text)
        {
            if (started)
            {
                _progressSince = Now;
                _progressActive = true;
                if (_followOn || AutoTimersOn) Plugin.Log.Info($"[TrailFollow] {CharName}: 진행 중 ({text}) - 자동 행동 잠시 멈춤");
            }
            else
            {
                _progressActive = false;
                _autoHoldUntil = 0;
            }
        }

        /// <summary>자동 힐/버프, [버프] 순서, AFK 방지를 지금 미뤄야 하는지</summary>
        private bool AutoActionsBlocked =>
            (_progressActive && Now - _progressSince < 20) || Now < _autoHoldUntil;

        // 따라오기가 꺼진 서브도 메인(마지막으로 조작한 게임 창)의 리콜을 따라 함
        private ulong _idleLeader;
        private int _idleSeenRecall;

        private void IdleRecallCheck(ulong me)
        {
            // 리더(메인 창에서 따라오기를 켰거나 서브들이 고정했던 캐릭터)의 리콜만 따라 함 - 직접 조작 중인 다른 서브의 리콜은 무시
            ulong main = Shared.LastLeader;
            if (main == 0 || main == me || IsFocused || !Settings.RecallWithLeader) { _idleLeader = 0; return; }
            if (!Shared.TryReadChar(main, out LeaderInfo li)) return;
            if (main != _idleLeader) { _idleLeader = main; _idleSeenRecall = li.RecallSeq; return; }   // 처음 본 값은 기준으로만
            if (li.RecallSeq != _idleSeenRecall)
            {
                _idleSeenRecall = li.RecallSeq;
                Plugin.Log.Info($"[TrailFollow] {CharName}: 따라오기는 꺼져 있지만 리더({Shared.NameOf(main)})가 리콜해서 같이 리콜");
                DoRecall();
            }
        }

        // v0.8.2: 리콜은 최우선. 시전·전투 중이라 바로 못 누르면 이동·자동 행동을 모두 멈추고 1초마다 다시 시도 (최대 30초)
        private double _recallPendingUntil = -1, _recallNextTry;
        private int _recallTries;

        private void DoRecall()
        {
            _recallPendingUntil = Now + 30;
            _recallNextTry = Now;
            _recallTries = 0;
            RunRecallPending();
        }

        private void RunRecallPending()
        {
            if (_recallPendingUntil < 0) return;
            if (Now > _recallPendingUntil)
            {
                _recallPendingUntil = -1;
                Plugin.Log.Info($"[TrailFollow] {CharName}: 30초 동안 리콜하지 못함 - 포기");
                return;
            }
            StopMoving();
            _holdUntil = Math.Max(_holdUntil, Now + 1.5);
            _autoHoldUntil = Math.Max(_autoHoldUntil, Now + 15);   // 리콜 진행 막대 신호를 못 받아도 자동 행동 멈춤
            _buffIndex = -1;   // [버프] 순서 중이면 중단
            if (Now < _recallNextTry) return;
            _recallNextTry = Now + 1.0;
            if (Features.TryRecall(_ddo, CharName, quiet: _recallTries++ > 0))
            {
                _recallPendingUntil = -1;   // 눌렀음. 진행 막대가 시작되면 OnProgress에서 자동 행동을 계속 멈춤
                _holdUntil = Now + 4;
            }
        }

        // =====================================================================
        //  발자취 기록
        // =====================================================================
        private void RecordLeader(ILocation loc, Vector3 lp, Vector3 myP, bool paused)
        {
            Vector3? v = null;
            try { v = loc.Velocity; } catch { }
            if (v.HasValue) _leaderSpeed = (float)Math.Sqrt(v.Value.X * v.Value.X + v.Value.Y * v.Value.Y);

            // 리더가 실제로 점프했을 때만 (게임의 점프 동작 신호)
            bool jump = _leaderJumped;
            _leaderJumped = false;

            // 가까이 따라올 땐 리더와 동시에 점프
            if (jump && !paused && CurrentGap <= InstantJumpGap && Settings.ReplayJumps) Jump("리더와 동시 점프");
            bool markJump = jump && CurrentGap > InstantJumpGap;

            if (_trail.Count == 0)
            {
                AddPoint(lp, false);
                return;
            }

            float d = Vector3.Distance(lp, _trail[_trail.Count - 1].P);
            if (d > TeleportJump)
            {
                Plugin.Log.Info($"[TrailFollow] {CharName}: 리더 순간이동 감지 ({d:0}m) - 발자취 초기화");
                ResetTrail();
                AddPoint(lp, false);
                if (_followOn) { _waitTeleport = true; _waitMyPos = myP; _waitSince = Now; }
                return;
            }

            if (d >= RecordStep) AddPoint(lp, markJump);
            else if (markJump)
            {
                var t = _trail[_trail.Count - 1];
                t.Jump = true;
                _trail[_trail.Count - 1] = t;
            }

            if (_trail.Count > MaxTrail) Prune(_trail.Count - MaxTrail);
        }

        private const float FastSpeed = 16f;   // 초속 이 이상이면 기술 이동으로 봄 (일반 달리기·탈것보다 빠름)
        private double _lastPointAt = -1;

        private void AddPoint(Vector3 p, bool jump)
        {
            float d = _trail.Count == 0 ? 0f : Vector3.Distance(_trail[_trail.Count - 1].P, p);
            float arc = _trail.Count == 0 ? 0f : _trail[_trail.Count - 1].Arc + d;
            double dt = Now - _lastPointAt;
            // 위치가 잠깐 멈췄다가 한꺼번에 따라잡는 경우(일반 달리기)를 기술 이동으로 오인하지 않도록, 게임이 알려 주는 리더 속도가 평범한 달리기(2~14m/s)면 제외
            bool normalRun = _leaderSpeed >= 2f && _leaderSpeed <= 14f;
            bool fast = _trail.Count > 0 && _lastPointAt > 0 && dt > 0 && d > 1f && d / dt > FastSpeed && !normalRun;
            _lastPointAt = Now;
            _trail.Add(new TrailPoint { P = p, Arc = arc, Jump = jump, Fast = fast });
        }

        // 리더가 기술로 이동한 구간 앞에서 막힘 → 여러 방향으로 돌아가 보며 구간 끝으로 이동 시도, 다 실패하면 대기
        private bool _skillBlocked;
        private float _skillBlockEndArc;
        private Vector3 _skillEndPoint;
        private int _detourStep;
        private double _detourUntil, _detourNextJump;
        private Vector3 _detourWaypoint;

        private void StartDetour(float endArc)
        {
            _skillBlocked = true;
            _skillBlockEndArc = endArc;
            _skillEndPoint = PointAtArc(endArc);
            _detourStep = 0;
            _detourUntil = 0;
            _recoverStep = 0;
            _backtrackTo = null;
            _inPhase = false;
        }

        /// <summary>
        /// 기술 구간 넘어가기: ① 구간 끝으로 점프하며 직진 ② 왼쪽으로 6m 비켜났다가 구간 끝으로 ③ 오른쪽으로 비켜났다가 구간 끝으로
        /// ④ 리더 쪽으로 곧장. 단계마다 몇 초씩 시도. 모두 실패하면 false (대기)
        /// </summary>
        private bool RunDetour(Vector3 me, Vector3 leader)
        {
            if (_detourStep >= 6) { GiveUpHere(); return true; }
            if (Now >= _detourUntil)
            {
                _detourStep++;
                if (_detourStep >= 6) { GiveUpHere(); return true; }
                Vector3 dir = _skillEndPoint - me; dir.Z = 0;
                if (dir.LengthSquared() < 0.01f) dir = new Vector3(1, 0, 0);
                dir = Vector3.Normalize(dir);
                var leftV = new Vector3(-dir.Y, dir.X, 0);
                switch (_detourStep)
                {
                    case 1: _detourWaypoint = _skillEndPoint; _detourUntil = Now + 4; break;                 // 구간 끝으로 직진
                    case 2: _detourWaypoint = me + leftV * 6f; _detourUntil = Now + 2.5; break;             // 왼쪽으로 비켜나기
                    case 3: _detourWaypoint = _skillEndPoint; _detourUntil = Now + 4; break;
                    case 4: _detourWaypoint = me - leftV * 6f; _detourUntil = Now + 3.5; break;             // 오른쪽으로 비켜나기 (왼쪽에서 출발하므로 조금 더 길게)
                    case 5: _detourWaypoint = _skillEndPoint; _detourUntil = Now + 4; break;
                }
                Plugin.Log.Info($"[TrailFollow] {CharName}: 기술 이동 구간 넘어가기 시도 {(_detourStep + 1) / 2}/3");
            }
            FaceTowards(me, _detourWaypoint);
            _moving = true;
            SetFlags(true, false, false, false);
            bool towardEnd = _detourStep == 1 || _detourStep == 3 || _detourStep == 5;
            if (towardEnd && Now >= _detourNextJump) { _detourNextJump = Now + 1.2; Jump("기술 구간 넘어가기"); }
            if (!towardEnd && Dist2D(me, _detourWaypoint) < 0.8f) _detourUntil = Now;   // 비켜나기 지점에 도착하면 바로 다음 단계
            SetStatus(Lang.T($"기술 이동 구간 넘어가는 중 ({(_detourStep + 1) / 2}/3)", $"Crossing a skill-movement section ({(_detourStep + 1) / 2}/3)"));
            return true;
        }

        /// <summary>내 앞 몇 m 안에 기술 이동 구간이 있으면 그 구간이 끝나는 진행 거리를 돌려줌</summary>
        private bool FastSegmentAhead(float within, out float endArc)
        {
            endArc = 0;
            int i = Math.Max(1, _seg);
            while (i < _trail.Count && _trail[i].Arc <= _prog + within && !_trail[i].Fast) i++;
            if (i >= _trail.Count || !_trail[i].Fast) return false;
            while (i + 1 < _trail.Count && _trail[i + 1].Fast) i++;   // 이어진 기술 구간의 끝까지
            endArc = _trail[i].Arc;
            return true;
        }

        private void Prune(int count)
        {
            count = Math.Min(count, Math.Max(0, _seg - 2));   // 내가 있는 구간 뒤쪽만 지움
            if (count <= 0) return;
            _trail.RemoveRange(0, count);
            _seg -= count;
        }

        private void ResetTrail()
        {
            _trail.Clear();
            _prog = 0; _seg = 0; _lateral = 0;
            _tightUntil = 0; _jumpCheckedArc = 0;
            _lastOnPathArc = 0;
            _hasLastOnPath = false;
            _waitTeleport = false;
            _backtrackTo = null;
            _skillBlocked = false;
        }

        /// <summary>선 전체에서 나와 가장 가까운 지점에 합류 (따라오기 시작/재개 시)</summary>
        private void JoinTrail(Vector3 me)
        {
            _recoverStep = 0;
            _gaveUp = false;
            _skillBlocked = false;
            _backtrackTo = null;
            _inPhase = false;
            SnapToNearest(me);
        }

        /// <summary>발자취에서 지금 위치와 가장 가까운 지점으로 진행 위치를 맞춤 (뒤쪽 지점일 수도 있음)</summary>
        private bool _heldByStraight;

        private void SnapToNearest(Vector3 me, bool preferRecent = false)
        {
            if (_trail.Count < 2) { _prog = 0; _seg = 0; return; }

            float bestD = float.MaxValue, bestArc = 0; int bestSeg = 0;
            float tol = preferRecent ? 2.0f : 0f;   // preferRecent: 2m 이내로 비슷하면 더 최근 구간을 고름
            for (int i = 0; i < _trail.Count - 1; i++)
            {
                Project(me, i, out float arc, out float lat, out float dz, out _);
                float score = lat + dz * 0.5f;
                if (score <= bestD + tol) { bestD = Math.Min(bestD, score); bestArc = arc; bestSeg = i; }   // 같으면 더 앞쪽(최근) 우선
            }
            _prog = bestArc; _seg = bestSeg;
            _jumpCheckedArc = _prog;
            _lastOnPathArc = _prog;
            _lastOnPath = PointAtArc(_prog);
            _hasLastOnPath = true;
        }

        // =====================================================================
        //  선 위 진행 계산
        // =====================================================================
        /// <summary>구간 i 위로 me를 수직 투영</summary>
        private void Project(Vector3 me, int i, out float arc, out float lateral, out float dz, out Vector3 point)
        {
            Vector3 a = _trail[i].P, b = _trail[i + 1].P;
            float abx = b.X - a.X, aby = b.Y - a.Y;
            float len2 = abx * abx + aby * aby;
            float t = len2 < 1e-6f ? 0f : Math.Clamp(((me.X - a.X) * abx + (me.Y - a.Y) * aby) / len2, 0f, 1f);
            point = Vector3.Lerp(a, b, t);
            lateral = Dist2D(me, point);
            dz = Math.Abs(me.Z - point.Z);
            arc = _trail[i].Arc + (_trail[i + 1].Arc - _trail[i].Arc) * t;
        }

        private void UpdateProgress(Vector3 me)
        {
            if (_trail.Count < 2)
            {
                _lateral = _trail.Count == 1 ? Dist2D(me, _trail[0].P) : 0f;
                return;
            }
            _seg = Math.Clamp(_seg, 0, _trail.Count - 2);

            // v0.8.23: 트랙 인식 범위 기능 끄면 기본 3.0m 고정
            var trSet = Settings;
            float projLat = (trSet != null && trSet.TrackRangeEnabled) ? trSet.TrackRange : ProjectLateral;
            float onPath = projLat * (OnPathLateral / ProjectLateral); // 비례 축소
            float shortcut = projLat * (ShortcutLateral / ProjectLateral);

            // 1) 지금 구간부터 앞쪽 몇 구간 중 가장 가까운 곳 (뒤로는 안 감)
            int to = Math.Min(_trail.Count - 2, _seg + 12);
            float bestLat = float.MaxValue, bestArc = _prog; int bestSeg = _seg;
            for (int i = _seg; i <= to; i++)
            {
                Project(me, i, out float arc, out float lat, out float dz, out _);
                if (arc < _prog - 0.01f || dz > 3f) continue;
                if (lat < bestLat - 0.05f || (Math.Abs(lat - bestLat) <= 0.05f && arc > bestArc))
                { bestLat = lat; bestArc = arc; bestSeg = i; }
            }
            if (bestLat < projLat) { _prog = Math.Max(_prog, bestArc); _seg = bestSeg; }

            // 2) 지름길: 선이 나중에 다시 내 곁을 지나가면 거기로 건너뜀
            for (int i = _trail.Count - 2; i > to; i--)
            {
                Project(me, i, out float arc, out float lat, out float dz, out _);
                if (lat < shortcut && dz < 1.5f && arc > _prog) { _prog = arc; _seg = i; break; }
            }

            // 3) 선에서 떨어진 거리 / 마지막 선 위 위치 기록
            Project(me, _seg, out _, out float curLat, out _, out _);
            _lateral = curLat;
            if (_lateral < onPath) { _lastOnPath = me; _lastOnPathArc = _prog; _hasLastOnPath = true; }
        }

        private Vector3 PointAtArc(float arc)
        {
            if (_trail.Count == 0) return Vector3.Zero;
            if (arc <= _trail[0].Arc) return _trail[0].P;
            int i = Math.Clamp(_seg, 0, Math.Max(0, _trail.Count - 2));
            while (i > 0 && _trail[i].Arc > arc) i--;
            while (i < _trail.Count - 1 && _trail[i + 1].Arc < arc) i++;
            if (i >= _trail.Count - 1) return _trail[_trail.Count - 1].P;
            float seg = _trail[i + 1].Arc - _trail[i].Arc;
            float t = seg < 1e-5f ? 0f : (arc - _trail[i].Arc) / seg;
            return Vector3.Lerp(_trail[i].P, _trail[i + 1].P, Math.Clamp(t, 0f, 1f));
        }

        private float EndArc => _trail.Count == 0 ? 0f : _trail[_trail.Count - 1].Arc;

        // =====================================================================
        //  이동 / 조향
        // =====================================================================
        private void Steer(ILocation myLoc, Vector3 myP, Vector3 lp)
        {
            float gap = CurrentGap;
            UpdateProgress(myP);

            // 지나온 점프 지점에서 점프 (멀리 따라올 때만)
            if (Settings.ReplayJumps && gap > InstantJumpGap)
            {
                for (int i = Math.Max(0, _seg - 3); i < _trail.Count && _trail[i].Arc <= _prog; i++)
                    if (_trail[i].Jump && _trail[i].Arc > _jumpCheckedArc) { Jump("리더가 점프한 자리"); break; }
            }
            _jumpCheckedArc = Math.Max(_jumpCheckedArc, _prog);

            // v0.8.23: 서브가 지나간 트레일을 즉시 제거 (빵 부스러기를 줏어먹음)
            //          같은 맵을 왔다갔다할 때 옛 경로를 따라가는 문제 방지
            if (_seg > 2) Prune(_seg - 1);

            // 남은 경로 거리 = 선까지 + 선을 따라 끝까지 + 끝에서 리더까지
            float tail = _trail.Count > 0 ? Vector3.Distance(_trail[_trail.Count - 1].P, lp) : Vector3.Distance(myP, lp);
            float remaining = _lateral + Math.Max(0, EndArc - _prog) + tail;

            _lastLeaderDist = Dist2D(myP, lp);
            _catchingUp = remaining > gap + CatchUpMargin && _lastLeaderDist > gap + CatchUpMargin * 0.5f;   // v0.8.8: 직선으로 가까우면(싸우며 오가는 중) 뒤처진 것으로 보지 않음   // 많이 뒤처져 있으면 타이머를 미루고 이동부터

            // v0.7.8: 탈출 중·대기 중이라도 리더와 같은 자리(가까이, 같은 높이)에 오면 탈출을 끝내고 바로 평소처럼 따라감
            // v0.8.5: 리더가 10m 안(같은 높이)이면 발자취·탈출 동작 없이 리더 쪽으로 곧장 따라감
            bool troubled = _recoverStep > 0 || _skillBlocked || _gaveUp || _inPhase || _backtrackTo != null;
            // v0.8.7: 발자취로 가는 길이 직선보다 훨씬 길면(벽을 돌아가는 길) 곧장 가지 않음.
            //         또 발자취를 지우지 않음 - 예전에는 지워서, 리더가 다시 멀어지면 벽 너머의 새 발자취로 곧장 가려다 막혔음
            // v0.8.21: 전투 중 리더가 왔다갔다하면 트랙이 부풀어 straightOk가 거짓이 됨.
            //          리더가 아주 가까우면(5m 이내) 무조건 곧장 가고, 10m 이내면 배수를 느슨하게 함.
            bool straightOk = _trail.Count < 2 || remaining <= _lastLeaderDist * 1.4f + 3f;
            bool veryClose = _lastLeaderDist <= NearLeaderDist * 0.5f;                       // 5m 이내
            bool combatRelaxed = remaining <= _lastLeaderDist * 4f + 10f;                     // 전투 왔다갔다(3-4배 부풀림) 허용
            bool wasNear = _nearMode;
            _nearMode = _lastLeaderDist <= NearLeaderDist && Math.Abs(myP.Z - lp.Z) < 3f
                        && (straightOk || veryClose || combatRelaxed);
            if (_nearMode)
            {
                if (troubled)
                {
                    _recoverStep = 0; _skillBlocked = false; _gaveUp = false; _inPhase = false; _backtrackTo = null; _phaseJumpAt = -1;
                    _hist.Clear(); ResetStuckWindow();
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 리더가 {NearLeaderDist:0}m 안 - 탈출 끝내고 리더를 곧장 따라감");
                }
                remaining = _lastLeaderDist;
            }
            else if (wasNear && _trail.Count >= 2)
            {
                SnapToNearest(myP);   // 곧장 가기를 끝내면 발자취에서 가장 가까운 지점부터 다시 따라감
                ResetStuckWindow();
            }

            // v0.8.5: 리더와 멀리(기본 30m) 떨어지면 지정한 핫바 칸(기본 3.1)을 누름 (20초마다 반복)
            var st5 = Settings;
            if (st5 != null && st5.FarHotkeyEnabled)
            {
                if (_lastLeaderDist >= st5.FarHotkeyDistance && Now >= _farHotkeyNext)
                {
                    _farHotkeyNext = Now + 20;
                    var fs = ParseSlots(st5.FarHotkeySlot);
                    if (fs.Count > 0)
                    {
                        FireSlot(fs[0].bar, fs[0].slot, out string fm);
                        Plugin.Log.Info($"[TrailFollow] {CharName}: 리더와 {_lastLeaderDist:0}m 떨어짐 - {fm}");
                    }
                }
                else if (_lastLeaderDist < st5.FarHotkeyDistance - 5) _farHotkeyNext = 0;   // 다시 가까워지면, 다음에 멀어질 때 바로 누름
            }

            if (_skillBlocked)
            {
                // 기술 구간을 지나면(진행 거리가 넘어서거나 구간 끝 근처에 도착) 다시 따라감
                if (_prog > _skillBlockEndArc || Dist2D(myP, _skillEndPoint) < 2.5f)
                {
                    _prog = Math.Max(_prog, _skillBlockEndArc);
                    while (_seg < _trail.Count - 2 && _trail[_seg + 1].Arc <= _prog) _seg++;
                    _skillBlocked = false; _recoverStep = 0; _hist.Clear(); ResetStuckWindow();
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 기술 이동 구간을 넘어서 다시 따라감");
                }
                else if (RunDetour(myP, lp)) return;
                else { StopMoving(); SetStatus(Lang.T("리더가 기술로 이동한 구간 - 직접 넘겨 주세요", "The leader crossed this with a skill - move this alt across yourself")); return; }
            }

            if (_gaveUp)
            {
                // 리더가 가까이 오면: 지나온 발자취는 버리고 리더 쪽으로 곧장 간 뒤, 거기서부터 새 발자취를 따라감
                // 이미 가까운 곳에서 막혔으면 리더가 3m 이상 더 다가와야 다시 시도 (같은 곳에서 계속 반복하지 않게)
                float dl = Dist2D(myP, lp);
                if (dl < RejoinDistance && (_gaveUpDist >= RejoinDistance || dl < _gaveUpDist - 3f))
                {
                    _gaveUp = false; _recoverStep = 0;
                    ResetTrail();
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 리더가 가까이 옴 - 리더 쪽으로 곧장 이동해서 다시 따라감");
                }
                else { StopMoving(); SetStatus(Lang.T($"막힘 - 리더가 {RejoinDistance:0}m 안으로 오면 다시 따라감", $"Blocked - will follow again when the leader is within {RejoinDistance:0} m")); return; }
            }

            // 출발/정지 (경로상 거리 기준, 관성만큼 일찍 멈춤)
            float mySpeed = 0;
            try
            {
                var mv = myLoc.Velocity;
                if (mv.HasValue) mySpeed = (float)Math.Sqrt(mv.Value.X * mv.Value.X + mv.Value.Y * mv.Value.Y);
            }
            catch { }
            // v0.8.27: 진단 기록으로 잰 값 - 이동키를 뗀 뒤 약 0.08초 더 미끄러짐(속도 14면 1.1m). 판단 간격(0.05초)의 절반을 더해
            //          멈춘 자리가 목표 거리의 앞뒤로 고르게 오도록 함
            float overshoot = Math.Min(1.6f, mySpeed * 0.105f);
            _dbgRemaining = remaining;
            // v0.8.23: NearMode이면 리더의 작은 움직임에 반응하지 않음 (앞지르기·왔다갔다 방지)
            //          공격 ON이면 추가 여유를 둠 (옆걸음이 부정확하므로)
            // v0.8.27: 앞지르기의 진짜 원인(루프 멈춤)을 고쳤으므로, 추측으로 넣었던 여유 거리(2m)는 뺌 - 서브가 리더에게서 떨어져 서 있던 원인
            //          거리 0~0.5m에서는 0.8m 넘게 벌어져야 다시 다가감 (더 작게 하면 리더 자리를 사이에 두고 왔다갔다함)
            float hysteresis = gap < 0.5f ? 0.8f : Math.Clamp(gap * 0.25f, 0.4f, 1.5f);

            // v0.8.8: 싸우면서 리더가 앞뒤로 움직이면 발자취(경로)는 계속 길어지지만 실제 거리는 가까움
            //         → 직선거리도 설정 거리 안이면(같은 높이) 멈춰서 정한 거리를 유지
            bool sameLevel = Math.Abs(myP.Z - lp.Z) < 3f;
            bool straightClose = gap >= 3f && sameLevel && _lastLeaderDist <= gap + overshoot;
            // v0.8.23: 스킬 시전 직후에는 이동을 멈춤 (시전 애니메이션 중 이동이 겹치면 과주행)
            if (Now < _castMoveHold)
            {
                if (_moving) StopMoving();
            }
            else if (_moving)
            {
                if (_backtrackTo == null && (remaining <= gap + overshoot || (straightClose && !_inPhase)))
                {
                    if (remaining > gap + overshoot) _heldByStraight = true;
                    StopMoving();
                }
            }
            else if (remaining > gap + hysteresis && !(gap >= 3f && sameLevel && _lastLeaderDist <= gap + hysteresis))
            {
                if (_heldByStraight && _trail.Count >= 2)
                {
                    SnapToNearest(myP, preferRecent: true);   // 리더가 오간 길(되돌아간 부분)은 건너뛰고 가장 최근 발자취부터
                    remaining = _lateral + Math.Max(0, EndArc - _prog) + tail;
                }
                _heldByStraight = false;
                StartMoving();
            }

            string modeText = Shared.FarMode ? Lang.T("멀리", "Far") : Lang.T("가까이", "Near");
            if (!_moving)
            {
                bool facing = FaceSelectedMonster(myP);
                SetStatus(Lang.T($"대기 · {modeText} {gap:0.#}m · 경로 {remaining:0.#}m{(facing ? " · 대상 바라봄" : "")}", $"Idle · {modeText} {gap:0.#}m · path {remaining:0.#}m{(facing ? " · facing target" : "")}"));
                return;
            }

            // ---- 걸림 탈출: 되돌아가기 ----
            if (_backtrackTo.HasValue)
            {
                Vector3 bt = _backtrackTo.Value;
                if (Dist2D(myP, bt) < 0.6f || Now > _backtrackUntil)
                {
                    _backtrackTo = null;
                    _tightUntil = _prog + 4f;
                    ResetStuckWindow();
                }
                else
                {
                    FaceTowards(myP, bt);
                    SetFlags(true, false, false, false);
                    SetStatus(Lang.T($"탈출: 되돌아가는 중 ({_recoverStep}단계)", $"Unstuck: backing up (step {_recoverStep})"));
                    return;
                }
            }

            // ---- 조향: 선 위에서 조금 앞 지점을 바라봄 ----
            float look = _prog < _tightUntil ? TightLookahead : Lookahead;
            if (_lateral > 1.2f) look = 0.6f;   // 선에서 벗어났으면 가까운 지점으로 복귀
            Vector3 carrot = (!_nearMode && _trail.Count >= 2 && _prog + look < EndArc) ? PointAtArc(_prog + look) : lp;
            // v0.8.25: 목표 지점이 내 발밑(수평 1m 안)이면 방향이 매번 반대로 뒤집혀 제자리에서 왔다갔다함 (리더가 위아래로 이동한 구간)
            //          → 선을 따라 더 앞의 지점을 보고, 그것도 없으면 리더 쪽, 그것도 발밑이면 지금 방향을 유지
            bool carrotUnder = false;
            if (Dist2D(myP, carrot) < 1.0f)
            {
                carrotUnder = true;
                if (!_nearMode && _trail.Count >= 2)
                    for (float extra = 1f; extra <= 8f && _prog + look + extra < EndArc; extra += 1f)
                    {
                        Vector3 c2 = PointAtArc(_prog + look + extra);
                        if (Dist2D(myP, c2) >= 1.0f) { carrot = c2; carrotUnder = false; break; }
                    }
                if (carrotUnder && Dist2D(myP, lp) >= 1.0f) { carrot = lp; carrotUnder = false; }
            }
            // v0.8.19: 공격 ON이고 근처에 대상 몬스터가 있으면, 이동 중에도 몬스터를 바라보며 옆걸음·뒷걸음으로 길을 따라감
            //          (많이 뒤처져 따라잡는 중이거나 걸림 탈출 중에는 평소처럼 앞으로 달림)
            Vector3? combatFace = (!_inPhase && !_catchingUp) ? CombatTargetPos(myP) : null;
            if (combatFace.HasValue != _combatMoving)
            {
                _combatMoving = combatFace.HasValue;
                if (_combatMoving) Plugin.Log.Info($"[TrailFollow] {CharName}: 공격 ON - 적을 바라보며 이동");
            }
            if (!carrotUnder || combatFace.HasValue) FaceTowards(myP, combatFace ?? carrot);

            if (_inPhase)
            {
                if (_phaseJumpAt > 0 && Now >= _phaseJumpAt) { _phaseJumpAt = -1; Jump($"걸림 탈출 {_recoverStep}단계"); }
                if (Now >= _phaseEnd)
                {
                    _inPhase = false;
                    ResetStuckWindow();
                    SnapToNearest(myP);   // 발자취에서 가장 가까운 지점으로 돌아가서 다시 따라감
                }
            }
            else
            {
                // 항상 달리기 (SDK의 걷기 전진은 실제로 움직이지 않는 것으로 보여 사용하지 않음)
                if (combatFace.HasValue) MoveWhileFacing(myP, carrot, combatFace.Value);
                else SetFlags(true, false, false, false);
                // 목표 거리 근처에서는 느려지는 게 정상이므로 걸림 판단을 하지 않음
                if (!_nearMode && remaining - gap > StuckIgnoreZone && Now >= _castGraceUntil) StuckCheck(lp, remaining);
                else ResetStuckWindow();
            }

            string rec = _recoverStep > 0 ? Lang.T($" · 탈출 {_recoverStep}단계", $" · unstuck step {_recoverStep}") : "";
            SetStatus(Lang.T($"이동 · {modeText} {gap:0.#}m · 경로 {remaining:0.#}m{rec}", $"Moving · {modeText} {gap:0.#}m · path {remaining:0.#}m{rec}"));
        }

        /// <summary>
        /// v0.8.9: 멈춰 있을 때, 선택한 대상이 근처(오토 힐/버프의 "근처" 거리)에 있는 살아 있는 몬스터면 정면으로 바라봄 (자동 공격이 빗나가지 않게)
        /// </summary>
        private double _faceCheckAt;
        private Vector3? _faceTargetPos;
        private ulong _faceId;
        private string _faceReason;

        /// <summary>id가 근처(오토 힐/버프 기준 거리)의 살아 있는 몬스터면 위치를 돌려줌. 아니면 reason에 이유</summary>
        private Vector3? NearbyMonsterPos(ulong id, Vector3 myP, float radius, out string name, out string reason)
        {
            name = null; reason = null;
            if (id == 0 || id == (CharId ?? 0)) { reason = "대상 없음"; return null; }
            IEntity e = null;
            try { e = _g.GetEntity(id); } catch { }
            if (e == null) try { e = (_g.GetEntities() ?? Array.Empty<IEntity>()).FirstOrDefault(x => x != null && x.InstanceId == id); } catch { }
            if (e == null || e.IsDeleted) { reason = "대상 정보를 읽을 수 없음"; return null; }
            name = e.Name;
            if (!IsMonster(e)) { reason = $"{name}: 몬스터가 아님"; return null; }
            uint? state = null;
            try { state = e.PropertyCollection?.GetEnumProperty(PropCombatState)?.UInt32Value; } catch { }
            int? hp = e.HitPoints_Current;
            if (state == 2 || (hp.HasValue && hp.Value <= 0)) { reason = $"{name}: 죽음"; return null; }
            var loc = e.Location;
            if (loc == null) { reason = $"{name}: 위치를 읽을 수 없음"; return null; }
            Vector3 p = Global(loc);
            float d = Dist2D(p, myP);
            if (d > radius || Math.Abs(p.Z - myP.Z) >= 12f) { reason = $"{name}: 멀리 있음 ({d:0}m)"; return null; }
            return p;
        }

        /// <summary>
        /// v0.8.9: 멈춰 있을 때, 근처(오토 힐/버프의 "근처" 거리)의 살아 있는 대상 몬스터를 정면으로 바라봄 (자동 공격이 빗나가지 않게)
        /// </summary>
        private bool _combatMoving;

        /// <summary>
        /// 바라보는 방향(적 쪽)은 그대로 두고, 가야 할 방향(carrot)에 맞춰 앞/뒤/옆걸음 조합을 고름 (8방향)
        /// </summary>
        private void MoveWhileFacing(Vector3 myP, Vector3 carrot, Vector3 face)
        {
            double moveH = Math.PI / 2 - Math.Atan2(carrot.Y - myP.Y, carrot.X - myP.X);
            double faceH = Math.PI / 2 - Math.Atan2(face.Y - myP.Y, face.X - myP.X);
            double rel = moveH - faceH;   // + 이면 가야 할 쪽이 바라보는 쪽의 오른쪽(시계 방향)
            while (rel > Math.PI) rel -= 2 * Math.PI;
            while (rel <= -Math.PI) rel += 2 * Math.PI;
            int sector = (int)Math.Round(rel / (Math.PI / 4));   // -4 ~ 4
            switch (sector)
            {
                case 0: SetFlags(true, false, false, false); break;
                case 1: SetFlags(true, false, false, true); break;
                case 2: SetFlags(false, false, false, true); break;
                case 3: SetFlags(false, true, false, true); break;
                case -1: SetFlags(true, false, true, false); break;
                case -2: SetFlags(false, false, true, false); break;
                case -3: SetFlags(false, true, true, false); break;
                default: SetFlags(false, true, false, false); break;   // ±4: 뒷걸음
            }
        }

        /// <summary>공격 ON이고, 선택한 대상이 근처의 살아 있는 몬스터면 그 위치 (아니면 null)</summary>
        private Vector3? CombatTargetPos(Vector3 myP)
        {
            var st = Settings;
            if (st == null || !st.FaceTarget || IsMain || !Shared.AttackOn) return null;
            UpdateCombatTarget(myP, st);
            return _faceTargetPos;
        }

        private bool FaceSelectedMonster(Vector3 myP)
        {
            var st = Settings;
            if (st == null || !st.FaceTarget || IsMain || !Shared.AttackOn)   // v0.8.11: 리더 창 [공격]이 켜져 있을 때만
            {
                _faceId = 0; _faceReason = null; _faceTargetPos = null;
                return false;
            }
            UpdateCombatTarget(myP, st);
            if (_faceTargetPos == null) return false;
            FaceTowards(myP, _faceTargetPos.Value);
            return true;
        }

        /// <summary>0.25초마다 내 대상이 근처의 살아 있는 몬스터인지 확인해서 _faceTargetPos 갱신 (바뀔 때 로그)</summary>
        private void UpdateCombatTarget(Vector3 myP, CharSettings st)
        {
            if (Now < _faceCheckAt) return;
            _faceCheckAt = Now + 0.25;
            _faceTargetPos = null;
            ulong useId = 0; string name = null, reason = null;
            try
            {
                float radius = Math.Clamp(st.EnemyRadiusM, 5, 100);
                ulong sel = _g.GetSelectedObjectId() ?? 0;
                _faceTargetPos = NearbyMonsterPos(sel, myP, radius, out name, out reason);
                if (_faceTargetPos != null) useId = sel;
            }
            catch (Exception ex) { reason = "오류: " + ex.Message; }

            if (useId != 0 && useId != _faceId)
            {
                _faceId = useId; _faceReason = null;
                Plugin.Log.Info($"[TrailFollow] {CharName}: 공격 ON - {name ?? useId.ToString()} 정면으로 바라봄");
            }
            else if (useId == 0)
            {
                _faceId = 0;
                if (reason != _faceReason) { _faceReason = reason; Plugin.Log.Info($"[TrailFollow] {CharName}: 공격 ON - 바라볼 대상 없음 ({reason})"); }
            }
        }

        private void FaceTowards(Vector3 from, Vector3 to)
        {
            // v0.8.22: 리더(메인)의 방향은 절대 조작하지 않음
            if (IsMain) return;
            float dx = to.X - from.X, dy = to.Y - from.Y;
            if (dx * dx + dy * dy < 0.0025f) return;
            double desired = Normalize(Math.PI / 2 - Math.Atan2(dy, dx));   // Awesome AF와 같은 변환
            bool changed = double.IsNaN(_lastHeadingValue) || AngleDiff(desired, _lastHeadingValue) > 0.03;
            // v0.8.24: 이동 중에는 0.1초마다 방향을 다시 지정 (게임이 시전·공격으로 캐릭터를 돌려 놓아도 엉뚱한 쪽으로 달리지 않게)
            if (changed || Now - _lastHeadingSet > (_moving ? 0.1 : 0.5))
            {
                try { _g.SetCharacterHeading(desired); } catch { }
                _lastHeadingValue = desired;
                _lastHeadingSet = Now;
            }
        }

        private void StartMoving()
        {
            _moving = true;
            ResetStuckWindow();
        }

        private void StopMoving()
        {
            _moving = false;
            _inPhase = false;
            _backtrackTo = null;
            _hist.Clear();
            SafeRelease();
        }

        // =====================================================================
        //  걸림 감지 / 탈출
        //  "움직였나"가 아니라 "선을 따라 앞으로 나아가고 있나"로 판단한다.
        //  벽에 붙어 옆으로 미끄러지기만 하면 걸림으로 본다.
        // =====================================================================
        private float EffectiveProgress => _prog - _lateral;

        private void ResetStuckWindow()
        {
            _hist.Clear();
            _movingSince = Now;
        }

        private float _lastRemaining;

        private void StuckCheck(Vector3 lp, float remaining)
        {
            float eff = EffectiveProgress;
            _lastRemaining = remaining;
            _hist.Enqueue((Now, eff));
            while (_hist.Count > 0 && Now - _hist.Peek().t > 1.5) _hist.Dequeue();

            // 마지막 탈출 시도 이후 3m 이상 나아갔으면 탈출 단계 초기화
            if (_recoverStep > 0 && eff - _effAtRecovery > 3f) _recoverStep = 0;

            if (Now - _movingSince < 1.2 || _hist.Count == 0) return;
            var old = _hist.Peek();
            if (Now - old.t < 1.2) return;
            if (eff - old.eff < 0.4f) BeginRecovery(lp);
        }

        private void BeginRecovery(Vector3 lp)
        {
            if (FastSegmentAhead(4f, out float fastEnd))
            {
                StartDetour(fastEnd);
                Plugin.Log.Info($"[TrailFollow] {CharName}: 리더가 기술로 빠르게 이동한 구간 앞에서 막힘 (진행 {_prog:0.0}m → {fastEnd:0.0}m) - 돌아서 넘어가기 시도");
                return;
            }

            // v0.8.21: 높은 트랙 구간으로 막힌 경우 - stuck일 때만 건너뜀 (UpdateProgress에서 매 틱 실행하지 않음)
            {
                Vector3? mePos = MyPos();
                if (mePos == null) return;
                Vector3 me = mePos.Value;
                int to = Math.Min(_trail.Count - 2, _seg + 12);
                for (int i = _seg; i <= to; i++)
                {
                    Project(me, i, out float arc, out float lat, out _, out Vector3 pt);
                    if (arc < _prog - 0.01f) continue;
                    float upDz = pt.Z - me.Z;
                    float trLat = (Settings != null && Settings.TrackRangeEnabled) ? Settings.TrackRange : ProjectLateral;
                    if (upDz > 5f && lat < trLat * 2f)
                    {
                        // 높은 구간 너머에서 높이가 비슷해지는 지점 찾기
                        for (int j = i + 1; j < _trail.Count; j++)
                        {
                            float jdz = _trail[j].P.Z - me.Z;
                            if (jdz <= 3f || j == _trail.Count - 1)
                            {
                                float newProg = _trail[Math.Min(j, _trail.Count - 1)].Arc;
                                _prog = Math.Max(_prog, newProg);
                                _seg = Math.Min(j, _trail.Count - 2);
                                Jump("높은 트랙 구간 건너뛰기 (stuck)");
                                Plugin.Log.Info($"[TrailFollow] {CharName}: stuck 중 높은 트랙 구간 (높이차 {upDz:0.0}m) 건너뜀 → 진행 {_prog:0.0}m");
                                ResetStuckWindow();
                                return;
                            }
                        }
                        break;
                    }
                }
            }

            _recoverStep++;
            _effAtRecovery = EffectiveProgress;
            bool canBacktrack = _hasLastOnPath && _trail.Count >= 2;
            Plugin.Log.Info($"[TrailFollow] {CharName}: 걸림 감지 (진행 {_prog:0.0}m, 선에서 {_lateral:0.0}m, 남은 경로 {_lastRemaining:0.0}m) - 탈출 {(_recoverStep - 1) / 4 + 1}바퀴 {(_recoverStep - 1) % 4 + 1}단계");

            // v0.8.5: 탈출 한 바퀴 = 움직이면서 점프 ① 앞 ② 왼쪽 ③ 오른쪽 ④ 뒤. 이걸 3바퀴(12단계)까지.
            //         단계가 끝날 때마다 발자취에서 가장 가까운 지점으로 돌아가서 다시 따라감.
            //         그래도 안 되면 그 자리에서 멈춰 있다가, 리더가 가까이 오면 리더 쪽으로 곧장 이동
            int stepInLoop = _recoverStep > RecoverLoops * 4 ? 0 : (_recoverStep - 1) % 4 + 1;
            switch (stepInLoop)
            {
                case 1: Phase(0.8, true, false, false, false, true); break;    // 앞으로 달리며 점프
                case 2: Phase(0.9, true, false, true, false, true); break;     // 왼쪽 앞으로 달리며 점프
                case 3: Phase(1.2, true, false, false, true, true); break;     // 오른쪽 앞으로 달리며 점프 (왼쪽으로 간 만큼 더)
                case 4: Phase(0.8, false, true, false, false, true); break;    // 뒤로 물러나며 점프
                default:
                    GiveUpHere();
                    break;
            }
            _hist.Clear();
        }

        private void Phase(double dur, bool f, bool b, bool l, bool r, bool jump)
        {
            SetFlags(f, b, l, r);
            _phaseJumpAt = jump ? Now + 0.2 : -1;   // 제자리 점프가 아니라 움직이기 시작한 뒤에 점프
            _inPhase = true;
            _phaseEnd = Now + dur;
        }
        private double _phaseJumpAt = -1;

        private void StartBacktrack(Vector3 to)
        {
            _backtrackTo = to;
            _backtrackUntil = Now + 3.5;
            _inPhase = false;
        }

        // =====================================================================
        //  입력
        // =====================================================================
        private void SetFlags(bool fwd, bool back, bool left, bool right, bool walk = false)
        {
            // v0.8.22: 리더(메인)의 이동은 절대 조작하지 않음
            if (IsMain) return;
            _inputOwned = fwd || back || left || right;
            try
            {
                bool run = fwd && !walk, walkFwd = fwd && walk;
                if (_ddo.RunningForward != run) _ddo.RunningForward = run;
                if (_ddo.WalkingForward != walkFwd) _ddo.WalkingForward = walkFwd;
                if (_ddo.RunningBackward != back) _ddo.RunningBackward = back;
                if (_ddo.StrafeLeft != left) _ddo.StrafeLeft = left;
                if (_ddo.StrafeRight != right) _ddo.StrafeRight = right;
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 이동 입력 실패", ex); }
        }

        // 우리가 누른 이동키만 뗀다 (사용자가 직접 조작할 때 키를 빼앗지 않음)
        private void SafeRelease()
        {
            if (!_inputOwned) return;
            try { SetFlags(false, false, false, false); } catch { }
        }

        /// <summary>
        /// 이 캐릭터의 게임 창에만 키 입력. v0.6.9: 게임의 키 보내기 기능은 지금 앞에 떠 있는 창(메인)에 키가 들어가는 경우가 있어서
        /// (서브가 걸림 탈출로 점프하면 메인도 점프), 이 창이 맨 앞이 아니면 이 창에만 직접 키 메시지를 보냄
        /// </summary>
        private void SendKeyToMe(HotKeyEventArgs hk)
        {
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = _g.GetGameWindowHandle(); } catch { }
            // v0.7.3: 창에 직접 키 메시지를 보내는 방식(v0.6.9)은 게임이 무시해서 서브가 점프를 못 함 → 원래 방식으로 되돌림
            _g.SendKey(hk);
        }

        private void Jump(string reason)
        {
            if (Now - _lastJump < 0.6) return;
            if (!IsAliveNow()) return;   // 영혼 상태에서는 점프하지 않음
            _lastJump = Now;
            Plugin.Log.Info($"[TrailFollow] {CharName}: 점프 ({reason})");
            try
            {
                HotKeyEventArgs hk = null;
                try { hk = _g.Keymap?.GetHotkey(((AltInputActions)JumpInputAction).ToString()); } catch { }
                if (hk == null || hk.Key <= 0) hk = HotKeyEventArgs.FromKeys(32, false, false, false);   // 기본: 스페이스바

                // v0.8.3: 게임 내부 명령 점프(v0.7.4)는 실제로는 동작하지 않았음 (오르막에서 높이가 올라가 "된다"고 잘못 판단한 캐릭터만 점프를 못 함)
                //         → 항상 키 보내기로 점프
                _g.SendKey(hk);
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 점프 입력 실패", ex); }
        }


        // =====================================================================
        //  핫바
        // =====================================================================
        /// <summary>핫바 번호(1~10)의 슬롯 10칸. 빈 칸은 null</summary>
        public IHotbarSlot[] GetHotbar(int hotbarNumber)
        {
            var result = new IHotbarSlot[10];
            try
            {
                var map = _ddo.GetHotbarMap();
                if (map == null) return result;
                uint bar = (uint)Math.Max(0, hotbarNumber - 1);   // SDK의 Bar/Slot 번호는 0부터
                foreach (var s in map)
                    if (s != null && s.Bar == bar && s.Slot < 10) result[s.Slot] = s;
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 핫바 읽기 실패", ex); }
            return result;
        }

        /// <summary>핫바 칸 사용 (Awesome AF와 같은 방식)</summary>
        // v0.8.2: 스킬을 쓰면 시전하는 동안 이동이 느려지거나 멈춤 → 그 사이를 "걸림"으로 오인해 탈출 동작을 하면서 길이 꼬였음
        private double _castGraceUntil;
        private const double CastGrace = 2.5;   // 스킬 사용 후 이 시간 동안은 걸림 판정 안 함
        private double _castMoveHold;            // v0.8.23: 스킬 사용 후 이 시간까지 이동 멈춤 (시전 애니메이션 대기)

        public bool UseSlot(IHotbarSlot s)
        {
            if (s == null) return false;
            _castGraceUntil = Now + CastGrace;
            // v0.8.27: 스킬 사용 뒤 1초 멈추던 것(추측으로 넣었던 것)을 뺌 - 버프를 쓸 때마다 서브가 뒤처지던 원인
            try
            {
                uint? action = s.GetInputAction();
                if (action == null && Enum.TryParse($"SHORTCUT_BAR{s.Bar + 1}_KEY{s.Slot + 1}", out InputActions parsed))
                    action = (uint)parsed;
                if (action == null) return false;
                CursorWatch.Note("핫바 사용");
                _ddo.SendInput(action.Value);
                Diag.Write(CharName, $"CAST 핫바 {s.Bar + 1} / 슬롯 {s.Slot + 1}");
                return true;
            }
            catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 핫바 사용 실패", ex); return false; }
        }

        public bool FireSlot(int bar, int slot, out string message)
        {
            var s = GetHotbar(bar).ElementAtOrDefault(slot - 1);
            if (s == null) { message = Lang.T($"핫바 {bar} / 슬롯 {slot} 이 비어 있음", $"Hotbar {bar} / slot {slot} is empty"); return false; }
            bool ok = UseSlot(s);
            message = ok ? Lang.T($"핫바 {bar} / 슬롯 {slot} 사용", $"Used hotbar {bar} / slot {slot}") : Lang.T($"핫바 {bar} / 슬롯 {slot} 사용 실패", $"Failed to use hotbar {bar} / slot {slot}");
            return ok;
        }

        // =====================================================================
        //  버프 타이머
        // =====================================================================
        private ulong _restoreTarget;
        private double _restoreTargetAt = -1;
        private double _lastTimerFire = -100;
        private bool _wasInDungeon;
        private uint? _lastDungeonQuest;
        private double _timersStartAt = -1;
        private bool _townLogged;
        private bool _catchingUp;
        private bool _nearMode;
        private const float NearLeaderDist = 10f;   // 리더가 이 안에 있으면 발자취 대신 리더 쪽으로 곧장
        private double _farHotkeyNext;
        private const float CatchUpMargin = 8f;   // 따라오는 거리보다 이만큼 더 뒤처지면 "따라잡는 중"
        // ---- 근처 적 감지 (Mapster와 같은 기준: 속성 268439740 = 1 이면 몬스터, 상태 268439052 = 2 면 죽음) ----
        private const uint PropIsMonster = 268439740;

        private double EnemyLinger => Math.Clamp(Settings?.EnemyLingerSec ?? 5, 0, 60);   // 적이 안 보여도 이 시간 동안은 계속 사용 (설정, 기본 5초)
        private double _enemySeenAt = -100, _nextEnemyScan;
        private bool _enemyLogged;

        private bool EnemyNearbyRecently()
        {
            if (Now >= _nextEnemyScan)
            {
                _nextEnemyScan = Now + 0.5;
                if (ScanEnemies(out string who))
                {
                    if (Now - _enemySeenAt >= Math.Max(EnemyLinger, 0.5)) Plugin.Log.Info($"[TrailFollow] {CharName}: 근처에 적 발견 ({who}) - 타이머 사용 시작");
                    _enemySeenAt = Now;
                    _enemyLogged = false;
                }
                else if (!_enemyLogged && Now - _enemySeenAt >= EnemyLinger && _enemySeenAt > 0)
                {
                    _enemyLogged = true;
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 근처에 적 없음 - 타이머 쉼");
                }
            }
            return Now - _enemySeenAt < Math.Max(EnemyLinger, 0.6);   // 0초여도 다음 확인(0.5초)까지는 유지
        }

        private bool ScanEnemies(out string who)
        {
            who = null;
            float radius = Math.Clamp(Settings?.EnemyRadiusM ?? 30, 5, 100);
            try
            {
                Vector3? me = MyPos();
                if (me == null) return false;
                ulong myId = CharId ?? 0;
                foreach (var e in _g.GetEntities() ?? Array.Empty<IEntity>())
                {
                    if (e == null || e.IsDeleted || e.InstanceId == myId) continue;
                    if (!IsMonster(e)) continue;
                    uint? state = e.PropertyCollection?.GetEnumProperty(PropCombatState)?.UInt32Value;
                    if (state == 2) continue;   // 죽음
                    int? hp = e.HitPoints_Current;
                    if (hp.HasValue && hp.Value <= 0) continue;
                    var loc = e.Location;
                    if (loc == null) continue;
                    Vector3 p = Global(loc);
                    float d = Dist2D(p, me.Value);
                    if (d <= radius && Math.Abs(p.Z - me.Value.Z) < 12f) { who = $"{e.Name} {d:0}m, 높이차 {p.Z - me.Value.Z:0}m"; return true; }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 몬스터 판정 (Mapster와 같은 순서): 종류(287) 하위 16비트 = 79(생물), 상위 16비트 = 1(몬스터 계열 - NPC는 0·2)
        /// 이면서 속성 1898 ≠ 1, 속성 268439740 = 1. v0.8.1: 예전엔 268439740만 봐서 NPC도 적으로 잡혔음
        /// </summary>
        private static bool IsMonster(IEntity e)
        {
            uint? type = null;
            try { type = e.WeeniePropertyCollection?.GetEnumProperty(PropObjectTypeId)?.UInt32Value; } catch { }
            if (!type.HasValue) { try { type = e.PropertyCollection?.GetEnumProperty(PropObjectTypeId)?.UInt32Value; } catch { } }
            if (!type.HasValue || (type.Value & 0xFFFF) != 79 || (type.Value >> 16) != 1) return false;
            if (ByteProp(e, 1898) == 1) return false;
            return ByteProp(e, PropIsMonster) == 1;
        }

        private const uint PropObjectTypeId = 287;

        private static byte? ByteProp(IEntity e, uint id)
        {
            byte? v = null;
            try { v = e.PropertyCollection?.GetBytePropertyValue(id); } catch { }
            if (!v.HasValue) { try { v = e.WeeniePropertyCollection?.GetBytePropertyValue(id); } catch { } }
            return v;
        }
        private object _seqSource;
        private int _seqIndex;
        private double _seqNext;

        private void FireTimer(BuffTimer t, string tag)
        {
            string msg;
            if (t.TargetLeader) msg = FireSlotOnLeader(t.Bar, t.Slot);
            else FireSlot(t.Bar, t.Slot, out msg);
            _lastTimerFire = Now;
            Plugin.Log.Info($"[TrailFollow] {CharName} 타이머{(tag == null ? "" : " (" + tag + ")")}: {msg}");
        }

        /// <summary>지금의 리더 (따라오기로 고정한 리더 → 마지막 리더 → 조작 중인 메인 순). 없거나 나 자신이면 0</summary>
        private ulong CurrentLeader()
        {
            ulong me = CharId ?? 0;
            foreach (ulong c in new[] { LockedLeader, Shared.LastLeader, Shared.FocusedCharId })
                if (c != 0 && c != me) return c;
            return 0;
        }

        /// <summary>리더를 선택한 뒤 칸을 사용하고, 1초 뒤 원래 대상으로 돌아감 (타이머의 "리더 고정")</summary>
        public string FireSlotOnLeader(int bar, int slot)
        {
            ulong leader = CurrentLeader();
            if (leader == 0) { FireSlot(bar, slot, out string m0); return m0 + Lang.T(" (리더가 없어 대상 고정 없이)", " (no leader, so without selecting one)"); }
            ulong prev = 0;
            try { prev = _g.GetSelectedObjectId() ?? 0; } catch { }
            if (prev != leader)
            {
                // v0.8.26: 대상 선택에 시간이 걸리므로 그동안 달리지 않게 이동키를 먼저 뗌 (밀린 백그라운드 선택 요청은 취소)
                SafeRelease();
                Interlocked.Increment(ref _selGen);
                try { Features.SelectSafe(_ddo, leader); } catch { }
                Thread.Sleep(150);   // 선택이 반영될 시간
            }
            FireSlot(bar, slot, out string m);
            if (prev != leader)
            {
                _restoreTarget = prev;
                _restoreTargetAt = Now + 2.5;   // 시전이 끝난 뒤 원래 대상으로 (리더 고정 타이머가 이어지면 그동안은 리더 유지)
            }
            string nm = Shared.NameOf(leader);
            return Lang.T($"{m} → 리더({(string.IsNullOrEmpty(nm) ? leader.ToString() : nm)})", $"{m} → leader ({(string.IsNullOrEmpty(nm) ? leader.ToString() : nm)})");
        }

        private void RestoreTargetIfDue()
        {
            if (_restoreTargetAt < 0 || Now < _restoreTargetAt) return;
            _restoreTargetAt = -1;
            if (_emergency || BuffTargetLocked) return;   // 비상 힐·버프 대상 고정 중이면 그쪽이 우선
            if (Shared.TargetMimic && !IsFocused) { _lastMimicTarget = 0; return; }   // 대상 따라 하기가 메인의 대상을 다시 선택
            if (_restoreTarget != 0) SelectInBackground(_restoreTarget);
        }

        private void RunTimers(bool paused)
        {
            RestoreTargetIfDue();
            var s = Settings;
            if (s == null) return;
            bool inTown = false;
            inTown = InTownCached();
            // v0.8.5: 던전(퀘스트 인스턴스)에 새로 들어가면 설정한 시간(기본 15초) 동안 타이머 쉼
            uint? quest = null;
            try { quest = _ddo.GetInstanceQuestDid(); } catch { }
            bool inDungeon = !inTown && quest.HasValue && quest.Value != 0;
            if (inDungeon && (!_wasInDungeon || quest != _lastDungeonQuest))
            {
                _timersStartAt = Now + Math.Max(0, s.TimersDungeonDelaySec);
                if (s.TimersDungeonDelaySec > 0) Plugin.Log.Info($"[TrailFollow] {CharName}: 던전 입장 - 타이머는 {s.TimersDungeonDelaySec}초 뒤부터");
            }
            _wasInDungeon = inDungeon;
            _lastDungeonQuest = inDungeon ? quest : null;

            BuffTimer[] timers;
            lock (SettingsLock) timers = s.Timers.ToArray();

            bool controllingMain = IsMain && !_followOn;
            // 서브 창의 [자동 힐/버프] 버튼이 켜져 있을 때만 작동
            bool active = AutoTimersOn && !paused && !controllingMain && IsAliveNow();
            if (!active || timers.Length == 0) { _nextFire.Clear(); _seqSource = null; return; }   // 다시 켜면 순서 모드는 1번부터

            foreach (var key in _nextFire.Keys.Where(k => !timers.Contains(k)).ToList()) _nextFire.Remove(key);

            bool mounted = s.TimersPauseWhileMounted && IsMounted();
            // v0.8.2: 리더에게서 많이 뒤처져 따라가는 중이면 타이머를 미룸 (시전하느라 느려져서 더 뒤처지지 않게)
            if (s.TimersHoldWhileCatchingUp && _followOn && _catchingUp) return;
            // v0.7.9: 근처에 적이 있을 때만 (적이 없으면 쉬다가, 적이 보이면 밀린 타이머부터 차례로)
            if (inTown && s.TimersPauseInTown)   // 마을에서는 쉬고, 나가면 다시 시작 (순서 모드는 1번부터)
            {
                if (!_townLogged) { _townLogged = true; Plugin.Log.Info($"[TrailFollow] {CharName}: 마을 - 힐/버프 타이머 쉼"); }
                _seqSource = null; _enemySeenAt = -100; return;
            }
            _townLogged = false;
            if (Now < _timersStartAt) return;
            if (s.TimersOnlyNearEnemies && !EnemyNearbyRecently()) return;
            bool hold = mounted || AutoActionsBlocked;   // 탑승·리콜 중이면 미뤘다가 끝나는 즉시 사용
            double minGap = Math.Clamp(s.TimerMinGapSec, 0, 10);
            bool gapOk = Now - _lastTimerFire >= minGap;

            if (s.TimerSequenceMode)
            {
                // 순서 모드: 사용 체크된 줄을 표 순서대로 하나씩. 각 줄의 간격(초) = 그 줄을 쓴 뒤 다음 줄까지 기다리는 시간
                var list = timers.Where(x => x.Enabled).ToList();
                if (list.Count == 0) return;
                if (!ReferenceEquals(_seqSource, s.Timers)) { _seqSource = s.Timers; _seqIndex = 0; _seqNext = Now + 1; }
                if (Now < _seqNext || hold || !gapOk) return;
                if (_seqIndex >= list.Count) _seqIndex = 0;
                var t = list[_seqIndex];
                FireTimer(t, $"순서 {_seqIndex + 1}/{list.Count}");
                _seqIndex = (_seqIndex + 1) % list.Count;
                _seqNext = Now + Math.Max(t.IntervalSec, minGap);
                return;
            }

            // 독립 모드: 줄마다 자기 간격대로. 여러 줄이 겹치면 가장 오래 기다린 줄부터 하나씩, 최소 간격을 두고 사용
            BuffTimer pick = null; double pickDue = double.MaxValue;
            foreach (var t in timers)
            {
                if (!t.Enabled) continue;
                if (!_nextFire.TryGetValue(t, out double due)) { _nextFire[t] = Now + 1 + _rng.NextDouble() * 2; continue; }
                if (Now >= due && due < pickDue) { pick = t; pickDue = due; }
            }
            if (pick == null || hold || !gapOk) return;
            FireTimer(pick, null);
            _nextFire[pick] = Now + Math.Max(3, pick.IntervalSec) + _rng.NextDouble() * 1.5;
        }

        // =====================================================================
        //  AFK 방지: 메인 창에서 켜면 모든 캐릭터가 정해진 간격마다 지정한 핫바 칸을 한 번 누름
        // =====================================================================
        // =====================================================================
        //  버프 순서 실행: 메인 창 [버프] → 모든 캐릭터가 버프 핫바의 1~10번 칸을 5초 간격으로 사용
        //  (진행 중에 다시 누르면 중단)
        // =====================================================================
        private int _lastBuffSeq = int.MinValue;
        private int _buffIndex = -1;          // -1: 실행 안 함, 0~9: 다음에 쓸 칸
        private double _buffNext;
        private int _buffUsed;

        /// <summary>버프 진행 상황 (창 표시용). 진행 중이 아니면 null</summary>
        public string BuffProgress => _buffIndex >= 0 ? $"{Math.Min(_buffIndex + 1, 10)}/10" : null;

        private ulong _buffTarget;
        private int _buffSelectTries;
        /// <summary>버프 중이고 대상을 고정해 둔 상태 (대상 따라 하기 잠시 멈춤)</summary>
        private bool BuffTargetLocked => _buffIndex >= 0 && _buffTarget != 0;

        private void RunBuffSequence()
        {
            int seq = Shared.BuffSeq;
            if (_lastBuffSeq == int.MinValue) _lastBuffSeq = seq;   // 시작 시점의 값은 무시
            if (seq != _lastBuffSeq)
            {
                _lastBuffSeq = seq;
                if (_buffIndex >= 0)
                {
                    _buffIndex = -1;
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 버프 중단");
                }
                else
                {
                    _buffIndex = 0;
                    _buffUsed = 0;
                    _buffNext = Now;
                    _buffSelectTries = 0;
                    // 서브는 메인이 [버프]를 누를 때 선택해 둔 대상을 버프가 끝날 때까지 유지 (메인은 직접 조작하므로 제외)
                    _buffTarget = IsMain ? 0 : Shared.BuffTarget;
                    string who = _buffTarget == 0 ? "" : $", 대상 고정: {(_g.GetEntity(_buffTarget)?.Name ?? _buffTarget.ToString())}";
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 버프 시작 (핫바 {Settings.BuffHotbar}{who})");
                }
            }
            if (_buffIndex < 0 || Now < _buffNext || AutoActionsBlocked) return;
            if (!IsAliveNow()) { _buffIndex = -1; return; }

            // 다음으로 채워진 칸을 찾아 사용 (빈 칸은 기다리지 않고 건너뜀)
            var bar = GetHotbar(Settings.BuffHotbar);
            while (_buffIndex < 10 && bar[_buffIndex] == null) _buffIndex++;
            if (_buffIndex >= 10)
            {
                Plugin.Log.Info($"[TrailFollow] {CharName}: 버프 완료 ({_buffUsed}개 사용)");
                _buffIndex = -1;
                return;
            }
            // 고정한 대상이 선택돼 있지 않으면 먼저 선택하고, 다음 틱에 사용
            if (_buffTarget != 0 && _buffSelectTries < 3)
            {
                ulong sel = 0;
                try { sel = _g.GetSelectedObjectId() ?? 0; } catch { }
                if (sel != _buffTarget)
                {
                    _buffSelectTries++;
                    SelectInBackground(_buffTarget);
                    _buffNext = Now + 0.3;
                    return;
                }
            }
            _buffSelectTries = 0;
            if (UseSlot(bar[_buffIndex])) _buffUsed++;
            _buffIndex++;
            _buffNext = Now + Shared.BuffInterval;
            if (_buffIndex >= 10 || !bar.Skip(_buffIndex).Any(x => x != null))
            {
                Plugin.Log.Info($"[TrailFollow] {CharName}: 버프 완료 ({_buffUsed}개 사용)");
                _buffIndex = -1;
            }
        }

        /// <summary>메인 창 [버프]: 지금 선택해 둔 대상을 같이 알려서, 서브들이 버프가 끝날 때까지 그 대상을 유지</summary>
        public void PostBuff()
        {
            ulong target = 0;
            try { target = _g.GetSelectedObjectId() ?? 0; } catch { }
            Shared.PostBuff(target);
        }

        // =====================================================================
        //  비상 힐 (리더 우선, 그다음 파티원)
        // =====================================================================
        private bool _emergency;
        private int _emergencyIdx;
        private double _emergencyNext;
        public bool EmergencyActive => _emergency;

        private float ReadHpPercent()
        {
            try
            {
                var e = _g.GetCurrentCharacter();
                int? cur = e?.HitPoints_Current, max = e?.HitPoints_Max;
                if (cur.HasValue && max.HasValue && max.Value > 0) return 100f * cur.Value / max.Value;
            }
            catch { }
            return -1f;
        }

        private static List<(int bar, int slot)> ParseSlots(string text)
        {
            var list = new List<(int, int)>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var part in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var ab = part.Split('.', '-', '/');
                if (ab.Length == 2 && int.TryParse(ab[0], out int b) && int.TryParse(ab[1], out int s) && b >= 1 && b <= 10 && s >= 1 && s <= 10)
                    list.Add((b, s));
            }
            return list;
        }

        private ulong _emergencyTarget;
        private bool _inTownCache;
        private double _inTownCheckAt = -1;

        /// <summary>마을 여부 (1초마다 다시 확인)</summary>
        private bool InTownCached()
        {
            if (Now >= _inTownCheckAt)
            {
                _inTownCheckAt = Now + 1.0;
                bool t = false;
                try { t = _ddo.InTown() == true; } catch { }
                _inTownCache = t;
            }
            return _inTownCache;
        }

        /// <summary>
        /// 비상 힐 대상 고르기: 리더가 기준 아래면 항상 리더.
        /// 리더가 괜찮으면 (설정 시) 다른 파티원 중 기준 아래인 캐릭터 - 이미 돕던 대상이 아직 낮으면 유지, 아니면 가장 낮은 캐릭터.
        /// </summary>
        private ulong PickEmergencyTarget(CharSettings st, ulong leader, out float hp)
        {
            hp = -1;
            float limit = st.EmergencyHpPercent;
            bool Low(float v) => v > 0 && v < limit;   // 0 이하(사망)나 모름(-1)은 제외

            if (Shared.TryReadChar(leader, out LeaderInfo li) && Low(li.HpPct)) { hp = li.HpPct; return leader; }
            if (!st.EmergencyIncludeParty) return 0;

            ulong best = 0; float bestHp = float.MaxValue;
            foreach (var (id, v) in Shared.ReadAllHp())
            {
                if (id == leader || !Low(v)) continue;
                if (id == _emergencyTarget) { hp = v; return id; }   // 대상을 자주 바꾸지 않도록 유지
                if (v < bestHp) { best = id; bestHp = v; }
            }
            if (best != 0) hp = bestHp;
            return best;
        }

        private void RunEmergencyHeal()
        {
            var st = Settings;
            ulong me = CharId ?? 0;
            ulong leader = LockedLeader != 0 ? LockedLeader : Shared.FocusedCharId;
            bool can = st != null && st.EmergencyHeal && AutoTimersOn && !IsMain && leader != 0 && leader != me
                       && !(_followOn && IsFocused) && IsAliveNow() && !AutoActionsBlocked;

            float hp = -1;
            ulong target = can ? PickEmergencyTarget(st, leader, out hp) : 0;

            if (target != 0 && target != _emergencyTarget)
            {
                string who = target == leader ? "리더" : target == me ? "자기 자신" : (Shared.NameOf(target) ?? target.ToString());
                Plugin.Log.Info($"[TrailFollow] {CharName}: {who} 체력 {hp:0}% - 비상 힐 {(_emergency ? "대상 변경" : "시작")}");
                if (!_emergency) { _emergencyIdx = 0; _emergencyNext = Now; }
                _emergency = true;
                _emergencyTarget = target;
            }
            else if (target == 0 && _emergency)
            {
                _emergency = false;
                _emergencyTarget = 0;
                Plugin.Log.Info($"[TrailFollow] {CharName}: 비상 힐 종료 (기준 아래인 캐릭터 없음)");
            }
            if (!_emergency || Now < _emergencyNext) return;

            var slots = ParseSlots(st.EmergencySlots);
            if (slots.Count == 0) { _emergency = false; _emergencyTarget = 0; return; }

            // 대상을 선택한 뒤 칸을 차례로 사용 (재사용 대기 중인 칸은 게임이 무시)
            try
            {
                if (_g.GetSelectedObjectId() != target)
                {
                    SafeRelease();   // v0.8.26: 선택하는 동안 달리지 않게
                    Interlocked.Increment(ref _selGen);
                    Features.SelectSafe(_ddo, target);
                }
            }
            catch { }
            var (bar, slot) = slots[_emergencyIdx % slots.Count];
            _emergencyIdx++;
            FireSlot(bar, slot, out _);
            _emergencyNext = Now + Math.Clamp(st.EmergencyIntervalMs, 200, 5000) / 1000.0;
        }

        // =====================================================================
        //  보상 고르기 따라 하기
        //  보상창이 열리면 그 창(보상 상자)에 들어 있는 아이템 목록을 기억해 두고,
        //  리더의 보상창이 닫힐 때 목록에서 빠져나간 아이템(= 리더가 고른 보상)을 서브들에게 알림.
        //  서브는 자기 보상창에서 같은 종류의 아이템을 선택해 "사용"해서 가져감.
        // =====================================================================
        // v0.5.6: 보상 목록이 캐릭터마다 다르게(무작위로) 나와서 "같은 보상 고르기"가 성립하지 않음 → 꺼 둠
        private static readonly bool RewardMimicEnabled = false;
        private ulong _rewardBox;
        private List<(ulong id, uint weenie, string name)> _rewardItems = new List<(ulong, uint, string)>();
        private readonly object _rewardLock = new object();

        private List<(ulong id, uint weenie, string name)> ReadRewardItems(ulong box)
        {
            var list = new List<(ulong, uint, string)>();
            try
            {
                foreach (var e in _g.GetEntities() ?? Array.Empty<IEntity>())
                    if (e != null && e.OwnerId == box && e.InstanceId != box)
                        list.Add((e.InstanceId, e.WeenieId ?? 0, e.Name ?? "?"));
            }
            catch { }
            return list;
        }

        private static string Describe(List<(ulong id, uint weenie, string name)> items)
            => items.Count == 0 ? "(없음)" : string.Join(", ", items.Select((x, i) => $"{i + 1}) {x.name} [{x.weenie}]"));

        public void OnRewardOpened(ulong box)
        {
            RewardWindowOpen = true;
            lock (_rewardLock) { _rewardBox = box; _rewardItems.Clear(); }
            if (!RewardMimicEnabled) return;
            System.Threading.Tasks.Task.Run(async () =>
            {
                // 목록이 조금 늦게 채워질 수 있어 잠시 기다렸다가 읽음
                for (int i = 0; i < 8; i++)
                {
                    await System.Threading.Tasks.Task.Delay(250);
                    var items = ReadRewardItems(box);
                    if (items.Count > 0 || i == 7)
                    {
                        lock (_rewardLock) { if (_rewardBox == box) _rewardItems = items; }
                        Plugin.Log.Info($"[TrailFollow] {CharName}: 보상창 열림 - 목록 {Describe(items)}");
                        return;
                    }
                }
            });
        }

        public void OnRewardClosed(ulong box)
        {
            RewardWindowOpen = false;
            if (!RewardMimicEnabled || !IsFocused) return;   // 지금 조작 중인 창(리더)만 알림
            List<(ulong id, uint weenie, string name)> items;
            lock (_rewardLock) { if (_rewardBox != box) return; items = _rewardItems.ToList(); _rewardBox = 0; }
            if (items.Count == 0) return;
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(300);
                // 보상 상자에서 빠져나간(주인이 바뀐) 아이템 = 리더가 고른 보상
                var moved = new List<(ulong id, uint weenie, string name)>();
                var state = new List<string>();
                foreach (var it in items)
                {
                    IEntity e = null;
                    try { e = _g.GetEntity(it.id); } catch { }
                    ulong? owner = e?.OwnerId;
                    state.Add($"{it.name}: {(e == null ? "없어짐" : "주인 " + (owner?.ToString() ?? "없음"))}");
                    if (e != null && owner.HasValue && owner.Value != box) moved.Add(it);
                }
                Plugin.Log.Info($"[TrailFollow] {CharName}: 보상창 닫힘 - {string.Join(", ", state)}");
                if (moved.Count == 1 && moved[0].weenie != 0)
                {
                    Shared.BumpReward(Slot, moved[0].weenie);
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 고른 보상 '{moved[0].name}' - 서브들에게 알림");
                }
                else Plugin.Log.Info($"[TrailFollow] {CharName}: 고른 보상을 알아내지 못함 (옮겨진 아이템 {moved.Count}개)");
            });
        }

        /// <summary>서브 쪽: 내 보상창에서 리더가 고른 것과 같은 종류의 아이템을 선택해 사용</summary>
        private void TakeSameReward(uint weenie)
        {
            if (weenie == 0) return;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 20 && !RewardWindowOpen; i++) await System.Threading.Tasks.Task.Delay(250);
                    ulong box;
                    lock (_rewardLock) box = _rewardBox;
                    if (!RewardWindowOpen || box == 0) { Plugin.Log.Info($"[TrailFollow] {CharName}: 보상창이 열려 있지 않아 보상 고르기 생략"); return; }
                    var items = ReadRewardItems(box);
                    var pick = items.FirstOrDefault(x => x.weenie == weenie);
                    if (pick.id == 0) { Plugin.Log.Info($"[TrailFollow] {CharName}: 내 보상 목록에 같은 아이템이 없음 - 목록 {Describe(items)}"); return; }
                    Features.SelectSafe(_ddo, pick.id);
                    await System.Threading.Tasks.Task.Delay(200);
                    _ddo.SendInput(InputUse);
                    Plugin.Log.Info($"[TrailFollow] {CharName}: 리더를 따라 보상 '{pick.name}' 선택·사용");
                }
                catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] 보상 고르기 실패", ex); }
            });
        }
        private const uint InputUse = 268435749;   // 게임의 "사용" 입력

        // =====================================================================
        //  오토어택: 메인 창 [공격] → 모든 서브가 핫바의 "공격" 버튼을 눌러 켜거나 끔 (Awesome AF 방식)
        // =====================================================================
        private const uint AutoAttackDid = 1879062547;   // 핫바의 "공격(오토어택)" 버튼 (Awesome AF 분석값)
        private const uint PropCombatState = 268439052;  // 캐릭터 상태: 2·3이면 공격 중 (Awesome AF가 공격 아이콘 표시에 쓰는 값)
        private int _lastAtkSeq = int.MinValue;

        /// <summary>게임 안의 실제 공격 상태 (모르면 null)</summary>
        private bool? IsAttackingNow(out uint? raw)
        {
            raw = null;
            try
            {
                raw = _g.GetCurrentCharacter()?.PropertyCollection?.GetEnumProperty(PropCombatState)?.UInt32Value;
                if (raw.HasValue) return raw.Value == 2 || raw.Value == 3;
            }
            catch { }
            return null;
        }


        private void RunAutoAttackCommand()
        {
            int seq = Shared.AttackSeq;
            if (_lastAtkSeq == int.MinValue) { _lastAtkSeq = seq; return; }   // 시작 시점의 값은 무시
            if (seq == _lastAtkSeq) return;
            _lastAtkSeq = seq;
            if (IsMain) return;   // 메인은 직접 조작

            bool on = Shared.AttackOn;
            // 켤 때 선택한 대상이 없으면 메인의 대상을 선택
            if (on)
            {
                try
                {
                    ulong main = LockedLeader != 0 ? LockedLeader : Shared.FocusedCharId;
                    ulong? sel = _g.GetSelectedObjectId();
                    if ((sel == null || sel == 0) && main != 0 && Shared.TryReadChar(main, out LeaderInfo li) && li.Target != 0 && li.Target != CharId)
                        Features.SelectSafe(_ddo, li.Target);
                }
                catch { }
            }
            // 설정한 칸(기본 핫바 10.8)의 오토어택 버튼을 한 번 누름 (누를 때마다 켜짐/꺼짐이 바뀜)
            FireSlot(Shared.AtkBar, Shared.AtkSlot, out string msg);
            Plugin.Log.Info($"[TrailFollow] {CharName}: 오토어택 {(on ? "켜기" : "끄기")} - {msg}, 선택 대상 {SafeSelected()}");
        }

        private string SafeSelected()
        {
            try { ulong? id = _g.GetSelectedObjectId(); return id.HasValue && id.Value != 0 ? (_g.GetEntity(id.Value)?.Name ?? id.Value.ToString()) : "없음"; }
            catch { return "?"; }
        }

        public void ToggleAttackAll()
        {
            bool on = !Shared.AttackOn;
            Shared.PostAttack(on);
            Plugin.Log.Info($"[TrailFollow] {CharName}: [공격] 버튼 - 모든 서브 오토어택 {(on ? "켜기" : "끄기")} 요청 (명령 {Shared.AttackSeq})");
        }

        private void RunAntiAfk()
        {
            if (!Shared.AntiAfk) { _nextAfk = -1; return; }
            if (_nextAfk < 0)
            {
                // 켜진 직후: 캐릭터마다 조금씩 다른 시각에 첫 입력 (1분 이내)
                _nextAfk = Now + 5 + _rng.NextDouble() * 55;
                Plugin.Log.Info($"[TrailFollow] {CharName}: AFK 방지 켜짐 ({Shared.AfkMinutes}분 간격, 핫바 {Shared.AfkBar}.{Shared.AfkSlot})");
                return;
            }
            if (Now < _nextAfk || AutoActionsBlocked) return;

            // v0.6.4: 게임 내부 입력(SendInput)은 게임의 자리 비움 시간을 되돌리지 못해서 로그아웃됨 →
            //         실제 키 입력(창에 키를 보냄)으로 그 칸의 단축키를 누르고, 점프 키도 한 번 누름
            string msg;
            HotKeyEventArgs hk = null;
            try { hk = _g.Keymap?.GetHotkey($"SHORTCUT_BAR{Shared.AfkBar}_KEY{Shared.AfkSlot}"); } catch { }
            if (hk != null && hk.Key > 0)
            {
                try { SendKeyToMe(hk); msg = $"핫바 {Shared.AfkBar}.{Shared.AfkSlot} 키 입력"; }
                catch (Exception ex) { Plugin.Log.Warn("[TrailFollow] AFK 방지 키 입력 실패", ex); msg = "키 입력 실패"; }
            }
            else
            {
                FireSlot(Shared.AfkBar, Shared.AfkSlot, out msg);
                msg += " (이 칸에 키가 지정돼 있지 않아 게임 내부 입력 사용)";
            }
            Jump("AFK 방지");
            Plugin.Log.Info($"[TrailFollow] {CharName}: AFK 방지 입력 - {msg} + 점프");
            _nextAfk = Now + Shared.AfkMinutes * 60 + _rng.NextDouble() * 30;   // 간격 + 0~30초
        }

        // =====================================================================
        //  공유 명령 (메인 창의 [따라오기] 버튼)
        // =====================================================================
        private void ApplyBroadcast()
        {
            int seq = Shared.CommandSeq;
            if (seq == _lastSeq) return;
            _lastSeq = seq;
            int follow = Shared.CommandFollow;
            if (follow == 1 && !_followOn && !IsFocused) SetFollow(true);   // 메인(조작 중인 창)은 제외
            else if (follow == 0) SetFollow(false);
        }

        // =====================================================================
        //  캐릭터 변경 / 설정
        // =====================================================================
        private void OnCharacterChanged(ulong? id)
        {
            StopMoving();
            ResetTrail();
            SetFollow(false);
            AutoTimersOn = false;
            _nextFire.Clear();
            SaveSettings();
            if (CharId.HasValue) Shared.ReleaseSlot(Slot, CharId.Value);
            Slot = -1;

            CharId = id;
            CharName = "";
            if (id != null)
            {
                Settings = CharSettings.Load(_folder, id.Value);
                CharName = ReadName();
                Slot = Shared.ClaimSlot(id.Value);
                Plugin.Log.Info($"[TrailFollow] 캐릭터 접속: {CharName} ({id})");
            }
            else Settings = null;

            CharacterChanged?.Invoke();
        }

        public void SaveSettings()
        {
            var s = Settings;
            var id = CharId;
            if (s != null && id.HasValue)
                lock (SettingsLock) s.Save(_folder, id.Value);
        }

        // =====================================================================
        //  유틸
        // =====================================================================
        private ILocation GetLocation(ulong id, bool isLeader)
        {
            ILocation l = null;
            if (isLeader)
            {
                try { l = _g.GetEntityLocationInAnotherClient(id, id); } catch { }
                if (HasPos(l)) return l;
            }
            try { l = _g.GetEntityLocation(id); } catch { }
            if (HasPos(l)) return l;
            if (!isLeader)
            {
                try { l = _g.GetCurrentCharacter()?.Location; } catch { }
                if (HasPos(l)) return l;
            }
            return null;
        }

        private static bool HasPos(ILocation l)
        {
            try { return l != null && l.Position.HasValue; } catch { return false; }
        }

        private static Vector3 Global(ILocation l)
        {
            var p = l.Position.Value;
            return new Vector3(l.LandblockX * LandblockSize + p.X, l.LandblockY * LandblockSize + p.Y, p.Z);
        }

        /// <summary>캐릭터 이름 읽기. 가끔 이름 대신 숫자가 오는 경우가 있어 두 가지 방법으로 시도</summary>
        private string ReadName()
        {
            string n = null;
            try { n = _g.GetCurrentCharacter()?.Name; } catch { }
            if (IsRealName(n)) return n;
            try { n = _g.GetCurrentCharacterName(); } catch { }
            return n ?? "";
        }

        private static bool IsRealName(string n) => !string.IsNullOrWhiteSpace(n) && !n.All(char.IsDigit);

        public bool IsMounted()
        {
            try { return (_g.GetCurrentCharacter()?.PropertyCollection?.GetBytePropertyValue(PropMounted)).GetValueOrDefault() != 0; }
            catch { return false; }
        }

        public bool IsAliveNow()
        {
            try
            {
                var ls = _g.GetCurrentCharacter()?.LifeState;
                return ls == null || ls == LifeState.Alive;
            }
            catch { return true; }
        }

        private static float Dist2D(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Normalize(double a)
        {
            a %= 2 * Math.PI;
            if (a < 0) a += 2 * Math.PI;
            return a;
        }

        private static double AngleDiff(double a, double b)
        {
            double d = Math.Abs(Normalize(a) - Normalize(b));
            return d > Math.PI ? 2 * Math.PI - d : d;
        }

        private void SetStatus(string s) => Status = s;

        public void Dispose()
        {
            _alive = false;
            try { _thread.Join(1500); } catch { }
            SafeRelease();
            SaveSettings();
            if (CharId.HasValue) Shared.ReleaseSlot(Slot, CharId.Value);
        }
    }
}
