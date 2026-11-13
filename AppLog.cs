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

        public static string Dir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherWidget", "logs");

        private static string TodayFile => Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message, Exception? ex = null) =>
            Write("WARN", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

        public static void Error(string message, Exception ex) => Write("ERROR", $"{message}{Environment.NewLine}{ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(TodayFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
                // logging must never take the app down
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
                    var all = File.ReadAllLines(newest, Encoding.UTF8);
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
