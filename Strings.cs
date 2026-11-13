using System.Globalization;
using Microsoft.Win32;

namespace frmweatherwid
{
    internal enum AppLanguage { Vietnamese, English }

    /// <summary>
    /// UI language (Tiếng Việt / English). Strings live next to their use as T("vi", "en")
    /// so adding one never means editing a resource file. Saved in HKCU\Software\FrmWeatherWidget\Language.
    /// </summary>
    internal static class L
    {
        private const string SettingsKeyPath = @"Software\FrmWeatherWidget";

        public static AppLanguage Current { get; private set; } = Load();

        public static event Action? Changed;

        public static bool IsVi => Current == AppLanguage.Vietnamese;

        /// <summary>Language code for geocoding APIs.</summary>
        public static string Code => IsVi ? "vi" : "en";

        public static CultureInfo Culture => IsVi ? CultureInfo.GetCultureInfo("vi-VN") : CultureInfo.GetCultureInfo("en-US");

        public static string T(string vi, string en) => IsVi ? vi : en;

        public static void Set(AppLanguage lang)
        {
            if (lang == Current) return;
            Current = lang;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
                key.SetValue("Language", lang == AppLanguage.Vietnamese ? "vi" : "en");
            }
            catch { }
            Changed?.Invoke();
        }

        /// <summary>Saved choice, otherwise follow the Windows display language.</summary>
        private static AppLanguage Load()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue("Language") is string s)
                    return s == "vi" ? AppLanguage.Vietnamese : AppLanguage.English;
            }
            catch { }
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi" ? AppLanguage.Vietnamese : AppLanguage.English;
        }
    }
}
