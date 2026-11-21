using System;
using System.Globalization;
using System.IO;

namespace TrailFollow
{
    /// <summary>
    /// v0.9.0: 화면 글자를 한국어/영어 중 하나로 표시.
    /// 기본은 Windows 표시 언어를 따름 (한국어 Windows = 한국어, 그 외 = 영어).
    /// 설정 창에서 직접 고르면 플러그인 폴더의 TrailFollow.lang 파일에 저장되고, Dungeon Helper를 다시 시작하면 적용됨.
    /// </summary>
    internal static class Lang
    {
        public static bool En { get; private set; } = Detect();
        /// <summary>"auto", "ko", "en"</summary>
        public static string Choice { get; private set; } = "auto";
        private static string _file;

        private static bool Detect()
        {
            try { return !CultureInfo.CurrentUICulture.Name.StartsWith("ko", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static void Init(string folder)
        {
            try
            {
                _file = Path.Combine(folder, "TrailFollow.lang");
                if (!File.Exists(_file)) return;
                string v = File.ReadAllText(_file).Trim().ToLowerInvariant();
                if (v == "en") { En = true; Choice = "en"; }
                else if (v == "ko") { En = false; Choice = "ko"; }
            }
            catch { }
        }

        /// <summary>언어 선택을 저장 (다음 실행부터 적용)</summary>
        public static bool Save(string choice)
        {
            try
            {
                if (_file == null) return false;
                if (choice == "auto") { if (File.Exists(_file)) File.Delete(_file); }
                else File.WriteAllText(_file, choice);
                Choice = choice;
                return true;
            }
            catch { return false; }
        }

        /// <summary>한국어/영어 중 지금 언어에 맞는 글자</summary>
        public static string T(string ko, string en) => En ? en : ko;
    }
}
