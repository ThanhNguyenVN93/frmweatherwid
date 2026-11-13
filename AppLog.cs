using System.Text;

namespace frmweatherwid
{
    /// <summary>
    /// Tiny file logger: %LOCALAPPDATA%\WeatherWidget\logs\app-yyyyMMdd.log, kept 7 days.
    /// Never logs coordinates — logs can end up in a public GitHub issue.
    /// </summary>
    internal static class AppLog
    {
        private static readonly object Gate = new();
        private static readonly Mutex CrossProcess = new(false, @"Local\FrmWeatherWidget.Log");

        public static string Dir { get; } = Environment.GetEnvironmentVariable("WEATHERWIDGET_LOG_DIR") is { Length: > 0 } custom
            ? custom // tests point this at a temp folder so they don't write into the real log
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherWidget", "logs");

        private static string TodayFile => Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message, Exception? ex = null) =>
            Write("WARN", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

        public static void Error(string message, Exception ex) => Write("ERROR", $"{message}{Environment.NewLine}{ex}");

        private static void Write(string level, string message)
        {
            var bytes = Encoding.UTF8.GetBytes($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            lock (Gate)
            {
                // Two processes can log at once (old + new exe during an update). Appending from two handles races
                // (both seek to the same end → one line overwrites the other), so serialise across processes too.
                bool owned = false;
                try
                {
                    try { owned = CrossProcess.WaitOne(1000); }
                    catch (AbandonedMutexException) { owned = true; }

                    Directory.CreateDirectory(Dir);
                    using var fs = new FileStream(TodayFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    fs.Write(bytes);
                }
                catch
                {
                    // logging must never take the app down
                }
                finally
                {
                    if (owned) CrossProcess.ReleaseMutex();
                }
            }
        }

        public static void Prune(int keepDays = 7)
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                var cutoff = DateTime.Now.AddDays(-keepDays);
                foreach (var f in Directory.EnumerateFiles(Dir, "app-*.log"))
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
            catch { }
        }

        /// <summary>Last <paramref name="lines"/> lines of the newest log file (for bug reports).</summary>
        public static string Tail(int lines)
        {
            try
            {
                lock (Gate)
                {
                    var newest = Directory.Exists(Dir)
                        ? Directory.EnumerateFiles(Dir, "app-*.log").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                        : null;
                    if (newest is null) return "";
                    using var fs = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    var all = reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                    return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
                }
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Route every unhandled exception to the log, and offer a bug report for UI-thread ones.</summary>
        public static void InstallGlobalHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (_, e) =>
            {
                Error("Unhandled UI exception", e.Exception);
                var answer = MessageBox.Show(
                    L.T("Weather Widget gặp lỗi nhưng vẫn tiếp tục chạy.\n\nMở trang báo lỗi trên GitHub (kèm thông tin lỗi) không?",
                        "Weather Widget hit an error but keeps running.\n\nOpen a bug report on GitHub (with the error details)?"),
                    "Weather Widget", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes) BugReport.Open(e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) Error("Fatal exception", ex);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Warn("Unobserved task exception", e.Exception.GetBaseException());
                e.SetObserved();
            };
        }
    }
}
