using System.Diagnostics;

namespace frmweatherwid
{
    internal static class Program
    {
        // "Local\" = per Windows session, so each logged-in user gets their own widget.
        private const string MutexName     = @"Local\FrmWeatherWidget.SingleInstance";
        private const string ShowEventName = @"Local\FrmWeatherWidget.Show";
        private const string UpdatedArg    = "--updated-from";

        /// <summary>Set by the auto-updater: start this exe after we've exited and released the mutex.</summary>
        internal static (string Path, string Args)? RestartAfterExit;

        [STAThread]
        static void Main(string[] args)
        {
            // After a self-update the previous process may still be shutting down — give it a moment.
            int argIndex = Array.IndexOf(args, UpdatedArg);
            string? updatedFrom = argIndex >= 0 && argIndex + 1 < args.Length ? args[argIndex + 1] : null;

            // Create/open the signal first so a second instance can always reach the first one.
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var mutex     = new Mutex(initiallyOwned: false, MutexName);

            bool owned;
            try
            {
                owned = mutex.WaitOne(updatedFrom is null ? 0 : 10_000);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // previous instance died without releasing — we own it now
            }

            if (!owned)
            {
                // Already running → ask the existing instance to show itself, then quit.
                showEvent.Set();
                return;
            }

            ApplicationConfiguration.Initialize();
            AppLog.InstallGlobalHandlers();
            using (var app = new AppController(updatedFrom, openSettings: args.Contains("--settings")))
            {
                // A second launch signals us → show the widgets (RequestShow marshals to the UI thread itself).
                var registration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => app.RequestShow(),
                                                                          null, Timeout.Infinite, executeOnlyOnce: false);
                try
                {
                    Application.Run(app);
                }
                finally
                {
                    registration.Unregister(null);
                    mutex.ReleaseMutex();
                }
            }

            if (RestartAfterExit is { } restart)
            {
                try { Process.Start(new ProcessStartInfo(restart.Path, restart.Args) { UseShellExecute = false }); }
                catch { /* nothing more we can do; the user can start it manually */ }
            }
        }

        internal static string UpdatedArgs(Version from) => $"{UpdatedArg} {from}";
    }
}
