using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using log4net;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Plugins;

namespace TrailFollow
{
    /// <summary>
    /// Dungeon Helper가 DDO 클라이언트마다 하나씩 만드는 플러그인 본체.
    /// </summary>
    public sealed class Plugin : IDdoPlugin, IPlugin, IPluginUI, IDisposable
    {
        internal static readonly ILog Log = LogManager.GetLogger(typeof(Plugin));

        private SharedState _shared;
        private Follower _follower;
        private Thread _uiThread;
        private HotbarWindow _window;
        private SettingsForm _settingsForm;
        private Image _icon;
        private bool _terminated;

        // ---- IPlugin ----
        public Guid PluginId => new Guid("6f3b2c1e-8a4d-4f7e-9c2b-5d1a7e3f9b40");
        public GameId Game => GameId.DDO;
        public string PluginKey => "TrailFollow";
        public string Name => "TrailFollow";
        public string Description => Lang.T("발자취 따라가기 + 서브 핫바 창 + 리더 따라 하기 (Awesome AF 대체)", "Trail following + alt hotbar windows + mimic the leader (an Awesome AF alternative)");
        public string Author => "Madmachine";
        public Version Version => new Version(0, 9, 1);

        public void Initialize(IDdoGameDataProvider gameDataProvider, string folder)
        {
            Log.Info($"[TrailFollow] 초기화 v0.9.1 by Madmachine (폴더: {folder})");
            Updater.CleanupOld(folder);   // v0.9.1: 지난 업데이트 때 남은 옛 파일 정리
            Lang.Init(folder);   // v0.9.0: 화면 언어 (한국어/영어)
            _shared = new SharedState();
            if (_shared.FreshlyCreated) GlobalSettings.Load(folder).ApplyTo(_shared);   // 첫 클라이언트가 공통 설정을 불러옴

            _follower = new Follower(gameDataProvider, folder, _shared);

            CursorWatch.Start();   // v0.9.0: 진단 - 마우스 커서가 튀는 순간 기록
            try { Features.Register(gameDataProvider, _follower); }
            catch (Exception ex) { Log.Error("[TrailFollow] 자동 수락 기능 등록 실패", ex); }

            // 화면 창은 자체 UI 스레드에서 실행
            _uiThread = new Thread(() =>
            {
                try
                {
                    _window = new HotbarWindow(_follower);
                    Application.Run(_window);
                }
                catch (Exception ex) { Log.Error("[TrailFollow] 화면 창 오류", ex); }
            })
            { IsBackground = true, Name = "TrailFollow UI" };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
        }

        public IPluginUI GetPluginUI() => this;

        public void Terminate()
        {
            if (_terminated) return;
            _terminated = true;
            Log.Info("[TrailFollow] 종료");
            try
            {
                var w = _window;
                if (w != null && w.IsHandleCreated) w.BeginInvoke(new Action(() => w.CloseForGood()));
            }
            catch { }
            try { _follower?.Dispose(); } catch { }
            try { _shared?.Dispose(); } catch { }
        }

        public void Dispose() => Terminate();

        // ---- IPluginUI (Dungeon Helper 안의 설정 화면) ----
        public float? FocusedOpacity => null;
        public bool EnabledInCharacterSelection => false;
        public Image ToolbarImage => _icon ??= MakeIcon();
        public Tuple<int, int> MinSize => Tuple.Create(510, 560);

        public object UserInterfaceForm
        {
            get
            {
                if (_follower == null) return new Form { Text = Lang.T("TrailFollow - 초기화 전", "TrailFollow - not initialized yet") };
                if (_settingsForm == null || _settingsForm.IsDisposed)
                    _settingsForm = new SettingsForm(_follower);
                return _settingsForm;
            }
        }

        private static Image MakeIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var dot = new SolidBrush(Color.FromArgb(120, 200, 255)))
                {
                    g.FillEllipse(dot, 3, 22, 6, 6);
                    g.FillEllipse(dot, 11, 15, 6, 6);
                    g.FillEllipse(dot, 19, 9, 6, 6);
                }
                using (var head = new SolidBrush(Color.FromArgb(255, 200, 80)))
                    g.FillEllipse(head, 23, 2, 8, 8);
            }
            return bmp;
        }
    }
}
