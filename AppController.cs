using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace frmweatherwid
{
    /// <summary>
    /// The app itself: data, tray icon, timers, alerts, hotkey, auto-update — and one <see cref="WidgetForm"/>
    /// per monitor that only draws what this controller holds. Runs as the ApplicationContext (no main form).
    /// </summary>
    internal sealed class AppController : ApplicationContext
    {
        // ------------------------------------------------------------------ config
        // Internal id for the Run registry value and the cache folder — kept as-is so existing installs keep their settings.
        private const string AppName         = "FrmWeatherWidget";
        private const string RunKeyPath      = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string SettingsKeyPath = @"Software\FrmWeatherWidget";
        private const string DonateUrl       = "https://nguyenthanhdev.com/donate/";

        private const int RefreshMs          = 15 * 60_000;    // also how fresh the "rain soon" alert is
        private const int RetryMs            = 5 * 60_000;     // after every provider failed
        private const int ResumeDelayMs      = 15_000;         // after waking from sleep
        private const int WallpaperRotateMs  = 60_000;
        private const int QuoteRotateMs      = 15_000;
        private const int FirstUpdateCheckMs = 60_000;         // 1 min after start (don't slow startup)
        private const int UpdateCheckMs      = 6 * 60 * 60_000;
        private const int RainAlertMinutes   = 60;             // warn when rain starts within this window
        private const int AqiAlertLevel      = 151;            // "Unhealthy" and worse
        private const int AqiAlertReset      = 100;            // re-arm once it's back to Moderate

        internal static readonly HttpClient Http = CreateHttpClient();

        private readonly WeatherService weatherService   = new(Http);
        private readonly LocationService locationService = new(Http);
        private readonly UpdateService updateService     = new(Http);

        // ------------------------------------------------------------------ state the widgets draw
        public GeoLocation Location { get; private set; }
        public WeatherData? Weather { get; private set; }
        public int? UsAqi { get; private set; }
        public DateTime? LastUpdated { get; private set; }
        public bool LastFetchFailed { get; private set; }
        public bool IsUpdating { get; private set; }
        public bool IsLocating { get; private set; }
        public int LoadingDots { get; private set; }
        public Image WeatherIcon { get; private set; }
        public Bitmap? Wallpaper => wallpapers.Count > 0 ? wallpapers[wallpaperIndex] : null;
        public (string Text, string Author)? Quote => quotes.Count > 0 ? quotes[quoteIndex] : null;

        /// <summary>Weather/location/settings changed → repaint everything.</summary>
        public event Action? DataChanged;
        /// <summary>Only the status line (loading dots) changed.</summary>
        public event Action? StatusChanged;
        public event Action? WallpaperChanged;
        public event Action? QuoteChanged;

        // ------------------------------------------------------------------ infrastructure
        private readonly Control ui;                 // hidden control = UI-thread marshalling target
        private readonly NotifyIcon tray;
        private readonly HotkeyWindow hotkey = new();
        private readonly List<WidgetForm> widgets = [];
        private bool widgetsShown;
        private readonly Dictionary<(WeatherKind, bool, int), Bitmap> iconCache = [];

        private readonly System.Windows.Forms.Timer refreshTimer   = new() { Interval = RefreshMs };
        private readonly System.Windows.Forms.Timer loadingTimer   = new() { Interval = 300 };
        private readonly System.Windows.Forms.Timer wallpaperTimer = new() { Interval = WallpaperRotateMs };
        private readonly System.Windows.Forms.Timer quoteTimer     = new() { Interval = QuoteRotateMs };
        private readonly System.Windows.Forms.Timer updateTimer    = new() { Interval = FirstUpdateCheckMs };
        private readonly System.Windows.Forms.Timer clockTimer     = new() { Interval = 60_000 }; // date box rolls over at midnight
        private DateTime shownDate = DateTime.Today;

        private bool relocateOnNextRefresh;          // set after resume from sleep
        private bool refreshQueued, relocateQueued;  // request that arrived while updating

        private readonly List<Bitmap> wallpapers = [];
        private int wallpaperIndex;
        private (WallpaperMode, string) loadedWallpaperSource;
        private readonly List<(string Text, string Author)> quotes = [];
        private int quoteIndex;

        private DateTime? lastRainAlertFor;          // rain start we already warned about
        private bool aqiAlerted;

        private readonly string? updatedFrom;
        private bool isCheckingUpdate;
        private (string File, UpdateInfo Info)? pendingUpdate;
        private bool dialogOpen;
        private WhatsNewForm? whatsNew;
        private SettingsForm? settingsForm;

        // ================================================================== setup

        private readonly bool openSettingsOnStart;

        public AppController(string? updatedFrom, bool openSettings = false)
        {
            this.updatedFrom = updatedFrom;
            openSettingsOnStart = openSettings;
            AppLog.Prune();
            AppLog.Info($"Start v{UpdateService.CurrentVersion.ToString(3)}" + (updatedFrom is null ? "" : $" (updated from {updatedFrom})"));

            ui = new Control();
            ui.CreateControl(); // installs the WinForms SynchronizationContext + gives us BeginInvoke

            Location    = locationService.ManualLocation ?? locationService.CachedLocation ?? LocationService.DefaultLocation;
            WeatherIcon = Visuals.CreatePlaceholderIcon(new Size(120, 120));

            tray = CreateTrayIcon();
            UpdateTray();

            refreshTimer.Tick   += async (_, _) => await UpdateWeatherAsync();
            loadingTimer.Tick   += (_, _) => { LoadingDots++; StatusChanged?.Invoke(); };
            wallpaperTimer.Tick += (_, _) => NextWallpaper();
            quoteTimer.Tick     += (_, _) => { if (quotes.Count > 0) { quoteIndex = (quoteIndex + 1) % quotes.Count; QuoteChanged?.Invoke(); } };
            updateTimer.Tick    += async (_, _) => { updateTimer.Interval = UpdateCheckMs; await CheckForUpdateAsync(manual: false); };
            clockTimer.Tick     += (_, _) =>
            {
                if (DateTime.Today == shownDate) return;
                shownDate = DateTime.Today;
                DataChanged?.Invoke();
            };
            clockTimer.Start();

            hotkey.Pressed += ToggleWidgets;
            RegisterHotkey();

            SystemEvents.PowerModeChanged      += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            L.Changed                          += OnLanguageChanged;
            AppSettings.Changed                += OnSettingsChanged;

            EnsureWidgets();
            InitStartupSetting();

            ui.BeginInvoke(new Action(async () => await StartAsync())); // runs once the message loop is up
        }

        private async Task StartAsync()
        {
            var weatherTask = UpdateWeatherAsync(relocate: true);
            _ = LoadWallpapersAsync();
            _ = LoadQuotesAsync();

            if (UpdateService.CanSelfUpdate)
            {
                _ = UpdateService.CleanupAsync();
                updateTimer.Start();
            }
            // Just restarted by the auto-updater → show what changed since the old version.
            if (updatedFrom is not null)
                ShowWhatsNew(Version.TryParse(updatedFrom, out var from) ? from : null);
            if (openSettingsOnStart) ShowSettings(); // "WeatherWidget.exe --settings"
            await weatherTask;
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout           = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10), // pick up DNS changes
                AutomaticDecompression   = DecompressionMethods.All,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            // MET Norway rejects requests without an identifying User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"WeatherWidget/{Application.ProductVersion}");
            return client;
        }

        /// <summary>Called from another thread when a second copy of the exe is started.</summary>
        public void RequestShow()
        {
            try { ui.BeginInvoke(ShowWidgets); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.PowerModeChanged       -= OnPowerModeChanged; // static events — must unhook
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                L.Changed           -= OnLanguageChanged;
                AppSettings.Changed -= OnSettingsChanged;

                foreach (var w in widgets.ToList()) w.Dispose();
                widgets.Clear();
                foreach (var t in (System.Windows.Forms.Timer[])[refreshTimer, loadingTimer, wallpaperTimer, quoteTimer, updateTimer, clockTimer]) t.Dispose();
                hotkey.Dispose();

                tray.Visible = false;
                tray.Icon?.Dispose();
                tray.ContextMenuStrip?.Dispose();
                tray.Dispose();

                foreach (var b in wallpapers) b.Dispose();
                foreach (var b in iconCache.Values) b.Dispose();
                WeatherIcon.Dispose();
                ui.Dispose();
            }
            base.Dispose(disposing);
        }

        // ================================================================== widgets / monitors

        /// <summary>One widget per monitor (or only the primary one) — follows plugging/unplugging screens.</summary>
        private void EnsureWidgets()
        {
            var screens = AppSettings.AllMonitors ? Screen.AllScreens : [Screen.PrimaryScreen ?? Screen.AllScreens[0]];
            var keys    = screens.Select(s => AppSettings.AllMonitors ? s.DeviceName : "single").ToHashSet();

            foreach (var w in widgets.Where(w => !keys.Contains(w.PositionKey)).ToList())
            {
                widgets.Remove(w);
                w.CloseForGood();
                w.Dispose();
            }
            foreach (var s in screens)
            {
                var key = AppSettings.AllMonitors ? s.DeviceName : "single";
                if (widgets.Any(w => w.PositionKey == key)) continue;
                var w = new WidgetForm(this, s, key, clampToScreen: AppSettings.AllMonitors);
                widgets.Add(w);
                if (widgetsShown) w.ShowWidget();
            }
            foreach (var w in widgets) w.EnsureOnScreen();
        }

        public void ShowWidgets()
        {
            widgetsShown = true;
            EnsureWidgets();
            foreach (var w in widgets) w.ShowWidget();
        }

        public void HideWidgets()
        {
            widgetsShown = false;
            foreach (var w in widgets) w.Hide();
            TryApplyPendingUpdate(); // an update waits until nobody is looking
        }

        public void ToggleWidgets()
        {
            if (widgets.Any(w => w.Visible)) HideWidgets();
            else ShowWidgets();
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            try { ui.BeginInvoke(EnsureWidgets); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }

        /// <summary>Shared, cached weather glyphs for the hourly/daily strips.</summary>
        public Bitmap GetIcon(WeatherKind kind, bool isDay, int size)
        {
            if (!iconCache.TryGetValue((kind, isDay, size), out var bmp))
                iconCache[(kind, isDay, size)] = bmp = Visuals.CreateWeatherIcon(kind, new Size(size, size), isDay);
            return bmp;
        }

        // ================================================================== power / language / settings

        /// <summary>After sleep/hibernate the data is stale: refresh shortly after resume (network needs a moment).</summary>
        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            try
            {
                ui.BeginInvoke(() =>
                {
                    relocateOnNextRefresh = true; // laptop may have moved while asleep
                    if (IsUpdating) return;
                    refreshTimer.Stop();
                    refreshTimer.Interval = ResumeDelayMs;
                    refreshTimer.Start();
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }

        private void OnLanguageChanged()
        {
            DataChanged?.Invoke();
            UpdateTray();
            // Place names come from the geocoder in the chosen language → fetch again (a manual pick keeps its name).
            if (Location.Source == LocationSource.Manual) return;
            if (Location.Source == LocationSource.Default) Location = LocationService.DefaultLocation;
            _ = UpdateWeatherAsync(relocate: true);
        }

        private void OnSettingsChanged()
        {
            RegisterHotkey();
            EnsureWidgets();
            if (loadedWallpaperSource != (AppSettings.Wallpaper, AppSettings.WallpaperFolder))
                _ = LoadWallpapersAsync();
            DataChanged?.Invoke(); // units, opacity, startup checkbox…
            UpdateTray();
        }

        public bool HotkeyActive { get; private set; }

        private void RegisterHotkey()
        {
            HotkeyActive = hotkey.Register(AppSettings.Hotkey);
            if (!HotkeyActive && AppSettings.Hotkey != Keys.None)
                AppLog.Warn($"Hotkey {HotkeyText(AppSettings.Hotkey)} is taken by another program");
        }

        public static string HotkeyText(Keys k)
        {
            if (k == Keys.None) return L.T("(tắt)", "(off)");
            var parts = new List<string>();
            if (k.HasFlag(Keys.Control)) parts.Add("Ctrl");
            if (k.HasFlag(Keys.Alt))     parts.Add("Alt");
            if (k.HasFlag(Keys.Shift))   parts.Add("Shift");
            parts.Add((k & Keys.KeyCode).ToString());
            return string.Join("+", parts);
        }

        // ================================================================== weather

        /// <param name="relocate">Re-detect the location first (startup, resume, manual refresh, city change).</param>
        public async Task UpdateWeatherAsync(bool relocate = false)
        {
            relocate |= relocateOnNextRefresh;
            if (IsUpdating)
            {
                // timer / tray menu / city change overlapped — run once more when the current update ends
                refreshQueued  = true;
                relocateQueued |= relocate;
                return;
            }
            IsUpdating = true;
            relocateOnNextRefresh = false;
            refreshTimer.Stop();
            LoadingDots = 0;
            loadingTimer.Start();
            StatusChanged?.Invoke();

            try
            {
                if (relocate)
                {
                    IsLocating = true;
                    StatusChanged?.Invoke();
                    var found = await locationService.ResolveAsync();
                    IsLocating = false;
                    ApplyLocation(found);
                }

                var loc         = Location;
                var weatherTask = weatherService.GetCurrentAsync(loc.Latitude, loc.Longitude);
                var aqiTask     = weatherService.GetUsAqiAsync(loc.Latitude, loc.Longitude);
                var data        = await weatherTask;
                var aqi         = await aqiTask;

                UsAqi = aqi ?? UsAqi; // keep last known AQI if this fetch failed
                LastFetchFailed = data is null;
                if (data is not null)
                {
                    Weather     = data;
                    LastUpdated = DateTime.Now;
                    var old = WeatherIcon;
                    WeatherIcon = Visuals.CreateWeatherIcon(data.Kind, new Size(120, 120), data.IsDay);
                    old.Dispose();
                    CheckAlerts(data);
                }
            }
            catch (Exception ex)
            {
                // Called from async-void handlers: an escaping exception would crash the app.
                AppLog.Error("Weather update failed", ex);
                LastFetchFailed = true;
            }
            finally
            {
                IsUpdating = false;
                IsLocating = false;
                loadingTimer.Stop();
                refreshTimer.Interval = LastFetchFailed ? RetryMs : RefreshMs;
                refreshTimer.Start();
                DataChanged?.Invoke();
                UpdateTray();

                if (refreshQueued)
                {
                    bool again = relocateQueued;
                    refreshQueued = relocateQueued = false;
                    ui.BeginInvoke(new Action(async () => await UpdateWeatherAsync(again)));
                }
            }
        }

        private void ApplyLocation(GeoLocation found)
        {
            // Moved somewhere else → old readings belong to the old place, don't show them under the new name.
            if (found.DistanceKm(Location) > 2)
            {
                Weather     = null;
                UsAqi       = null;
                LastUpdated = null;
                lastRainAlertFor = null;
                aqiAlerted  = false;
            }
            Location = found;
            DataChanged?.Invoke();
        }

        public string StatusLabel
        {
            get
            {
                if (IsUpdating)
                    return (IsLocating ? L.T("Đang định vị", "Locating") : L.T("Đang tải", "Loading")) + new string('.', LoadingDots % 4);
                if (LastFetchFailed)
                    return LastUpdated is { } t ? L.T($"Mất mạng · lần cuối {t:HH:mm}", $"Offline · last {t:HH:mm}")
                                                : L.T("Mất mạng · đang thử lại…", "Offline · retrying...");
                return LastUpdated is { } u ? L.T($"Cập nhật {u:HH:mm} · {Weather!.Source}", $"Updated {u:HH:mm} · {Weather!.Source}")
                                            : L.T("Cập nhật --:--", "Updated --:--");
            }
        }

        // ================================================================== alerts

        /// <summary>"Rain in ~20 min" and "air is unhealthy" — each warned once per event.</summary>
        private void CheckAlerts(WeatherData data)
        {
            if (AppSettings.RainAlert && !data.RainingNow && data.RainStartsAt is { } start)
            {
                var minutes = (int)Math.Round((start - data.LocalNow).TotalMinutes);
                bool newEvent = lastRainAlertFor is not { } prev || start > prev.AddHours(2);
                if (minutes <= RainAlertMinutes && newEvent)
                {
                    lastRainAlertFor = start;
                    Notify(minutes <= 5
                        ? L.T($"🌧 Sắp mưa tại {Location.Name} — mang ô nhé!", $"🌧 Rain is about to start in {Location.Name} — grab an umbrella!")
                        : L.T($"🌧 Khoảng {minutes} phút nữa có mưa tại {Location.Name} (≈ {start:HH:mm}).",
                              $"🌧 Rain expected in about {minutes} min in {Location.Name} (≈ {start:HH:mm})."),
                        ToolTipIcon.Info);
                    AppLog.Info($"Rain alert: {minutes} min");
                }
            }

            if (UsAqi is { } aqi)
            {
                if (aqi < AqiAlertReset) aqiAlerted = false;
                if (AppSettings.AqiAlert && aqi >= AqiAlertLevel && !aqiAlerted)
                {
                    aqiAlerted = true;
                    Notify(L.T($"😷 Không khí {Visuals.ClassifyAqi(aqi).Label.ToLower()} (AQI {aqi}) — nên đeo khẩu trang, hạn chế ra ngoài.",
                               $"😷 Air quality is {Visuals.ClassifyAqi(aqi).Label.ToLower()} (AQI {aqi}) — consider a mask and staying indoors."),
                           ToolTipIcon.Warning);
                    AppLog.Info($"AQI alert: {aqi}");
                }
            }
        }

        private void Notify(string text, ToolTipIcon icon) => tray.ShowBalloonTip(5000, "Weather Widget", text, icon);

        // ================================================================== location / cities

        private static string SourceLabel(LocationSource s) => s switch
        {
            LocationSource.Windows => L.T("định vị Windows", "Windows location"),
            LocationSource.IP      => L.T("theo IP", "by IP"),
            LocationSource.Manual  => L.T("chọn thủ công", "chosen manually"),
            LocationSource.Cached  => L.T("lần trước", "last known"),
            _                      => L.T("mặc định", "default"),
        };

        public async Task UseCityAsync(SavedCity city)
        {
            locationService.ManualLocation = new GeoLocation(city.Latitude, city.Longitude, city.Name, LocationSource.Manual);
            AppSettings.RememberCity(city);
            await UpdateWeatherAsync(relocate: true);
        }

        public async Task UseAutoLocationAsync()
        {
            locationService.ManualLocation = null;
            await UpdateWeatherAsync(relocate: true);
        }

        /// <summary>City search dialog; the pick becomes the location and goes to the saved list.</summary>
        public async Task PickCityAsync(IWin32Window? owner = null)
        {
            if (dialogOpen) return;
            CityResult? c;
            dialogOpen = true;
            try
            {
                using var dlg = new CityPickerForm(locationService);
                c = (owner is null ? dlg.ShowDialog() : dlg.ShowDialog(owner)) == DialogResult.OK ? dlg.Selected : null;
            }
            finally
            {
                dialogOpen = false;
            }
            if (c is not null) await UseCityAsync(new SavedCity(c.Latitude, c.Longitude, c.Name));
            TryApplyPendingUpdate();
        }

        // ================================================================== run at startup

        private static string StartupCommand => $"\"{Application.ExecutablePath}\"";

        /// <summary>
        /// Enable "run at startup" only on the very first launch, so unchecking it sticks.
        /// If an entry exists but points at an old exe path, repoint it to this exe.
        /// </summary>
        private static void InitStartupSetting()
        {
            try
            {
                using var settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
                bool firstRun = settings.GetValue("StartupInitialized") is null;
                using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

                if (run is not null && (firstRun || run.GetValue(AppName) is not null))
                    run.SetValue(AppName, StartupCommand);
                if (firstRun)
                    settings.SetValue("StartupInitialized", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Couldn't initialise run-at-startup", ex);
            }
        }

        public bool StartupEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                    var value = (key?.GetValue(AppName) as string ?? "").Trim('"');
                    return string.Equals(value, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                    if (key is null) return;
                    if (value) key.SetValue(AppName, StartupCommand);
                    else key.DeleteValue(AppName, throwOnMissingValue: false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Couldn't change run-at-startup", ex);
                }
                DataChanged?.Invoke(); // every widget's checkbox + the Settings window reflect the real state
            }
        }

        // ================================================================== auto-update / dialogs

        /// <param name="manual">From the tray menu: report the result and apply right away even if the widget is open.</param>
        private async Task CheckForUpdateAsync(bool manual)
        {
            if (!UpdateService.CanSelfUpdate)
            {
                if (manual) Notify(L.T("Bản đang chạy không phải bản phát hành (WeatherWidget.exe) nên không tự cập nhật.",
                                       "This isn't a release build (WeatherWidget.exe), so it doesn't self-update."), ToolTipIcon.Info);
                return;
            }
            if (pendingUpdate is not null)
            {
                TryApplyPendingUpdate(force: manual);
                return;
            }
            if (isCheckingUpdate) return;
            isCheckingUpdate = true;
            try
            {
                var info = await updateService.CheckAsync();
                if (info is null)
                {
                    if (manual) Notify(L.T($"Đang dùng bản mới nhất (v{UpdateService.CurrentVersion.ToString(3)}).",
                                           $"You're on the latest version (v{UpdateService.CurrentVersion.ToString(3)})."), ToolTipIcon.Info);
                    return;
                }
                AppLog.Info($"Update {info.Tag} found, downloading");
                if (manual) Notify(L.T($"Đang tải bản {info.Tag}…", $"Downloading {info.Tag}…"), ToolTipIcon.Info);

                var file = await updateService.DownloadAsync(info);
                pendingUpdate = (file, info);
                TryApplyPendingUpdate(force: manual);
            }
            catch (Exception ex)
            {
                // network / GitHub down / disk: try again at the next check
                AppLog.Warn("Update check/download failed", ex);
                if (manual) Notify(L.T("Không kiểm tra được bản cập nhật. Sẽ thử lại sau.",
                                       "Couldn't check for updates. Will try again later."), ToolTipIcon.Warning);
            }
            finally
            {
                isCheckingUpdate = false;
            }
        }

        /// <summary>Swap the exe and restart — only when nobody is looking at the widget / a dialog (unless forced).</summary>
        private void TryApplyPendingUpdate(bool force = false)
        {
            if (pendingUpdate is not { } p || dialogOpen || whatsNew is not null || settingsForm is not null) return;
            if (widgets.Any(w => w.Visible) && !force) return; // HideWidgets() will call us again

            try
            {
                var exe = UpdateService.Apply(p.File);
                pendingUpdate = null;
                AppLog.Info($"Installed {p.Info.Tag}, restarting");
                Program.RestartAfterExit = (exe, Program.UpdatedArgs(UpdateService.CurrentVersion));
                Application.Exit();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Couldn't install {p.Info.Tag}", ex);
                pendingUpdate = null; // files untouched/rolled back — retry on the next check
                try { File.Delete(p.File); } catch { }
                Notify(L.T($"Không cài được bản {p.Info.Tag} (thư mục chỉ đọc?).",
                           $"Couldn't install {p.Info.Tag} (read-only folder?)."), ToolTipIcon.Warning);
            }
        }

        /// <summary>Non-modal so the widget keeps working; only one window at a time.</summary>
        private void ShowWhatsNew(Version? updatedFrom)
        {
            if (whatsNew is { IsDisposed: false })
            {
                whatsNew.Activate();
                return;
            }
            whatsNew = new WhatsNewForm(updatedFrom);
            whatsNew.FormClosed += (_, _) => { whatsNew = null; TryApplyPendingUpdate(); };
            whatsNew.Show();
        }

        public void ShowSettings()
        {
            if (settingsForm is { IsDisposed: false })
            {
                settingsForm.Activate();
                return;
            }
            settingsForm = new SettingsForm(this);
            settingsForm.FormClosed += (_, _) => { settingsForm = null; TryApplyPendingUpdate(); };
            settingsForm.Show();
        }

        public static void OpenDonatePage() => OpenUrl(DonateUrl);

        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Couldn't open {url}", ex);
            }
        }

        // ================================================================== tray

        private NotifyIcon CreateTrayIcon()
        {
            var menu       = new ContextMenuStrip();
            var mnuToggle  = new ToolStripMenuItem("", null, (_, _) => ToggleWidgets());
            var mnuRefresh = new ToolStripMenuItem("", null, async (_, _) => await UpdateWeatherAsync(relocate: true));
            var mnuLocation = new ToolStripMenuItem();

            // 🌐 Language submenu — names in their own language so either is findable
            var mnuVi       = new ToolStripMenuItem("Tiếng Việt", null, (_, _) => L.Set(AppLanguage.Vietnamese));
            var mnuEn       = new ToolStripMenuItem("English",    null, (_, _) => L.Set(AppLanguage.English));
            var mnuLanguage = new ToolStripMenuItem();
            mnuLanguage.DropDownItems.AddRange([mnuVi, mnuEn]);

            var mnuSettings = new ToolStripMenuItem("", null, (_, _) => ShowSettings());
            var mnuAbout    = new ToolStripMenuItem($"Weather Widget v{UpdateService.CurrentVersion.ToString(3)}") { Enabled = false };
            var mnuWhatsNew = new ToolStripMenuItem("", null, (_, _) => ShowWhatsNew(null));
            var mnuUpdate   = new ToolStripMenuItem("", null, async (_, _) => await CheckForUpdateAsync(manual: true));
            var mnuBug      = new ToolStripMenuItem("", null, (_, _) => BugReport.Open());
            var mnuDonate   = new ToolStripMenuItem("", null, (_, _) => OpenDonatePage());
            var mnuExit     = new ToolStripMenuItem("", null, (_, _) => Application.Exit());

            menu.Items.AddRange([mnuToggle, mnuRefresh, mnuLocation, mnuLanguage, mnuSettings, new ToolStripSeparator(),
                                 mnuAbout, mnuWhatsNew, mnuUpdate, mnuBug, mnuDonate, new ToolStripSeparator(), mnuExit]);

            // All texts (and the city list) are rebuilt right before the menu shows → language/city changes need nothing else.
            menu.Opening += (_, _) =>
            {
                mnuToggle.Text   = widgets.Any(w => w.Visible) ? L.T("Ẩn Widget", "Hide widget") : L.T("Hiện Widget", "Show widget");
                if (HotkeyActive) mnuToggle.ShortcutKeyDisplayString = HotkeyText(AppSettings.Hotkey);
                else mnuToggle.ShortcutKeyDisplayString = null;
                mnuRefresh.Text  = L.T("🔄 Cập nhật thời tiết", "🔄 Refresh weather");
                mnuRefresh.Enabled = !IsUpdating;
                mnuLocation.Text = L.T("📍 Vị trí", "📍 Location");
                BuildLocationMenu(mnuLocation);
                mnuLanguage.Text = L.T("🌐 Ngôn ngữ", "🌐 Language");
                mnuVi.Checked    = L.IsVi;
                mnuEn.Checked    = !L.IsVi;
                mnuSettings.Text = L.T("⚙ Cài đặt…", "⚙ Settings…");
                mnuWhatsNew.Text = L.T("📝 Có gì mới", "📝 What's new");
                mnuUpdate.Text   = pendingUpdate is { } p ? L.T($"⬆ Cài bản {p.Info.Tag} ngay", $"⬆ Install {p.Info.Tag} now")
                                                          : L.T("⬆ Kiểm tra cập nhật", "⬆ Check for updates");
                mnuUpdate.Enabled = !isCheckingUpdate;
                mnuBug.Text      = L.T("🐞 Báo lỗi trên GitHub", "🐞 Report a bug on GitHub");
                mnuDonate.Text   = L.T("♥ Ủng hộ tác giả", "♥ Donate");
                mnuExit.Text     = L.T("Thoát", "Exit");
            };

            var icon = new NotifyIcon { Text = "Weather Widget", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleWidgets(); };
            return icon;
        }

        /// <summary>Current place · auto-detect · saved cities (one click to switch) · search.</summary>
        private void BuildLocationMenu(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();
            parent.DropDownItems.Add(new ToolStripMenuItem(
                L.T($"Hiện tại: {Location.Name} ({SourceLabel(Location.Source)})", $"Current: {Location.Name} ({SourceLabel(Location.Source)})"))
                { Enabled = false });
            parent.DropDownItems.Add(new ToolStripSeparator());
            parent.DropDownItems.Add(new ToolStripMenuItem(L.T("Tự động định vị", "Detect automatically"), null,
                async (_, _) => await UseAutoLocationAsync()) { Checked = Location.Source != LocationSource.Manual });

            var saved = AppSettings.SavedCities;
            if (saved.Count > 0)
            {
                parent.DropDownItems.Add(new ToolStripSeparator());
                foreach (var city in saved)
                {
                    bool current = Location.Source == LocationSource.Manual && city.Name == Location.Name &&
                                   Math.Abs(city.Latitude - Location.Latitude) < 0.01 && Math.Abs(city.Longitude - Location.Longitude) < 0.01;
                    parent.DropDownItems.Add(new ToolStripMenuItem(city.Name, null, async (_, _) => await UseCityAsync(city)) { Checked = current });
                }
            }
            parent.DropDownItems.Add(new ToolStripSeparator());
            parent.DropDownItems.Add(new ToolStripMenuItem(L.T("Chọn thành phố…", "Choose city…"), null, async (_, _) => await PickCityAsync()));
        }

        private void UpdateTray()
        {
            var w = Weather;
            string shortTemp = w is null ? "--" : Units.Temp(w.TempC);

            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode     = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    if (w is null)
                    {
                        using var bg = new SolidBrush(Color.FromArgb(220, 30, 144, 255));
                        g.FillEllipse(bg, 1, 1, 30, 30);
                        using var font = new Font("Segoe UI", 11f, FontStyle.Bold);
                        g.DrawString(shortTemp, font, Brushes.White, new RectangleF(1, 1, 30, 30), Visuals.Center);
                    }
                    else
                    {
                        using var bg = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
                        g.FillEllipse(bg, 0, 0, 32, 32);
                        g.DrawImage(WeatherIcon, 0, 0, 20, 20);
                        using var font = new Font("Segoe UI", shortTemp.Length > 3 ? 7f : 8.5f, FontStyle.Bold);
                        using var fmt  = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far };
                        g.DrawString(shortTemp, font, Brushes.White, new RectangleF(0, 0, 31, 31), fmt);
                    }
                }
                SetTrayIcon(bmp);
            }

            var tip = w is null
                ? $"{Location.Name}: --"
                : $"{Location.Name}: {Units.Temp(w.TempC, precise: true, suffix: true)}\n" +
                  $"{L.T("Độ ẩm", "Humidity")}: {w.Humidity:0}%\n" +
                  $"{L.T("Gió", "Wind")}: {Units.Wind(w.WindKph)}\n" +
                  $"{L.T("Cảm giác như", "Feels like")}: {Units.Temp(w.FeelsLikeC, suffix: true)}";
            if (UsAqi is { } aqi) tip += $"\nAQI: {aqi} ({Visuals.ClassifyAqi(aqi).Label})";
            tray.Text = tip.Length > 127 ? tip[..127] : tip;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>Bitmap.GetHicon leaks a GDI handle unless it's destroyed explicitly.</summary>
        private void SetTrayIcon(Bitmap bmp)
        {
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hIcon);
                var old = tray.Icon;
                tray.Icon = (Icon)temp.Clone(); // clone owns its own copy of the handle
                old?.Dispose();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        // ================================================================== wallpapers

        private async Task LoadWallpapersAsync()
        {
            var source = (AppSettings.Wallpaper, AppSettings.WallpaperFolder);
            loadedWallpaperSource = source;
            try
            {
                var target = new Size(WidgetForm.FormWidth, WidgetForm.ExpandedHeight); // covers the forecast area too
                List<Bitmap> bitmaps = [];

                if (source.Item1 == WallpaperMode.Online)
                {
                    string cacheDir = Path.Combine(Path.GetTempPath(), AppName);
                    Directory.CreateDirectory(cacheDir);
                    PruneCache(cacheDir, TimeSpan.FromDays(14));
                    var sources = await Task.WhenAll(FetchBingUrlsAsync(), FetchWallhavenUrlsAsync());
                    var loaded  = await Task.WhenAll(sources.SelectMany(s => s)
                        .Select(e => LoadScaledImageAsync(e.Url, Path.Combine(cacheDir, e.FileName), target)));
                    bitmaps = loaded.OfType<Bitmap>().ToList();
                }
                else if (source.Item1 == WallpaperMode.Folder && Directory.Exists(source.Item2))
                {
                    var files = Directory.EnumerateFiles(source.Item2)
                        .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp")
                        .Take(50).ToList();
                    var loaded = await Task.WhenAll(files.Select(f => Task.Run(() =>
                    {
                        try { return DecodeCover(File.ReadAllBytes(f), target); } catch { return null; }
                    })));
                    bitmaps = loaded.OfType<Bitmap>().ToList();
                }

                if (loadedWallpaperSource != source) // settings changed again while loading
                {
                    bitmaps.ForEach(b => b.Dispose());
                    return;
                }

                Random.Shared.Shuffle(CollectionsMarshal.AsSpan(bitmaps));
                var old = wallpapers.ToList();
                wallpapers.Clear();
                wallpapers.AddRange(bitmaps);
                wallpaperIndex = 0;
                WallpaperChanged?.Invoke(); // widgets swap BackgroundImage before the old bitmaps go away
                old.ForEach(b => b.Dispose());

                if (wallpapers.Count > 1) wallpaperTimer.Start();
                else wallpaperTimer.Stop();
                AppLog.Info($"Wallpapers: {source.Item1}, {wallpapers.Count} images");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Wallpaper loading failed", ex); // cosmetic only
            }
        }

        private void NextWallpaper()
        {
            if (wallpapers.Count == 0) return;
            wallpaperIndex = (wallpaperIndex + 1) % wallpapers.Count;
            WallpaperChanged?.Invoke();
        }

        /// <summary>Loads from cache or downloads, then scales/crops to <paramref name="target"/> (cover) to keep memory small.</summary>
        private static async Task<Bitmap?> LoadScaledImageAsync(string url, string cacheFile, Size target)
        {
            try
            {
                bool cached = File.Exists(cacheFile);
                byte[] bytes;
                if (cached)
                {
                    bytes = await File.ReadAllBytesAsync(cacheFile);
                    File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow); // keep recently used files out of pruning
                }
                else
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    bytes = await Http.GetByteArrayAsync(url, cts.Token);
                }

                var bmp = await Task.Run(() => DecodeCover(bytes, target));
                if (bmp is null)
                {
                    if (cached) File.Delete(cacheFile); // corrupt cache entry
                    return null;
                }
                if (!cached) await File.WriteAllBytesAsync(cacheFile, bytes); // only cache images that decode
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap? DecodeCover(byte[] bytes, Size target)
        {
            try
            {
                using var ms  = new MemoryStream(bytes);
                using var img = Image.FromStream(ms);
                float scale = Math.Max((float)target.Width / img.Width, (float)target.Height / img.Height);
                float w = img.Width * scale, h = img.Height * scale;

                var bmp = new Bitmap(target.Width, target.Height);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
                g.DrawImage(img, (target.Width - w) / 2, (target.Height - h) / 2, w, h);
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static void PruneCache(string dir, TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
                catch { /* in use / no access */ }
            }
        }

        private static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>Bing daily wallpapers at 1024x768 (≈70 KB each instead of full HD).</summary>
        private static async Task<List<(string Url, string FileName)>> FetchBingUrlsAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var stream = await Http.GetStreamAsync(
                    "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=en-US", cts.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

                var list = new List<(string, string)>();
                foreach (var el in doc.RootElement.GetProperty("images").EnumerateArray())
                {
                    var urlBase = el.GetProperty("urlbase").GetString();          // /th?id=OHR.Name_EN-US123
                    if (string.IsNullOrEmpty(urlBase)) continue;
                    var id = urlBase[(urlBase.IndexOf("id=", StringComparison.Ordinal) + 3)..];
                    list.Add(($"https://www.bing.com{urlBase}_1024x768.jpg", SafeFileName($"bing_{id}_1024x768.jpg")));
                }
                return list;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Bing wallpapers failed", ex);
                return [];
            }
        }

        /// <summary>Random SFW general wallpapers from Wallhaven (exactly 1920x1080 to cap download size).</summary>
        private static async Task<List<(string Url, string FileName)>> FetchWallhavenUrlsAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var stream = await Http.GetStreamAsync(
                    "https://wallhaven.cc/api/v1/search?categories=100&purity=100&sorting=random&resolutions=1920x1080", cts.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

                var list = new List<(string, string)>();
                foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray().Take(8))
                {
                    var url = el.GetProperty("path").GetString();
                    var id  = el.GetProperty("id").GetString();
                    if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(id)) continue;
                    var ext = Path.GetExtension(url);
                    list.Add((url, SafeFileName($"wh_{id}{(ext.Length > 0 ? ext : ".jpg")}")));
                }
                return list;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Wallhaven wallpapers failed", ex);
                return [];
            }
        }

        // ================================================================== quotes

        private async Task LoadQuotesAsync()
        {
            try
            {
                // ZenQuotes — free, no key, 50 quotes per call (English only)
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var stream = await Http.GetStreamAsync("https://zenquotes.io/api/quotes", cts.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

                var list = doc.RootElement.EnumerateArray()
                    .Select(el => (Text: el.GetProperty("q").GetString() ?? "", Author: el.GetProperty("a").GetString() ?? ""))
                    .Where(q => !string.IsNullOrWhiteSpace(q.Text))
                    .ToArray();
                if (list.Length == 0) return;

                Random.Shared.Shuffle(list);
                quotes.Clear();
                quotes.AddRange(list);
                quoteIndex = 0;
                QuoteChanged?.Invoke();
                quoteTimer.Start();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Quotes failed", ex); // quote panel just stays hidden
            }
        }
    }

    /// <summary>Message-only window that receives WM_HOTKEY for the global show/hide shortcut.</summary>
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312, Id = 0xB00B;
        private const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModNoRepeat = 0x4000;
        private bool registered;

        public event Action? Pressed;

        public HotkeyWindow() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>(Re)registers <paramref name="keys"/>; false if it's off or taken by another program.</summary>
        public bool Register(Keys keys)
        {
            if (registered) UnregisterHotKey(Handle, Id);
            registered = false;
            var key = keys & Keys.KeyCode;
            if (keys == Keys.None || key == Keys.None) return false;

            uint mods = ModNoRepeat;
            if (keys.HasFlag(Keys.Control)) mods |= ModControl;
            if (keys.HasFlag(Keys.Alt))     mods |= ModAlt;
            if (keys.HasFlag(Keys.Shift))   mods |= ModShift;
            registered = RegisterHotKey(Handle, Id, mods, (uint)key);
            return registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam == Id) Pressed?.Invoke();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (registered) UnregisterHotKey(Handle, Id);
            DestroyHandle();
        }
    }
}
