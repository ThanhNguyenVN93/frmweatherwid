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
        private const string AppName         = "FrmWeatherWidget";
        private const string RunKeyPath      = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string SettingsKeyPath = @"Software\FrmWeatherWidget";

        private const int RefreshMs         = 30 * 60_000; // normal refresh
        private const int RetryMs           = 5 * 60_000;  // after every provider failed
        private const int ResumeDelayMs     = 15_000;      // after waking from sleep
        private const int WallpaperRotateMs = 60_000;
        private const int QuoteRotateMs     = 15_000;

        // ------------------------------------------------------------------ shared resources
        private static readonly HttpClient Http = CreateHttpClient();

        private static readonly Font FontTemp    = new("Segoe UI", 24f,  FontStyle.Bold);
        private static readonly Font FontStats   = new("Segoe UI", 10f,  FontStyle.Bold);
        private static readonly Font FontAqi     = new("Segoe UI", 9.5f, FontStyle.Bold);
        private static readonly Font FontStatus  = new("Segoe UI", 9f,   FontStyle.Bold | FontStyle.Italic);
        private static readonly Font FontQuote   = new("Segoe UI", 7.5f, FontStyle.Italic);
        private static readonly Font FontAuthor  = new("Segoe UI", 7f,   FontStyle.Bold);
        private static readonly Font FontSmall   = new("Segoe UI", 9f,   FontStyle.Bold);
        private static readonly Font FontHint    = new("Segoe UI", 8f,   FontStyle.Bold);
        private static readonly StringFormat CenterFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        private static readonly StringFormat QuoteFormat  = new() { Trimming = StringTrimming.EllipsisWord };

        private readonly WeatherService weatherService   = new(Http);
        private readonly LocationService locationService = new(Http);
        private GeoLocation location;

        // ------------------------------------------------------------------ controls
        private readonly Label lblCity;
        private Font? cityFont; // sized to fit the current city name
        private readonly PictureBox pictureWeather;
        private readonly Panel pnlTemperature, pnlStats, pnlAqi, pnlStatus, pnlQuote;
        private readonly CheckBox chkStartup;
        private readonly NotifyIcon trayIcon;

        private readonly System.Windows.Forms.Timer refreshTimer   = new() { Interval = RefreshMs };
        private readonly System.Windows.Forms.Timer loadingTimer   = new() { Interval = 300 };
        private readonly System.Windows.Forms.Timer wallpaperTimer = new() { Interval = WallpaperRotateMs };
        private readonly System.Windows.Forms.Timer quoteTimer     = new() { Interval = QuoteRotateMs };

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

        private readonly List<Bitmap> wallpapers = [];
        private int wallpaperIndex;
        private readonly List<(string Text, string Author)> quotes = [];
        private int quoteIndex;

        private Point dragCursorStart, dragFormStart, lockedLocation = new(-1, -1);
        private bool dragging;

        // ================================================================== setup

        public FrmWeatherWidget()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            FormBorderStyle       = FormBorderStyle.None;
            ClientSize            = new Size(400, 300);
            MaximumSize           = MinimumSize = ClientSize;
            Opacity               = 0.95;
            ShowInTaskbar         = false; // lives in the tray
            BackColor             = Color.FromArgb(20, 20, 20);
            StartPosition         = FormStartPosition.Manual;
            BackgroundImageLayout = ImageLayout.None; // wallpapers are pre-scaled to ClientSize
            KeyPreview            = true;

            // Start with the last known place (instant), re-detected in OnStartedAsync.
            location = locationService.ManualLocation ?? locationService.CachedLocation ?? LocationService.DefaultLocation;

            lblCity = new Label
            {
                AutoSize     = false,
                Bounds       = new Rectangle(190, 10, 206, 58),
                TextAlign    = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                ForeColor    = Color.DodgerBlue,
                BackColor    = Color.Transparent,
            };
            SetCityLabel(location.Name);

            pictureWeather = new PictureBox
            {
                SizeMode  = PictureBoxSizeMode.Zoom,
                Size      = new Size(120, 120),
                Location  = new Point(12, 12),
                BackColor = Color.Transparent,
            };
            pictureWeather.Image = CreatePlaceholderIcon(pictureWeather.Size);

            pnlQuote       = CreatePill(new Rectangle(8, 138, 178, 72),  alpha: 140, radius: 10, PaintQuote);
            pnlStats       = CreatePill(new Rectangle(190, 73, 202, 66), alpha: 170, radius: 10, PaintStats);
            pnlTemperature = CreatePill(new Rectangle(186, 148, 208, 48), alpha: 170, radius: 12, PaintTemperature);
            pnlAqi         = CreatePill(new Rectangle(186, 206, 208, 26), alpha: 120, radius: 10, PaintAqi);
            pnlStatus      = CreatePill(new Rectangle(100, 238, 200, 22), alpha: 130, radius: 10, PaintStatus);
            pnlQuote.Visible = false; // until quotes are loaded

            chkStartup = new CheckBox
            {
                AutoSize  = true,
                Text      = "Run at Startup",
                Font      = FontSmall,
                ForeColor = Color.LightSeaGreen,
                BackColor = Color.FromArgb(160, 0, 0, 0),
                Padding   = new Padding(6, 3, 6, 3),
            };
            chkStartup.Size     = chkStartup.PreferredSize;
            chkStartup.Location = new Point(12, ClientSize.Height - chkStartup.Height - 8);
            chkStartup.CheckedChanged += ChkStartup_CheckedChanged;

            var lblHint = new Label
            {
                AutoSize  = true,
                Font      = FontHint,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(100, 0, 0, 0),
                Text      = "Press ESC to Hide",
                Padding   = new Padding(6, 4, 6, 4),
            };
            lblHint.Size     = lblHint.PreferredSize;
            lblHint.Location = new Point(ClientSize.Width - lblHint.Width - 8, ClientSize.Height - lblHint.Height - 8);

            Controls.AddRange([lblCity, pictureWeather, chkStartup, lblHint]);

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

            trayIcon = CreateTrayIcon();
            UpdateTray();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

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
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppName}/1.0");
            return client;
        }

        /// <summary>Transparent owner-drawn panel with a rounded dark background.</summary>
        private Panel CreatePill(Rectangle bounds, int alpha, int radius, Action<Graphics, Rectangle> draw)
        {
            var panel = new BufferedPanel { BackColor = Color.Transparent, Bounds = bounds };
            panel.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using (var bg = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                using (var path = RoundedPath(panel.ClientRectangle, radius))
                    g.FillPath(bg, path);

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
                SystemEvents.PowerModeChanged -= OnPowerModeChanged; // static event — must unhook
                refreshTimer.Dispose();
                loadingTimer.Dispose();
                wallpaperTimer.Dispose();
                quoteTimer.Dispose();

                trayIcon.Visible = false;
                trayIcon.Icon?.Dispose();
                trayIcon.ContextMenuStrip?.Dispose();
                trayIcon.Dispose();

                BackgroundImage = null;
                foreach (var bmp in wallpapers) bmp.Dispose();
                wallpapers.Clear();
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

        /// <summary>Largest font (28pt → 12pt) that fits the city name in the label.</summary>
        private void SetCityLabel(string name)
        {
            Font font;
            for (float size = 28f; ; size -= 2f)
            {
                font = new Font("Segoe UI", size, FontStyle.Bold);
                if (size <= 12f || TextRenderer.MeasureText(name, font).Width <= lblCity.Width - 4) break;
                font.Dispose();
            }
            lblCity.Font = font;
            lblCity.Text = name;
            cityFont?.Dispose();
            cityFont = font;
        }

        private static string SourceLabel(LocationSource s) => s switch
        {
            LocationSource.Windows => "định vị Windows",
            LocationSource.IP      => "theo IP",
            LocationSource.Manual  => "chọn thủ công",
            LocationSource.Cached  => "lần trước",
            _                      => "mặc định",
        };

        private async Task PickCityAsync()
        {
            using var dlg = new CityPickerForm(locationService);
            if (dlg.ShowDialog() != DialogResult.OK || dlg.Selected is not { } c) return;
            locationService.ManualLocation = new GeoLocation(c.Latitude, c.Longitude, c.Name, LocationSource.Manual);
            await UpdateWeatherAsync(relocate: true);
        }

        private static (int Index, string Label) ClassifyAqi(int aqi) => aqi switch
        {
            <= 50  => (1, "Good"),
            <= 100 => (2, "Moderate"),
            <= 150 => (3, "Unhealthy (Sensitive)"),
            <= 200 => (4, "Unhealthy"),
            <= 300 => (5, "Very Unhealthy"),
            _      => (6, "Hazardous"),
        };

        private string TempLabel => weather is null ? (lastFetchFailed ? "Err" : "-- °C") : $"{weather.TempC:0.#}°C";

        private string StatusLabel
        {
            get
            {
                if (isUpdating)
                    return (isLocating ? "Locating" : "Loading") + new string('.', loadingDots % 4);
                if (lastFetchFailed)
                    return lastUpdated is { } t ? $"Offline · last {t:HH:mm}" : "Offline · retrying...";
                return lastUpdated is { } u ? $"Updated {u:HH:mm} · {weather!.Source}" : "Updated --:--";
            }
        }

        // ================================================================== painting

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
                $"Humidity: {(w is null ? "--" : $"{w.Humidity:0}%")}",
                $"Wind: {(w is null ? "--" : $"{w.WindKph:0} km/h")}",
                $"Feels like: {(w is null ? "--" : $"{w.FeelsLikeC:0}")}°C",
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
                text = $"Air quality: {aqi}  ({label})";
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
            using var scrim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
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
                WeatherKind.Fog          => "🌫️",
                WeatherKind.Drizzle      => "🌦️",
                WeatherKind.Rain         => "🌧️",
                WeatherKind.Snow         => "🌨️",
                WeatherKind.Thunder      => "⛈️",
                _                        => "🌡️",
            };

            var bmp = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI Emoji", size.Height * 0.52f);
            g.DrawString(emoji, font, Brushes.White, new RectangleF(0, 0, size.Width, size.Height), CenterFormat);
            return bmp;
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
            var menu      = new ContextMenuStrip();
            var mnuToggle = new ToolStripMenuItem("Hiện Widget", null, (_, _) => ToggleWidget());
            var mnuRefresh = new ToolStripMenuItem("🔄 Cập nhật thời tiết");
            mnuRefresh.Click += async (_, _) =>
            {
                mnuRefresh.Enabled = false;
                try { await UpdateWeatherAsync(relocate: true); }
                finally { mnuRefresh.Enabled = true; }
            };

            // 📍 Location submenu
            var mnuCurrent = new ToolStripMenuItem { Enabled = false };
            var mnuAuto    = new ToolStripMenuItem("Tự động định vị", null, async (_, _) =>
            {
                locationService.ManualLocation = null;
                await UpdateWeatherAsync(relocate: true);
            });
            var mnuPick     = new ToolStripMenuItem("Chọn thành phố…", null, async (_, _) => await PickCityAsync());
            var mnuLocation = new ToolStripMenuItem("📍 Vị trí");
            mnuLocation.DropDownItems.AddRange([mnuCurrent, new ToolStripSeparator(), mnuAuto, mnuPick]);

            menu.Items.AddRange([mnuToggle, mnuRefresh, mnuLocation, new ToolStripSeparator(),
                                 new ToolStripMenuItem("Thoát", null, (_, _) => Application.Exit())]);
            menu.Opening += (_, _) =>
            {
                mnuToggle.Text  = Visible ? "Ẩn Widget" : "Hiện Widget";
                mnuCurrent.Text = $"Hiện tại: {location.Name} ({SourceLabel(location.Source)})";
                mnuAuto.Checked = location.Source != LocationSource.Manual;
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
                : $"{location.Name}: {w.TempC:0.#}°C\nHumidity: {w.Humidity:0}%\nWind: {w.WindKph:0} km/h\nFeels like: {w.FeelsLikeC:0}°C";
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
                var target  = ClientSize;
                var loaded  = await Task.WhenAll(sources.SelectMany(s => s)
                    .Select(e => LoadScaledImageAsync(e.Url, Path.Combine(cacheDir, e.FileName), target)));

                var bitmaps = loaded.OfType<Bitmap>().ToList();
                if (IsDisposed)
                {
                    bitmaps.ForEach(b => b.Dispose());
                    return;
                }
                if (bitmaps.Count == 0) return;

                Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bitmaps));

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
                // ZenQuotes — free, no key, 50 quotes per call
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
