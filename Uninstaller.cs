using System.Diagnostics;
using Microsoft.Win32;

namespace frmweatherwid
{
    /// <summary>
    /// Makes the portable exe show up in Settings → Apps → Installed apps (per-user, no admin) with a working Uninstall.
    /// The uninstall command is plain cmd — it does NOT need the exe — so it still cleans up after the user has already
    /// deleted the folder: stops the app, removes the run-at-startup entry, settings, logs, caches, the exe, and itself.
    /// </summary>
    internal static class Uninstaller
    {
        private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WeatherWidget";
        private const string RunKeyPath       = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName     = "FrmWeatherWidget";
        private const string SettingsKeyPath  = @"Software\FrmWeatherWidget";

        /// <summary>Called at every start of a release build: keeps path/version/size in "Installed apps" current.</summary>
        public static void Register()
        {
            if (!UpdateService.CanSelfUpdate || Environment.ProcessPath is not { } exe) return;
            // Installed with winget → winget already lists and uninstalls it; don't show the app twice.
            if (exe.Contains(@"\Microsoft\WinGet\Packages\", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
                key.SetValue("DisplayName", "Weather Widget");
                key.SetValue("DisplayVersion", UpdateService.CurrentVersion.ToString(3));
                key.SetValue("Publisher", "Nguyen Thanh");
                key.SetValue("DisplayIcon", exe);
                key.SetValue("InstallLocation", Path.GetDirectoryName(exe) ?? "");
                key.SetValue("URLInfoAbout", AppController.RepoUrl);
                key.SetValue("HelpLink", AppController.RepoUrl + "/issues");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
                if (key.GetValue("InstallDate") is null) key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("UninstallString", Command(exe));
                key.SetValue("QuietUninstallString", Command(exe));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Couldn't register in Installed apps", ex);
            }
        }

        /// <summary>The "Uninstall" button in Settings: same command, run hidden, after the user confirmed.</summary>
        public static void UninstallNow()
        {
            var exe = UpdateService.CanSelfUpdate ? Environment.ProcessPath : null; // never delete a dev build in bin\
            var cmd = Command(exe);
            AppLog.Info("Uninstall requested");
            Process.Start(new ProcessStartInfo("cmd.exe", cmd["cmd.exe ".Length..]) { CreateNoWindow = true, UseShellExecute = false });
            Application.Exit();
        }

        /// <summary>
        /// One cmd line, every step independent ("&amp;") and silent, so a missing piece never stops the rest.
        /// %LOCALAPPDATA% / %TEMP% are expanded by cmd when it runs (works for whichever user uninstalls).
        /// </summary>
        internal static string Command(string? exe)
        {
            var steps = new List<string>
            {
                "taskkill /f /im WeatherWidget.exe >nul 2>&1",
                "ping -n 3 127.0.0.1 >nul",                                                 // let the process release the exe
                $"reg delete \"HKCU\\{RunKeyPath}\" /v {RunValueName} /f >nul 2>&1",
                $"reg delete \"HKCU\\{SettingsKeyPath}\" /f >nul 2>&1",
                "rd /s /q \"%LOCALAPPDATA%\\WeatherWidget\" >nul 2>&1",                      // logs + weather cache
                "rd /s /q \"%TEMP%\\FrmWeatherWidget\" >nul 2>&1",                           // wallpaper cache
                "rd /s /q \"%TEMP%\\.net\\WeatherWidget\" >nul 2>&1",                        // single-file extraction
            };
            if (!string.IsNullOrEmpty(exe))
                steps.Add($"del /f /q \"{exe}\" \"{exe}.old\" \"{exe}.new\" \"{exe}.bad\" >nul 2>&1");
            steps.Add($"reg delete \"HKCU\\{UninstallKeyPath}\" /f >nul 2>&1");
            return "cmd.exe /d /c " + string.Join(" & ", steps);
        }
    }
}
