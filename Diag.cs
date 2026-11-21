using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TrailFollow
{
    /// <summary>
    /// 진단 기록. "내 PC\Downloads\TrailFollow\trace" 폴더가 있을 때만 그 안에 캐릭터별 파일로 기록.
    /// (그 폴더가 없는 PC에서는 아무것도 하지 않음)
    /// </summary>
    internal static class Diag
    {
        private static readonly object Gate = new object();
        private static readonly string Dir;
        public static readonly bool Enabled;
        private const long MaxBytes = 4 * 1024 * 1024;

        static Diag()
        {
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TrailFollow");
                // v0.9.0: trace 폴더를 직접 만들어 둔 PC에서만 기록 (공개 배포판에서 저절로 켜지지 않게)
                string dir = Path.Combine(root, "trace");
                if (Directory.Exists(dir))
                {
                    Dir = dir;
                    Enabled = true;
                }
            }
            catch { Enabled = false; }
        }

        public static void Write(string who, string line)
        {
            if (!Enabled) return;
            try
            {
                string name = string.IsNullOrWhiteSpace(who) ? "unknown" : who;
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                string path = Path.Combine(Dir, name + ".txt");
                byte[] bytes = Encoding.UTF8.GetBytes(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + "\r\n");
                lock (Gate)
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes)
                        {
                            string old = Path.Combine(Dir, name + ".old.txt");
                            File.Copy(path, old, true);
                            File.Delete(path);
                        }
                    }
                    catch { }
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// v0.8.33: 진단 - 마우스 커서가 한 번에 크게 튀는 순간을 기록하고, 그 직전에 플러그인이 무슨 동작을 했는지 같이 남김.
    /// (플러그인 동작 직후에만 튀면 플러그인이 시킨 동작 때문이고, 아무 동작도 없을 때 튀면 다른 원인)
    /// 기록 파일: Downloads\TrailFollow\trace\_cursor.txt
    /// </summary>
    internal static class CursorWatch
    {
        private static int _started;
        private static volatile string _lastAction = "";
        private static long _lastActionMs = -100000;
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private const int JumpPx = 300;

        /// <summary>플러그인이 커서에 영향을 줄 수 있는 동작을 하기 직전에 호출</summary>
        public static void Note(string action)
        {
            _lastAction = action ?? "";
            Volatile.Write(ref _lastActionMs, Clock.ElapsedMilliseconds);
        }

        public static void Start()
        {
            if (!Diag.Enabled) return;
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            var t = new Thread(Run) { IsBackground = true, Name = "TrailFollow CursorWatch" };
            t.Start();
        }

        private static void Run()
        {
            Native.GetCursorPos(out var prev);
            long prevMs = Clock.ElapsedMilliseconds;
            double prevStep = 0;   // 바로 앞 구간에서 움직인 거리 (가만히 있다가 튄 것인지, 계속 움직이던 중인지 구분용)
            int pid = 0;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
            while (true)
            {
                Thread.Sleep(5);
                try
                {
                    if (!Native.GetCursorPos(out var now)) continue;
                    long ms = Clock.ElapsedMilliseconds;
                    long dx = now.X - prev.X, dy = now.Y - prev.Y;
                    double step = Math.Sqrt(dx * dx + dy * dy);
                    if (step > JumpPx)
                    {
                        long ago = ms - Volatile.Read(ref _lastActionMs);
                        string act = ago <= 1000 ? $"{_lastAction} ({ago}ms 전)" : "없음";
                        bool l = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0, r = (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
                        Diag.Write("_cursor", $"[{pid}] CURSOR 튐 ({prev.X},{prev.Y})->({now.X},{now.Y}) {step:0}px / {ms - prevMs}ms, 직전 구간 이동 {prevStep:0}px, 버튼 L={(l ? 1 : 0)} R={(r ? 1 : 0)}, 직전 플러그인 동작: {act}");
                    }
                    prev = now; prevMs = ms; prevStep = step;
                }
                catch { }
            }
        }
    }
}
