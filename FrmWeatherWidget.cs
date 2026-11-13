using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace frmweatherwid
{
    public sealed class FrmWeatherWidget : Form
    {
        // ------------------------------------------------------------------ config
        // Internal id for the Run registry value and the cache folder — kept as-is so existing installs keep their settings.
        private const string AppName         = "FrmWeatherWidget";
        private const string RunKeyPath      = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string SettingsKeyPath = @"Software\FrmWeatherWidget";

        private const int FormWidth      = 400;
        private const int BaseHeight     = 300;                   // collapsed
        private const int ForecastHeight = 116;                   // extra height when the forecast is open
        private const int ExpandedHeight = BaseHeight + ForecastHeight;

        private const int RefreshMs         = 30 * 60_000; // normal refresh
        private const int RetryMs           = 5 * 60_000;  // after every provider failed
        private const int ResumeDelayMs     = 15_000;      // after waking from sleep
        private const int WallpaperRotateMs = 60_000;
        private const int QuoteRotateMs     = 15_000;
        private const int ScrimAlpha        = 110;         // dark layer over the wallpaper (see OnPaint)

        // ------------------------------------------------------------------ shared resources
        private static readonly HttpClient Http = CreateHttpClient();

        private static readonly Font FontTemp     = new("Segoe UI", 24f,  FontStyle.Bold);
        private static readonly Font FontStats    = new("Segoe UI", 10f,  FontStyle.Bold);
        private static readonly Font FontAqi      = new("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font FontStatus   = new("Segoe UI", 9f,   FontStyle.Bold | FontStyle.Italic);
        private static readonly Font FontQuote    = new("Segoe UI", 7.5f, FontStyle.Italic);
        private static readonly Font FontAuthor   = new("Segoe UI", 7f,   FontStyle.Bold);
        private static readonly Font FontSmall    = new("Segoe UI", 9f,   FontStyle.Bold);
        private static readonly Font FontHint     = new("Segoe UI", 8f,   FontStyle.Bold);
        private static readonly Font FontDay      = new("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font FontDayTemp  = new("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font FontDaySmall = new("Segoe UI", 7.5f, FontStyle.Regular);
        private static readonly StringFormat CenterFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        private static readonly StringFormat QuoteFormat  = new() { Trimming = StringTrimming.EllipsisWord };

        private readonly WeatherService weatherService   = new(Http);
        private readonly LocationService locationService = new(Http);
        private GeoLocation location;

        // ------------------------------------------------------------------ controls
        private readonly PictureBox pictureWeather;
        private readonly Panel pnlCity, pnlTemperature, pnlStats, pnlAqi, pnlStatus, pnlQuote, pnlForecast;
        private readonly CheckBox chkStartup;
        private readonly Label lblHint, lblForecastToggle;
        private readonly NotifyIcon trayIcon;

        private readonly System.Windows.Forms.Timer refreshTimer   = new() { Interval = RefreshMs };
        private readonly System.Windows.Forms.Timer loadingTimer   = new() { Interval = 300 };
        private readonly System.Windows.Forms.Timer wallpaperTimer = new() { Interval = WallpaperRotateMs };
        private readonly System.Windows.Forms.Timer quoteTimer     = new() { Interval = QuoteRotateMs };
        private readonly System.Windows.Forms.Timer updateTimer    = new() { Interval = FirstUpdateCheckMs };

        // ------------------------------------------------------------------ auto-update
        private const int FirstUpdateCheckMs = 60_000;            // 1 min after start (don't slow startup)
        private const int UpdateCheckMs      = 6 * 60 * 60_000;   // then every 6 h
        private readonly UpdateService updateService = new(Http);
        private readonly string? updatedFrom;                     // set when we were just restarted by an update
        private bool isCheckingUpdate;
        private bool dialogOpen;
        private WhatsNewForm? whatsNew;
        private const string DonateUrl = "https://nguyenthanhdev.com/donate/";
        private (string File, UpdateInfo Info)? pendingUpdate;   // downloaded + verified, waiting to be applied

        // ------------------------------------------------------------------ state
        private WeatherData? weather;
        private int? usAqi;
        private DateTime? lastUpdated;
        private bool lastFetchFailed;
        private bool isUpdating;
        private bool isLocating;
        private bool relocateOnNextRefresh;          // set after resume from sleep
        private bool refreshQueued, relocateQueued;  // request that arrived while updating
        private int loadingDots;
        private bool started;
        private bool suppressStartupEvent;

        private Font? cityFont;                      // sized to fit the current city name
        private Color cityColor = Color.FromArgb(77, 184, 255);
        private Color cityShadow = Color.FromArgb(170, 0, 0, 0);

        private bool forecastOpen;
        private int? topBeforeExpand;                // where the widget was before opening pushed it up
        private readonly Dictionary<WeatherKind, Bitmap> smallIcons = [];

        private readonly List<Bitmap> wallpapers = [];
        private int wallpaperIndex;
        private readonly List<(string Text, string Author)> quotes = [];
        private int quoteIndex;

        private Point dragCursorStart, dragFormStart, lockedLocation = new(-1, -1);
        private bool dragging;

        // ================================================================== setup

        public FrmWeatherWidget(string? updatedFrom = null)
        {
            this.updatedFrom = updatedFrom;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            FormBorderStyle       = FormBorderStyle.None;
            ClientSize            = new Size(FormWidth, BaseHeight);
            Opacity               = 0.95;
            ShowInTaskbar         = false; // lives in the tray
            BackColor             = Color.FromArgb(20, 20, 20);
            StartPosition         = FormStartPosition.Manual;
            BackgroundImageLayout = ImageLayout.None; // wallpapers are pre-scaled to FormWidth x ExpandedHeight
            KeyPreview            = true;

            // Start with the last known place (instant), re-detected in OnStartedAsync.
            location = locationService.ManualLocation ?? locationService.CachedLocation ?? LocationService.DefaultLocation;

            pictureWeather = new PictureBox
            {
                SizeMode  = PictureBoxSizeMode.Zoom,
                Size      = new Size(120, 120),
                Location  = new Point(12, 12),
                BackColor = Color.Transparent,
            };
            pictureWeather.Image = CreatePlaceholderIcon(pictureWeather.Size);

            pnlCity        = CreatePill(new Rectangle(188, 8, 208, 62),  alpha: 0,   radius: 10, PaintCity);
            pnlQuote       = CreatePill(new Rectangle(8, 138, 178, 72),  alpha: 140, radius: 10, PaintQuote);
            pnlStats       = CreatePill(new Rectangle(190, 73, 202, 66), alpha: 170, radius: 10, PaintStats);
            pnlTemperature = CreatePill(new Rectangle(186, 148, 208, 48), alpha: 170, radius: 12, PaintTemperature);
            pnlAqi         = CreatePill(new Rectangle(186, 206, 208, 26), alpha: 120, radius: 10, PaintAqi);
            pnlStatus      = CreatePill(new Rectangle(100, 238, 200, 22), alpha: 130, radius: 10, PaintStatus);
            pnlForecast    = CreatePill(new Rectangle(8, BaseHeight + 2, FormWidth - 16, ForecastHeight - 10), alpha: 160, radius: 12, PaintForecast);
            pnlQuote.Visible    = false; // until quotes are loaded
            pnlForecast.Visible = false;
            SetCityLabel(location.Name);

            chkStartup = new CheckBox
            {
                AutoSize  = true,
                Font      = FontSmall,
                ForeColor = Color.LightSeaGreen,
                BackColor = Color.FromArgb(160, 0, 0, 0),
                Padding   = new Padding(6, 3, 6, 3),
            };
            chkStartup.CheckedChanged += ChkStartup_CheckedChanged;

            lblHint = new Label
            {
                AutoSize  = true,
                Font      = FontHint,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(100, 0, 0, 0),
                Padding   = new Padding(6, 4, 6, 4),
            };

            lblForecastToggle = new Label
            {
                AutoSize  = true,
                Font      = FontHint,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(170, 25, 100, 190),
                Padding   = new Padding(6, 4, 6, 4),
                Cursor    = Cursors.Hand,
            };
            lblForecastToggle.Click += (_, _) => ToggleForecast();

            Controls.AddRange([pictureWeather, chkStartup, lblHint, lblForecastToggle]);
            ApplyLanguageToControls();

            // Drag on the form and on every child
            foreach (Control c in Controls.Cast<Control>().Append(this))
            {
                c.MouseDown += OnDragStart;
                c.MouseMove += OnDragMove;
                c.MouseUp   += OnDragEnd;
            }

            KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                e.Handled = true;
                Hide();
            };

            refreshTimer.Tick   += async (_, _) => await UpdateWeatherAsync();
            loadingTimer.Tick   += (_, _) => { loadingDots++; pnlStatus.Invalidate(); };
            wallpaperTimer.Tick += (_, _) => ShowNextWallpaper();
            quoteTimer.Tick     += (_, _) => ShowNextQuote();
            updateTimer.Tick    += async (_, _) =>
            {
                updateTimer.Interval = UpdateCheckMs;
                await CheckForUpdateAsync(manual: false);
            };
            // An update downloaded while the widget was on screen is applied once it's hidden.
            VisibleChanged += (_, _) => { if (!Visible) TryApplyPendingUpdate(); };

            trayIcon = CreateTrayIcon();
            UpdateTray();
            UpdateCityColor();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            L.Changed += OnLanguageChanged;

            // Load doesn't fire because the form starts hidden — use HandleCreated instead.
            HandleCreated += async (_, _) =>
            {
                if (started) return; // handle can be recreated; only run once
                started = true;
                await OnStartedAsync();
            };
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

        /// <summary>Transparent owner-drawn panel with a rounded dark background (alpha 0 = no background).</summary>
        private Panel CreatePill(Rectangle bounds, int alpha, int radius, Action<Graphics, Rectangle> draw)
        {
            var panel = new BufferedPanel { BackColor = Color.Transparent, Bounds = bounds };
            panel.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                if (alpha > 0)
                {
                    using var bg   = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
                    using var path = RoundedPath(panel.ClientRectangle, radius);
                    g.FillPath(bg, path);
                }
                draw(g, panel.ClientRectangle);
            };
            Controls.Add(panel);
            return panel;
        }

        private sealed class BufferedPanel : Panel
        {
            public BufferedPanel() => DoubleBuffered = true;
        }

        private async Task OnStartedAsync()
        {
            if (Screen.PrimaryScreen is { } screen)
            {
                var area = screen.WorkingArea;
                Location = lockedLocation = new Point(area.Right - Width, area.Top + (area.Height - Height) / 2);
            }

            InitStartupSetting();

            // Weather first; wallpaper and quotes are cosmetic and load in the background.
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
            await weatherTask;
        }

        // Keep the form hidden on the very first Show() so it doesn't flash on startup.
        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated)
            {
                CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        internal void ShowWidget()
        {
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        private void ToggleWidget()
        {
            if (Visible) Hide();
            else ShowWidget();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged; // static events — must unhook
                L.Changed -= OnLanguageChanged;
                refreshTimer.Dispose();
                loadingTimer.Dispose();
                wallpaperTimer.Dispose();
                quoteTimer.Dispose();
                updateTimer.Dispose();

                trayIcon.Visible = false;
                trayIcon.Icon?.Dispose();
                trayIcon.ContextMenuStrip?.Dispose();
                trayIcon.Dispose();

                BackgroundImage = null;
                foreach (var bmp in wallpapers) bmp.Dispose();
                wallpapers.Clear();
                foreach (var bmp in smallIcons.Values) bmp.Dispose();
                smallIcons.Clear();
                cityFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>After sleep/hibernate the data is stale: refresh shortly after resume (network needs a moment).</summary>
        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    relocateOnNextRefresh = true; // laptop may have moved while asleep
                    if (isUpdating) return;
                    refreshTimer.Stop();
                    refreshTimer.Interval = ResumeDelayMs;
                    refreshTimer.Start();
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }

        // ================================================================== language

        /// <summary>Texts of the real controls (painted panels read L.T at paint time) + re-layout of the bottom row.</summary>
        private void ApplyLanguageToControls()
        {
            chkStartup.Text        = L.T("Tự khởi động", "Run at Startup");
            lblHint.Text           = L.T("Nhấn ESC để ẩn", "Press ESC to Hide");
            lblForecastToggle.Text = (forecastOpen ? "▲ " : "▼ ") + L.T("Dự báo", "Forecast");

            chkStartup.Size        = chkStartup.PreferredSize;
            lblHint.Size           = lblHint.PreferredSize;
            lblForecastToggle.Size = lblForecastToggle.PreferredSize;

            // Bottom row (inside the collapsed area): [checkbox] ...... [▼ Forecast] [Press ESC]
            // (Donate lives in the tray menu — the gap here is too narrow in some languages.)
            chkStartup.Location        = new Point(12, BaseHeight - chkStartup.Height - 8);
            lblHint.Location           = new Point(FormWidth - lblHint.Width - 8, BaseHeight - lblHint.Height - 8);
            lblForecastToggle.Location = new Point(lblHint.Left - lblForecastToggle.Width - 6, lblHint.Top);
        }

        private void OnLanguageChanged()
        {
            ApplyLanguageToControls();
            foreach (var p in (Panel[])[pnlCity, pnlStats, pnlAqi, pnlStatus, pnlTemperature, pnlForecast]) p.Invalidate();
            UpdateTray();

            // Place names come from the geocoder in the chosen language → fetch again (a manual pick keeps its name).
            if (location.Source == LocationSource.Manual) return;
            if (location.Source == LocationSource.Default) ApplyLocation(LocationService.DefaultLocation);
            _ = UpdateWeatherAsync(relocate: true);
        }

        // ================================================================== weather

        /// <param name="relocate">Re-detect the location first (startup, resume, manual refresh, city change).</param>
        private async Task UpdateWeatherAsync(bool relocate = false)
        {
            relocate |= relocateOnNextRefresh;
            if (isUpdating)
            {
                // timer / tray menu / city change overlapped — run once more when the current update ends
                refreshQueued  = true;
                relocateQueued |= relocate;
                return;
            }
            isUpdating = true;
            relocateOnNextRefresh = false;
            refreshTimer.Stop();
            loadingDots = 0;
            loadingTimer.Start();
            pnlStatus.Invalidate();

            try
            {
                if (relocate)
                {
                    isLocating = true;
                    pnlStatus.Invalidate();
                    var found = await locationService.ResolveAsync();
                    isLocating = false;
                    if (IsDisposed) return;
                    ApplyLocation(found);
                }

                var loc         = location;
                var weatherTask = weatherService.GetCurrentAsync(loc.Latitude, loc.Longitude);
                var aqiTask     = weatherService.GetUsAqiAsync(loc.Latitude, loc.Longitude);
                var data        = await weatherTask;
                var aqi         = await aqiTask;
                if (IsDisposed) return;

                usAqi = aqi ?? usAqi; // keep last known AQI if this fetch failed
                lastFetchFailed = data is null;
                if (data is not null)
                {
                    weather     = data;
                    lastUpdated = DateTime.Now;
                    var oldIcon = pictureWeather.Image;
                    pictureWeather.Image = CreateWeatherIcon(data.Kind, pictureWeather.Size);
                    oldIcon?.Dispose();
                }
            }
            catch
            {
                // Called from async-void handlers: an escaping exception would crash the app.
                lastFetchFailed = true;
            }
            finally
            {
                isUpdating = false;
                isLocating = false;
                if (!IsDisposed)
                {
                    loadingTimer.Stop();
                    refreshTimer.Interval = lastFetchFailed ? RetryMs : RefreshMs;
                    refreshTimer.Start();

                    pnlTemperature.Invalidate();
                    pnlStats.Invalidate();
                    pnlAqi.Invalidate();
                    pnlStatus.Invalidate();
                    pnlForecast.Invalidate();
                    UpdateTray();

                    if (refreshQueued)
                    {
                        bool again = relocateQueued;
                        refreshQueued = relocateQueued = false;
                        BeginInvoke(new Action(async () => await UpdateWeatherAsync(again)));
                    }
                }
            }
        }

        private void ApplyLocation(GeoLocation found)
        {
            // Moved somewhere else → old readings belong to the old place, don't show them under the new name.
            if (found.DistanceKm(location) > 2)
            {
                weather     = null;
                usAqi       = null;
                lastUpdated = null;
            }
            location = found;
            SetCityLabel(found.Name);
        }

        /// <summary>Largest font (28pt → 12pt) that fits the city name.</summary>
        private void SetCityLabel(string name)
        {
            Font font;
            for (float size = 28f; ; size -= 2f)
            {
                font = new Font("Segoe UI", size, FontStyle.Bold);
                if (size <= 12f || TextRenderer.MeasureText(name, font).Width <= pnlCity.Width - 8) break;
                font.Dispose();
            }
            cityFont?.Dispose();
            cityFont = font;
            pnlCity.Invalidate();
        }

        private static string SourceLabel(LocationSource s) => s switch
        {
            LocationSource.Windows => L.T("định vị Windows", "Windows location"),
            LocationSource.IP      => L.T("theo IP", "by IP"),
            LocationSource.Manual  => L.T("chọn thủ công", "chosen manually"),
            LocationSource.Cached  => L.T("lần trước", "last known"),
            _                      => L.T("mặc định", "default"),
        };

        private async Task PickCityAsync()
        {
            if (dialogOpen) return;
            CityResult? c;
            dialogOpen = true;
            try
            {
                using var dlg = new CityPickerForm(locationService);
                c = dlg.ShowDialog() == DialogResult.OK ? dlg.Selected : null;
            }
            finally
            {
                dialogOpen = false;
            }
            if (c is null)
            {
                TryApplyPendingUpdate();
                return;
            }
            locationService.ManualLocation = new GeoLocation(c.Latitude, c.Longitude, c.Name, LocationSource.Manual);
            await UpdateWeatherAsync(relocate: true);
            TryApplyPendingUpdate();
        }

        // ================================================================== auto-update

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
                if (manual) Notify(L.T($"Đang tải bản {info.Tag}…", $"Downloading {info.Tag}…"), ToolTipIcon.Info);

                var file = await updateService.DownloadAsync(info);
                if (IsDisposed) return;
                pendingUpdate = (file, info);
                TryApplyPendingUpdate(force: manual);
            }
            catch
            {
                // network / GitHub down / disk: try again at the next check
                if (manual && !IsDisposed) Notify(L.T("Không kiểm tra được bản cập nhật. Sẽ thử lại sau.",
                                                      "Couldn't check for updates. Will try again later."), ToolTipIcon.Warning);
            }
            finally
            {
                isCheckingUpdate = false;
            }
        }

        /// <summary>Swap the exe and restart — only when nobody is looking at the widget (unless forced).</summary>
        private void TryApplyPendingUpdate(bool force = false)
        {
            if (pendingUpdate is not { } p || dialogOpen || whatsNew is not null || IsDisposed) return;
            if (Visible && !force) return; // VisibleChanged will call us again when hidden

            try
            {
                var exe = UpdateService.Apply(p.File);
                pendingUpdate = null;
                Program.RestartAfterExit = (exe, Program.UpdatedArgs(UpdateService.CurrentVersion));
                Application.Exit();
            }
            catch
            {
                pendingUpdate = null; // files untouched/rolled back — retry on the next check
                try { File.Delete(p.File); } catch { }
                Notify(L.T($"Không cài được bản {p.Info.Tag} (thư mục chỉ đọc?).",
                           $"Couldn't install {p.Info.Tag} (read-only folder?)."), ToolTipIcon.Warning);
            }
        }

        private void Notify(string text, ToolTipIcon icon) => trayIcon.ShowBalloonTip(4000, "Weather Widget", text, icon);

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

        private void OpenDonatePage()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DonateUrl) { UseShellExecute = true });
            }
            catch
            {
                Notify(L.T("Không mở được trình duyệt: ", "Couldn't open the browser: ") + DonateUrl, ToolTipIcon.Warning);
            }
        }

        /// <summary>US AQI bands; Vietnamese names follow the VN AQI scale (Tốt → Nguy hại).</summary>
        private static (int Index, string Label) ClassifyAqi(int aqi) => aqi switch
        {
            <= 50  => (1, L.T("Tốt", "Good")),
            <= 100 => (2, L.T("Trung bình", "Moderate")),
            <= 150 => (3, L.T("Kém", "Unhealthy (Sensitive)")),
            <= 200 => (4, L.T("Xấu", "Unhealthy")),
            <= 300 => (5, L.T("Rất xấu", "Very Unhealthy")),
            _      => (6, L.T("Nguy hại", "Hazardous")),
        };

        private string TempLabel => weather is null ? (lastFetchFailed ? L.T("Lỗi", "Err") : "-- °C") : $"{weather.TempC:0.#}°C";

        private string StatusLabel
        {
            get
            {
                if (isUpdating)
                    return (isLocating ? L.T("Đang định vị", "Locating") : L.T("Đang tải", "Loading")) + new string('.', loadingDots % 4);
                if (lastFetchFailed)
                    return lastUpdated is { } t ? L.T($"Mất mạng · lần cuối {t:HH:mm}", $"Offline · last {t:HH:mm}")
                                                : L.T("Mất mạng · đang thử lại…", "Offline · retrying...");
                return lastUpdated is { } u ? L.T($"Cập nhật {u:HH:mm} · {weather!.Source}", $"Updated {u:HH:mm} · {weather!.Source}")
                                            : L.T("Cập nhật --:--", "Updated --:--");
            }
        }

        // ================================================================== forecast drop-down

        private void ToggleForecast()
        {
            forecastOpen = !forecastOpen;
            var area = Screen.FromControl(this).WorkingArea;

            if (forecastOpen)
            {
                Height = ExpandedHeight;
                // Not enough room below → move up so the whole forecast is visible; remember where we were.
                if (Bottom > area.Bottom)
                {
                    topBeforeExpand = Top;
                    Top = Math.Max(area.Top, area.Bottom - Height);
                }
            }
            else
            {
                Height = BaseHeight;
                if (topBeforeExpand is { } top) Top = top;
                topBeforeExpand = null;
            }

            pnlForecast.Visible = forecastOpen;
            lockedLocation = Location;
            ApplyLanguageToControls(); // arrow ▼/▲
            Invalidate();
        }

        private void PaintForecast(Graphics g, Rectangle r)
        {
            var days = weather?.Daily ?? [];
            using var dim = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            if (days.Count == 0)
            {
                g.DrawString(L.T("Chưa có dữ liệu dự báo", "No forecast data yet"), FontDay, dim, r, CenterFormat);
                return;
            }

            var today   = DateOnly.FromDateTime(DateTime.Now);
            int count   = Math.Min(days.Count, WeatherService.ForecastDays);
            float colW  = (float)r.Width / count;
            using var sep  = new Pen(Color.FromArgb(50, 255, 255, 255));
            using var rain = new SolidBrush(Color.FromArgb(130, 200, 255));

            for (int i = 0; i < count; i++)
            {
                var d = days[i];
                float x = i * colW;
                if (i > 0) g.DrawLine(sep, x, 12, x, r.Height - 12);

                string dayName = d.Date == today
                    ? L.T("Hôm nay", "Today")
                    : L.Culture.DateTimeFormat.GetAbbreviatedDayName(d.Date.DayOfWeek);
                g.DrawString(dayName, FontDay, Brushes.White, new RectangleF(x, 4, colW, 18), CenterFormat);

                var icon = GetSmallIcon(d.Kind);
                g.DrawImage(icon, x + (colW - icon.Width) / 2f, 22);

                using (var hot = new SolidBrush(GetTemperatureColor(d.MaxC)))
                {
                    // "28° / 21°" — max in its temperature color, min dimmed
                    string max = $"{Math.Round(d.MaxC)}°", min = $" / {Math.Round(d.MinC)}°";
                    var maxW = g.MeasureString(max, FontDayTemp).Width;
                    var minW = g.MeasureString(min, FontDaySmall).Width;
                    float start = x + (colW - maxW - minW) / 2f;
                    g.DrawString(max, FontDayTemp, hot, start, 58);
                    g.DrawString(min, FontDaySmall, dim, start + maxW - 4, 61);
                }

                if (d.RainChance is { } chance)
                {
                    string pct = $"{chance}%";
                    float textW = g.MeasureString(pct, FontDaySmall).Width, dropH = 9;
                    float start = x + (colW - textW - dropH * 0.7f - 1) / 2f;
                    DrawDrop(g, rain, start, 83, dropH);
                    g.DrawString(pct, FontDaySmall, rain, start + dropH * 0.7f, 80);
                }
            }
        }

        private Bitmap GetSmallIcon(WeatherKind kind)
        {
            if (!smallIcons.TryGetValue(kind, out var bmp))
                smallIcons[kind] = bmp = CreateWeatherIcon(kind, new Size(34, 34));
            return bmp;
        }

        // ================================================================== painting

        /// <summary>City name with a soft outline, in whichever color reads best on the current wallpaper.</summary>
        private void PaintCity(Graphics g, Rectangle r)
        {
            if (cityFont is null) return;
            var name = location.Name;
            var size = g.MeasureString(name, cityFont);
            float x = 2, y = (r.Height - size.Height) / 2f;

            using (var shadow = new SolidBrush(cityShadow))
            {
                foreach (var (dx, dy) in (ReadOnlySpan<(int, int)>)[(-1, 0), (1, 0), (0, -1), (0, 1), (2, 2)])
                    g.DrawString(name, cityFont, shadow, x + dx, y + dy);
            }
            using var brush = new SolidBrush(cityColor);
            g.DrawString(name, cityFont, brush, x, y);
        }

        /// <summary>
        /// Average the wallpaper behind the city name (after the dark scrim) and pick the text color
        /// with the best contrast — the brand blue when it's readable enough, otherwise white / gold / navy.
        /// </summary>
        private void UpdateCityColor()
        {
            var bg = AverageBackground(pnlCity.Bounds);

            Color brand = Color.FromArgb(77, 184, 255);
            Color best  = brand;
            if (Contrast(brand, bg) < 4.5)
            {
                Color[] candidates = [Color.White, Color.FromArgb(255, 214, 79), Color.FromArgb(10, 37, 64)];
                best = candidates.MaxBy(c => Contrast(c, bg));
            }

            cityColor  = best;
            cityShadow = Luminance(best) > 0.4 ? Color.FromArgb(170, 0, 0, 0) : Color.FromArgb(150, 255, 255, 255);
            pnlCity.Invalidate();
        }

        private Color AverageBackground(Rectangle area)
        {
            Color raw = BackColor;
            if (BackgroundImage is Bitmap bmp)
            {
                var rect = Rectangle.Intersect(area, new Rectangle(Point.Empty, bmp.Size));
                long r = 0, g = 0, b = 0, n = 0;
                for (int y = rect.Top; y < rect.Bottom; y += 4)
                    for (int x = rect.Left; x < rect.Right; x += 4)
                    {
                        var c = bmp.GetPixel(x, y);
                        r += c.R; g += c.G; b += c.B; n++;
                    }
                if (n > 0) raw = Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
            }
            // OnPaint lays a black scrim (alpha ScrimAlpha) over everything
            double keep = 1 - ScrimAlpha / 255.0;
            return Color.FromArgb((int)(raw.R * keep), (int)(raw.G * keep), (int)(raw.B * keep));
        }

        private static double Luminance(Color c)
        {
            static double Ch(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        /// <summary>WCAG contrast ratio (1 … 21).</summary>
        private static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private void PaintTemperature(Graphics g, Rectangle r)
        {
            var color = weather is null ? Color.Gainsboro : GetTemperatureColor(weather.TempC);
            using var brush = new SolidBrush(color);
            g.DrawString(TempLabel, FontTemp, brush, 6f, 4f);
        }

        private void PaintStats(Graphics g, Rectangle r)
        {
            var w = weather;
            string[] lines =
            [
                $"{L.T("Độ ẩm", "Humidity")}: {(w is null ? "--" : $"{w.Humidity:0}%")}",
                $"{L.T("Gió", "Wind")}: {(w is null ? "--" : $"{w.WindKph:0} km/h")}",
                $"{L.T("Cảm giác như", "Feels like")}: {(w is null ? "--" : $"{w.FeelsLikeC:0}")}°C",
            ];
            using var brush = new SolidBrush(Color.LightSeaGreen);
            for (int i = 0; i < lines.Length; i++)
                g.DrawString(lines[i], FontStats, brush, 6f, 2f + i * 22);
        }

        private void PaintAqi(Graphics g, Rectangle r)
        {
            int index = 0;
            string text = "AQI: --";
            if (usAqi is { } aqi)
            {
                (index, var label) = ClassifyAqi(aqi);
                text = L.T($"AQI: {aqi}  ({label})", $"Air quality: {aqi}  ({label})");
            }

            var dotColor = index switch
            {
                1 => Color.FromArgb( 80, 220,  80),
                2 => Color.FromArgb(255, 220,  50),
                3 => Color.FromArgb(255, 160,  30),
                4 => Color.FromArgb(255,  60,  60),
                5 => Color.FromArgb(180,  50, 180),
                6 => Color.FromArgb(180,  30,  30),
                _ => Color.Gray,
            };
            using (var dot = new SolidBrush(dotColor))
                g.FillEllipse(dot, 8, 8, 10, 10);
            g.DrawString(text, FontAqi, Brushes.White, 24f, 4f);
        }

        private void PaintStatus(Graphics g, Rectangle r)
        {
            using var brush = new SolidBrush(lastFetchFailed && !isUpdating ? Color.Salmon : Color.LightSeaGreen);
            g.DrawString(StatusLabel, FontStatus, brush, r, CenterFormat);
        }

        private void PaintQuote(Graphics g, Rectangle r)
        {
            if (quotes.Count == 0) return;
            var (text, author) = quotes[quoteIndex];

            using var brushText   = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
            using var brushAuthor = new SolidBrush(Color.FromArgb(200, 180, 220, 255));
            g.DrawString(text, FontQuote, brushText, new RectangleF(6, 4, r.Width - 12, r.Height - 18), QuoteFormat);
            if (!string.IsNullOrEmpty(author))
                g.DrawString($"— {author}", FontAuthor, brushAuthor, 6f, r.Height - 14f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Dark scrim so text stays readable on any wallpaper
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var scrim = new SolidBrush(Color.FromArgb(ScrimAlpha, 0, 0, 0));
            using var path  = RoundedPath(ClientRectangle, 14);
            e.Graphics.FillPath(scrim, path);
        }

        private static GraphicsPath RoundedPath(Rectangle rect, int d)
        {
            var path = new GraphicsPath();
            path.AddArc(rect.Left,      rect.Top,        d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top,        d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d,   0, 90);
            path.AddArc(rect.Left,      rect.Bottom - d, d, d,  90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color GetTemperatureColor(double tempC) => tempC switch
        {
            <= 0  => Color.FromArgb(100, 180, 255), // freezing — ice blue
            <= 10 => Color.FromArgb( 80, 220, 255), // cold     — cyan
            <= 18 => Color.FromArgb(100, 255, 200), // cool     — mint
            <= 25 => Color.FromArgb(255, 230,  60), // mild     — yellow
            <= 32 => Color.FromArgb(255, 150,  30), // warm     — orange
            _     => Color.FromArgb(255,  70,  40), // hot      — red-orange
        };

        private static Bitmap CreateWeatherIcon(WeatherKind kind, Size size)
        {
            var emoji = kind switch
            {
                WeatherKind.Clear        => "☀️",
                WeatherKind.PartlyCloudy => "⛅",
                WeatherKind.Cloudy       => "☁️",
                WeatherKind.Fog          => "☁️", // 🌫️ renders as a framed blob in GDI+ → cloud + mist lines below
                WeatherKind.Drizzle      => "🌦️",
                WeatherKind.Rain         => "🌧️",
                WeatherKind.Snow         => "🌨️",
                WeatherKind.Thunder      => "⛈️",
                _                        => "🌡️",
            };

            var bmp = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.SmoothingMode     = SmoothingMode.AntiAlias;

            bool fog = kind == WeatherKind.Fog;
            var area = fog ? new RectangleF(0, 0, size.Width, size.Height * 0.72f) : new RectangleF(0, 0, size.Width, size.Height);
            using var font = new Font("Segoe UI Emoji", size.Height * (fog ? 0.42f : 0.52f));
            g.DrawString(emoji, font, Brushes.White, area, CenterFormat);

            if (fog)
            {
                using var pen = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1.5f, size.Height * 0.06f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                for (int i = 0; i < 3; i++)
                {
                    float y = size.Height * (0.70f + i * 0.11f), inset = size.Width * (i == 1 ? 0.18f : 0.26f);
                    g.DrawLine(pen, inset, y, size.Width - inset, y);
                }
            }
            return bmp;
        }

        /// <summary>Small rain-drop glyph (the 💧 emoji has no glyph in Segoe UI and shows as a box).</summary>
        private static void DrawDrop(Graphics g, Brush brush, float x, float y, float h)
        {
            float w = h * 0.7f;
            using var path = new GraphicsPath();
            path.AddLine(x + w / 2, y, x + w, y + h * 0.58f);
            path.AddArc(x, y + h - w, w, w, 0, 180);
            path.CloseFigure();
            g.FillPath(brush, path);
        }

        private static Bitmap CreatePlaceholderIcon(Size size)
        {
            var bmp = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Color.FromArgb(240, 240, 200));
            int d = Math.Min(size.Width, size.Height) - 12;
            g.FillEllipse(b, 6, 6, d, d);
            return bmp;
        }

        // ================================================================== tray

        private NotifyIcon CreateTrayIcon()
        {
            var menu       = new ContextMenuStrip();
            var mnuToggle  = new ToolStripMenuItem("", null, (_, _) => ToggleWidget());
            var mnuRefresh = new ToolStripMenuItem();
            mnuRefresh.Click += async (_, _) =>
            {
                mnuRefresh.Enabled = false;
                try { await UpdateWeatherAsync(relocate: true); }
                finally { mnuRefresh.Enabled = true; }
            };

            // 📍 Location submenu
            var mnuCurrent = new ToolStripMenuItem { Enabled = false };
            var mnuAuto    = new ToolStripMenuItem("", null, async (_, _) =>
            {
                locationService.ManualLocation = null;
                await UpdateWeatherAsync(relocate: true);
            });
            var mnuPick     = new ToolStripMenuItem("", null, async (_, _) => await PickCityAsync());
            var mnuLocation = new ToolStripMenuItem();
            mnuLocation.DropDownItems.AddRange([mnuCurrent, new ToolStripSeparator(), mnuAuto, mnuPick]);

            // 🌐 Language submenu — names in their own language so either is findable
            var mnuVi       = new ToolStripMenuItem("Tiếng Việt", null, (_, _) => L.Set(AppLanguage.Vietnamese));
            var mnuEn       = new ToolStripMenuItem("English",    null, (_, _) => L.Set(AppLanguage.English));
            var mnuLanguage = new ToolStripMenuItem();
            mnuLanguage.DropDownItems.AddRange([mnuVi, mnuEn]);

            var mnuUpdate   = new ToolStripMenuItem("", null, async (_, _) => await CheckForUpdateAsync(manual: true));
            var mnuWhatsNew = new ToolStripMenuItem("", null, (_, _) => ShowWhatsNew(null));
            var mnuDonate   = new ToolStripMenuItem("", null, (_, _) => OpenDonatePage());
            var mnuAbout    = new ToolStripMenuItem($"Weather Widget v{UpdateService.CurrentVersion.ToString(3)}") { Enabled = false };
            var mnuExit     = new ToolStripMenuItem("", null, (_, _) => Application.Exit());

            menu.Items.AddRange([mnuToggle, mnuRefresh, mnuLocation, mnuLanguage, new ToolStripSeparator(),
                                 mnuAbout, mnuWhatsNew, mnuUpdate, mnuDonate, new ToolStripSeparator(), mnuExit]);

            // All texts are (re)set right before the menu shows, so a language switch needs nothing else.
            menu.Opening += (_, _) =>
            {
                mnuToggle.Text    = Visible ? L.T("Ẩn Widget", "Hide widget") : L.T("Hiện Widget", "Show widget");
                mnuRefresh.Text   = L.T("🔄 Cập nhật thời tiết", "🔄 Refresh weather");
                mnuLocation.Text  = L.T("📍 Vị trí", "📍 Location");
                mnuCurrent.Text   = L.T($"Hiện tại: {location.Name} ({SourceLabel(location.Source)})",
                                        $"Current: {location.Name} ({SourceLabel(location.Source)})");
                mnuAuto.Text      = L.T("Tự động định vị", "Detect automatically");
                mnuAuto.Checked   = location.Source != LocationSource.Manual;
                mnuPick.Text      = L.T("Chọn thành phố…", "Choose city…");
                mnuLanguage.Text  = L.T("🌐 Ngôn ngữ", "🌐 Language");
                mnuVi.Checked     = L.IsVi;
                mnuEn.Checked     = !L.IsVi;
                mnuUpdate.Text    = pendingUpdate is { } p ? L.T($"⬆ Cài bản {p.Info.Tag} ngay", $"⬆ Install {p.Info.Tag} now")
                                                           : L.T("⬆ Kiểm tra cập nhật", "⬆ Check for updates");
                mnuUpdate.Enabled = !isCheckingUpdate;
                mnuWhatsNew.Text  = L.T("📝 Có gì mới", "📝 What's new");
                mnuDonate.Text    = L.T("♥ Ủng hộ tác giả", "♥ Donate");
                mnuExit.Text      = L.T("Thoát", "Exit");
            };

            var icon = new NotifyIcon { Text = "Weather Widget", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleWidget(); };
            return icon;
        }

        private void UpdateTray()
        {
            var w = weather;
            string shortTemp = w is null ? "--" : $"{Math.Round(w.TempC)}°";

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
                        g.DrawString(shortTemp, font, Brushes.White, new RectangleF(1, 1, 30, 30), CenterFormat);
                    }
                    else
                    {
                        using var bg = new SolidBrush(Color.FromArgb(220, 20, 20, 20));
                        g.FillEllipse(bg, 0, 0, 32, 32);
                        if (pictureWeather.Image is { } img) g.DrawImage(img, 0, 0, 20, 20);
                        using var font = new Font("Segoe UI", shortTemp.Length > 3 ? 7f : 8.5f, FontStyle.Bold);
                        using var fmt  = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far };
                        g.DrawString(shortTemp, font, Brushes.White, new RectangleF(0, 0, 31, 31), fmt);
                    }
                }
                SetTrayIcon(bmp);
            }

            var tip = w is null
                ? $"{location.Name}: --"
                : $"{location.Name}: {w.TempC:0.#}°C\n" +
                  $"{L.T("Độ ẩm", "Humidity")}: {w.Humidity:0}%\n" +
                  $"{L.T("Gió", "Wind")}: {w.WindKph:0} km/h\n" +
                  $"{L.T("Cảm giác như", "Feels like")}: {w.FeelsLikeC:0}°C";
            if (usAqi is { } aqi) tip += $"\nAQI: {aqi} ({ClassifyAqi(aqi).Label})";
            trayIcon.Text = tip.Length > 127 ? tip[..127] : tip;
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
                var old = trayIcon.Icon;
                trayIcon.Icon = (Icon)temp.Clone(); // clone owns its own copy of the handle
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
            try
            {
                string cacheDir = Path.Combine(Path.GetTempPath(), AppName);
                Directory.CreateDirectory(cacheDir);
                PruneCache(cacheDir, TimeSpan.FromDays(14));

                var sources = await Task.WhenAll(FetchBingUrlsAsync(), FetchWallhavenUrlsAsync());
                var target  = new Size(FormWidth, ExpandedHeight); // covers the forecast area too
                var loaded  = await Task.WhenAll(sources.SelectMany(s => s)
                    .Select(e => LoadScaledImageAsync(e.Url, Path.Combine(cacheDir, e.FileName), target)));

                var bitmaps = loaded.OfType<Bitmap>().ToList();
                if (IsDisposed)
                {
                    bitmaps.ForEach(b => b.Dispose());
                    return;
                }
                if (bitmaps.Count == 0) return;

                Random.Shared.Shuffle(CollectionsMarshal.AsSpan(bitmaps));

                BackgroundImage = null;
                foreach (var old in wallpapers) old.Dispose();
                wallpapers.Clear();
                wallpapers.AddRange(bitmaps);
                wallpaperIndex = -1;
                ShowNextWallpaper();
                if (wallpapers.Count > 1) wallpaperTimer.Start();
            }
            catch
            {
                // cosmetic only
            }
        }

        private void ShowNextWallpaper()
        {
            if (wallpapers.Count == 0) return;
            wallpaperIndex = (wallpaperIndex + 1) % wallpapers.Count;
            BackgroundImage = wallpapers[wallpaperIndex]; // owned by the pool — don't dispose here
            UpdateCityColor();
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
            catch
            {
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
            catch
            {
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
                if (list.Length == 0 || IsDisposed) return;

                Random.Shared.Shuffle(list);
                quotes.Clear();
                quotes.AddRange(list);
                quoteIndex = 0;
                pnlQuote.Visible = true;
                pnlQuote.Invalidate();
                quoteTimer.Start();
            }
            catch
            {
                // quote panel just stays hidden
            }
        }

        private void ShowNextQuote()
        {
            if (quotes.Count == 0) return;
            quoteIndex = (quoteIndex + 1) % quotes.Count;
            pnlQuote.Invalidate();
        }

        // ================================================================== run at startup

        private static string StartupCommand => $"\"{Application.ExecutablePath}\"";

        /// <summary>
        /// Enable "run at startup" only on the very first launch, so unchecking it sticks.
        /// If an entry exists but points at an old exe path, repoint it to this exe.
        /// </summary>
        private void InitStartupSetting()
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
            catch
            {
                // no registry access — leave as is
            }

            suppressStartupEvent = true;
            chkStartup.Checked   = IsStartupEnabled();
            suppressStartupEvent = false;
        }

        private void ChkStartup_CheckedChanged(object? sender, EventArgs e)
        {
            if (suppressStartupEvent) return;
            SetStartup(chkStartup.Checked);

            bool actual = IsStartupEnabled();
            if (actual == chkStartup.Checked) return;
            suppressStartupEvent = true; // write failed → reflect reality
            chkStartup.Checked   = actual;
            suppressStartupEvent = false;
        }

        private static void SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key is null) return;
                if (enable) key.SetValue(AppName, StartupCommand);
                else key.DeleteValue(AppName, throwOnMissingValue: false);
            }
            catch { }
        }

        private static bool IsStartupEnabled()
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

        // ================================================================== drag (snaps back to the locked spot)

        private void OnDragStart(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging        = true;
            dragCursorStart = Cursor.Position;
            dragFormStart   = Location;
        }

        private void OnDragMove(object? sender, MouseEventArgs e)
        {
            if (!dragging) return;
            var p = Cursor.Position;
            Location = new Point(dragFormStart.X + p.X - dragCursorStart.X, dragFormStart.Y + p.Y - dragCursorStart.Y);
        }

        private void OnDragEnd(object? sender, MouseEventArgs e)
        {
            dragging = false;
            if (lockedLocation.X >= 0) Location = lockedLocation;
        }
    }
}
