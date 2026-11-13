namespace frmweatherwid
{
    internal static class Program
    {
        // "Local\" = per Windows session, so each logged-in user gets their own widget.
        private const string MutexName     = @"Local\FrmWeatherWidget.SingleInstance";
        private const string ShowEventName = @"Local\FrmWeatherWidget.Show";

        [STAThread]
        static void Main()
        {
            // Create/open the signal first so a second instance can always reach the first one.
            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var mutex     = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);

            if (!isFirstInstance)
            {
                // Already running → ask the existing instance to show itself, then quit.
                showEvent.Set();
                return;
            }

            ApplicationConfiguration.Initialize();
            using var form = new FrmWeatherWidget();

            var registration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
            {
                try
                {
                    if (form.IsHandleCreated && !form.IsDisposed)
                        form.BeginInvoke(form.ShowWidget);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // form is closing — ignore (an unhandled exception here would kill the process)
                }
            }, null, Timeout.Infinite, executeOnlyOnce: false);

            try
            {
                Application.Run(form);
            }
            finally
            {
                registration.Unregister(null);
                mutex.ReleaseMutex();
            }
        }
    }
}
