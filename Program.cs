using System.Diagnostics;

namespace frmweatherwid
{
    internal static class Program
    {
        // "Local\" = per Windows session, so each logged-in user gets their own widget.
        private const string MutexName       = @"Local\FrmWeatherWidget.SingleInstance";
        private const string ShowEventName   = @"Local\FrmWeatherWidget.Show";
        internal const string UpdateOkEvent  = @"Local\FrmWeatherWidget.UpdateOk";
        private const string UpdatedArg      = "--updated-from";
        private const string UpdateFailedArg = "--update-failed";
        private static readonly TimeSpan UpdateHealthWindow = TimeSpan.FromSeconds(90);

        /// <summary>Set by the auto-updater: the new exe to start after we've released the mutex, and its tag.</summary>
        internal static (string Path, string Args, string Tag)? RestartAfterExit;

        [STAThread]
        static void Main(string[] args)
        {
            string? Arg(string name)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            string? updatedFrom  = Arg(UpdatedArg);
            string? updateFailed = Arg(UpdateFailedArg);

            // Create/open the signal first so a second instance can always reach the first one.
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var mutex     = new Mutex(initiallyOwned: false, MutexName);

            bool owned;
            try
            {
                // After a self-update / rollback the previous process may still be shutting down — give it a moment.
                owned = mutex.WaitOne(updatedFrom is null && updateFailed is null ? 0 : 10_000);
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
            var options = new StartOptions(updatedFrom, updateFailed,
                                           OpenSettings: args.Contains("--settings"),
                                           OpenAbout: args.Contains("--about"),
                                           TestApplyFile: Arg("--test-apply"));
            using (var app = new AppController(options))
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

            if (RestartAfterExit is { } restart) StartUpdatedAndWatch(restart.Path, restart.Args, restart.Tag);
        }

        internal static string UpdatedArgs(Version from) => $"{UpdatedArg} {from}";

        /// <summary>
        /// Start the freshly installed exe and stay around until it reports it's healthy (it sets <see cref="UpdateOkEvent"/>
        /// after ~20 s of running). If it dies or hangs before that, put the previous exe back and start it again,
        /// remembering the broken tag so auto-update doesn't install it again.
        /// </summary>
        private static void StartUpdatedAndWatch(string exe, string args, string tag)
        {
            using var ok = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateOkEvent);
            AppLog.Info($"Starting {tag}, waiting up to {UpdateHealthWindow.TotalSeconds:0}s for it to report healthy");
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Couldn't start updated {tag}", ex);
            }

            var deadline = DateTime.UtcNow + UpdateHealthWindow;
            while (process is not null && DateTime.UtcNow < deadline)
            {
                if (ok.WaitOne(500))
                {
                    AppLog.Info($"Updated {tag} is running fine");
                    return;
                }
                if (process.HasExited) break;
            }
            if (ok.WaitOne(0)) return;

            AppLog.Warn($"Updated {tag} didn't start properly (exit code {(process is { HasExited: true } ? process.ExitCode : -1)}) — rolling back");
            try
            {
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
                process?.WaitForExit(5000);
            }
            catch { }
            Rollback(exe, tag);
        }

        private static void Rollback(string exe, string tag)
        {
            var old = exe + ".old";
            if (!File.Exists(old))
            {
                AppLog.Warn("Rollback impossible: previous exe is gone");
                return;
            }
            try
            {
                File.Move(exe, exe + ".bad", overwrite: true); // keep it for diagnosis; cleaned up later
                File.Move(old, exe);                         // we may be running from .old — renaming a running exe is allowed
                AppSettings.SkipUpdateTag = tag;
                Process.Start(new ProcessStartInfo(exe, $"{UpdateFailedArg} {tag}") { UseShellExecute = false });
                AppLog.Info($"Rolled back from {tag}");
            }
            catch (Exception ex)
            {
                AppLog.Error("Rollback failed", ex);
            }
        }
    }

    internal sealed record StartOptions(string? UpdatedFrom, string? UpdateFailed, bool OpenSettings, bool OpenAbout, string? TestApplyFile);
}
