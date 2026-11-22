using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace TrailFollow
{
    /// <summary>GitHub에 올라와 있는 최신 버전 정보</summary>
    internal sealed class UpdateInfo
    {
        public Version Version;
        public string Sha256;     // TrailFollow.dll의 SHA-256 (내려받은 파일이 온전한지 확인용)
        public string NotesKo;
        public string NotesEn;
    }

    /// <summary>
    /// v0.9.1: 설정 창 [정보] 탭의 업데이트 기능.
    /// GitHub 저장소의 dist 폴더(version.json, TrailFollow.dll, plugin.metadata, TrailFollow.deps.json)를 확인해서
    /// 더 새 버전이 있으면 플러그인 폴더의 파일을 바꿔 놓음. Dungeon Helper를 다시 시작하면 새 버전이 적용됨.
    /// 버튼을 눌렀을 때만 동작하고, 저절로 확인하거나 설치하지 않음.
    /// </summary>
    internal static class Updater
    {
        public const string RepoUrl = "https://github.com/Madmachine-X/TrailFollow";
        private const string RawBase = "https://raw.githubusercontent.com/Madmachine-X/TrailFollow/main/dist/";
        private static readonly HttpClient Http = MakeClient();

        private static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            try { c.DefaultRequestHeaders.UserAgent.ParseAdd("TrailFollow-Updater"); } catch { }
            return c;
        }

        /// <summary>버전을 세 자리(0.9.1)로 맞춤 - 네 자리와 세 자리를 그대로 비교하면 같은 버전도 다르게 나옴</summary>
        private static Version Norm(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

        public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

        /// <summary>GitHub에서 최신 버전 정보를 읽음. 실패하면 예외</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            string json = await Http.GetStringAsync(RawBase + "version.json?t=" + DateTime.UtcNow.Ticks).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(json))
            {
                var r = doc.RootElement;
                var info = new UpdateInfo { Version = Norm(Version.Parse(r.GetProperty("version").GetString())) };
                if (r.TryGetProperty("sha256", out var sha)) info.Sha256 = sha.GetString();
                if (r.TryGetProperty("notes_ko", out var nk)) info.NotesKo = nk.GetString();
                if (r.TryGetProperty("notes_en", out var ne)) info.NotesEn = ne.GetString();
                return info;
            }
        }

        public static bool IsNewer(UpdateInfo info) => info != null && info.Version > Current;

        /// <summary>새 버전 파일을 내려받아 플러그인 폴더에 넣음. 실패하면 예외 (그 경우 기존 파일은 그대로)</summary>
        public static async Task InstallAsync(UpdateInfo info, string folder)
        {
            string q = "?t=" + DateTime.UtcNow.Ticks;
            byte[] dll = await Http.GetByteArrayAsync(RawBase + "TrailFollow.dll" + q).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                string got = Convert.ToHexString(SHA256.HashData(dll));
                if (!string.Equals(got, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Lang.T("내려받은 파일이 손상되었습니다 (확인값 불일치).", "The downloaded file is corrupted (checksum mismatch)."));
            }
            byte[] meta = await Http.GetByteArrayAsync(RawBase + "plugin.metadata" + q).ConfigureAwait(false);
            byte[] deps = null;
            try { deps = await Http.GetByteArrayAsync(RawBase + "TrailFollow.deps.json" + q).ConfigureAwait(false); } catch { }

            string target = Path.Combine(folder, "TrailFollow.dll");
            string tmp = target + ".new";
            File.WriteAllBytes(tmp, dll);

            // 사용 중인 DLL은 덮어쓸 수 없지만 이름은 바꿀 수 있음 → 옛 파일을 .old로 밀어 두고 새 파일을 그 자리에 놓음
            if (File.Exists(target))
            {
                string old = target + ".old";
                try { if (File.Exists(old)) File.Delete(old); }
                catch { old = target + "." + DateTime.Now.Ticks + ".old"; }
                File.Move(target, old);
            }
            File.Move(tmp, target);
            File.WriteAllBytes(Path.Combine(folder, "plugin.metadata"), meta);
            if (deps != null) try { File.WriteAllBytes(Path.Combine(folder, "TrailFollow.deps.json"), deps); } catch { }
        }

        /// <summary>지난 업데이트 때 밀어 둔 옛 파일을 지움 (시작할 때 호출)</summary>
        public static void CleanupOld(string folder)
        {
            try
            {
                foreach (string f in Directory.GetFiles(folder, "TrailFollow.dll*.old")) try { File.Delete(f); } catch { }
                string tmp = Path.Combine(folder, "TrailFollow.dll.new");
                if (File.Exists(tmp)) try { File.Delete(tmp); } catch { }
            }
            catch { }
        }
    }
}
