using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace frmweatherwid
{
    /// <summary>
    /// One on-screen widget. It only draws what <see cref="AppController"/> holds, so there can be one per monitor.
    /// Drag anywhere — the position is remembered per monitor.
    /// </summary>
    internal sealed class WidgetForm : Form
    {
        public const int FormWidth  = 400;
        public const int BaseHeight = 300;                                  // collapsed
        private const int SunTop    = BaseHeight + 2,  SunHeight    = 24;
        private const int HourTop   = SunTop + SunHeight + 4, HourHeight = 80;
        private const int DayTop    = HourTop + HourHeight + 4, DayHeight = 104;
        public const int ExpandedHeight = DayTop + DayHeight + 8;            // 526
        private const int ScrimAlpha = 110;                                 // dark layer over the wallpaper (see OnPaint)
        private const int HourSlots  = 8;

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
        private static readonly Font FontLunar    = new("Segoe UI", 8.25f, FontStyle.Regular);
        private static readonly StringFormat QuoteFormat = new() { Trimming = StringTrimming.EllipsisWord };

        private readonly AppController app;
        private string screenName;
        public string PositionKey { get; }
        private readonly bool clampToScreen;     // "one widget per monitor" mode keeps each on its own screen

        private readonly PictureBox pictureWeather;
        private readonly Panel pnlCity, pnlTemperature, pnlStats, pnlAqi, pnlStatus, pnlQuote, pnlDate, pnlSun, pnlHourly, pnlDaily;
        private readonly CheckBox chkStartup;
        private readonly Label lblHint, lblForecastToggle;
        private bool syncingCheckbox;

        private Font? cityFont;
        private Color cityColor  = Color.FromArgb(77, 184, 255);
        private Color cityShadow = Color.FromArgb(170, 0, 0, 0);

        private bool forecastOpen;
        private int? topBeforeExpand;            // where we were before opening pushed the widget up
        private bool allowClose;

        private Point dragCursorStart, dragFormStart;
        private bool mouseDown, dragging;

        public WidgetForm(AppController app, Screen screen, string positionKey, bool clampToScreen)
        {
            this.app = app;
            screenName = screen.DeviceName;
            PositionKey = positionKey;
            this.clampToScreen = clampToScreen;

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            FormBorderStyle       = FormBorderStyle.None;
            ClientSize            = new Size(FormWidth, BaseHeight);
            ShowInTaskbar         = false; // lives in the tray
            BackColor             = Color.FromArgb(20, 20, 20);
            StartPosition         = FormStartPosition.Manual;
            BackgroundImageLayout = ImageLayout.None; // wallpapers are pre-scaled to FormWidth x ExpandedHeight
            KeyPreview            = true;
            Text                  = "Weather Widget";

            pictureWeather = new PictureBox
            {
                SizeMode  = PictureBoxSizeMode.Zoom,
                Size      = new Size(120, 120),
                Location  = new Point(12, 12),
                BackColor = Color.Transparent,
            };

            pnlCity        = CreatePill(new Rectangle(188, 8, 208, 62),   alpha: 0,   radius: 10, PaintCity);
            pnlQuote       = CreatePill(new Rectangle(8, 138, 178, 58),   alpha: 140, radius: 10, PaintQuote);
            pnlDate        = CreatePill(new Rectangle(8, 200, 178, 34),   alpha: 140, radius: 10, PaintDate);
            pnlStats       = CreatePill(new Rectangle(190, 73, 202, 66),  alpha: 170, radius: 10, PaintStats);
            pnlTemperature = CreatePill(new Rectangle(186, 148, 208, 48), alpha: 170, radius: 12, PaintTemperature);
            pnlAqi         = CreatePill(new Rectangle(186, 206, 208, 26), alpha: 120, radius: 10, PaintAqi);
            pnlStatus      = CreatePill(new Rectangle(100, 238, 200, 22), alpha: 130, radius: 10, PaintStatus);
            pnlSun         = CreatePill(new Rectangle(8, SunTop, FormWidth - 16, SunHeight),   alpha: 160, radius: 10, PaintSun);
            pnlHourly      = CreatePill(new Rectangle(8, HourTop, FormWidth - 16, HourHeight), alpha: 160, radius: 12, PaintHourly);
            pnlDaily       = CreatePill(new Rectangle(8, DayTop, FormWidth - 16, DayHeight),   alpha: 160, radius: 12, PaintDaily);
            foreach (var p in (Panel[])[pnlSun, pnlHourly, pnlDaily]) p.Visible = false;

            chkStartup = new CheckBox
            {
                AutoSize  = true,
                Font      = FontSmall,
                ForeColor = Color.LightSeaGreen,
                BackColor = Color.FromArgb(160, 0, 0, 0),
                Padding   = new Padding(6, 3, 6, 3),
            };
            chkStartup.CheckedChanged += (_, _) => { if (!syncingCheckbox) app.StartupEnabled = chkStartup.Checked; };

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
            lblForecastToggle.Click += (_, _) => { if (!dragging) ToggleForecast(); };

            Controls.AddRange([pictureWeather, chkStartup, lblHint, lblForecastToggle]);

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
                app.HideWidgets();
            };

            app.DataChanged      += OnDataChanged;
            app.StatusChanged    += OnStatusChanged;
            app.WallpaperChanged += OnWallpaperChanged;
            app.QuoteChanged     += OnQuoteChanged;

            Location = InitialLocation(screen);
            OnWallpaperChanged();
            OnQuoteChanged();
            OnDataChanged();
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
                    using var path = Visuals.RoundedPath(panel.ClientRectangle, radius);
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

        public void ShowWidget()
        {
            EnsureOnScreen();
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        /// <summary>Only the controller really closes a widget (monitor removed / app exit); Alt+F4 just hides.</summary>
        public void CloseForGood()
        {
            allowClose = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                app.HideWidgets();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                app.DataChanged      -= OnDataChanged;
                app.StatusChanged    -= OnStatusChanged;
                app.WallpaperChanged -= OnWallpaperChanged;
                app.QuoteChanged     -= OnQuoteChanged;
                BackgroundImage = null; // owned by the controller's pool
                pictureWeather.Image = null;
                cityFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ================================================================== reacting to the controller

        private void OnDataChanged()
        {
            if (IsDisposed) return;
            ApplyLanguageToControls();
            Opacity = AppSettings.Opacity;
            syncingCheckbox    = true;
            chkStartup.Checked = app.StartupEnabled;
            syncingCheckbox    = false;

            pictureWeather.Image = app.WeatherIcon; // owned by the controller
            SetCityFont(app.Location.Name);
            foreach (Control c in Controls) c.Invalidate();
        }

        private void OnStatusChanged()
        {
            if (!IsDisposed) pnlStatus.Invalidate();
        }

        private void OnWallpaperChanged()
        {
            if (IsDisposed) return;
            BackgroundImage = app.Wallpaper; // owned by the controller's pool — never dispose here
            UpdateCityColor();
            Invalidate(true);
        }

        private void OnQuoteChanged()
        {
            if (IsDisposed) return;
            pnlQuote.Visible = app.Quote is not null;
            pnlQuote.Invalidate();
        }

        /// <summary>Texts of the real controls (painted panels read L.T at paint time) + layout of the bottom row.</summary>
        private void ApplyLanguageToControls()
        {
            chkStartup.Text        = L.T("Tự khởi động", "Run at Startup");
            lblHint.Text           = L.T("Nhấn ESC để ẩn", "Press ESC to Hide");
            lblForecastToggle.Text = (forecastOpen ? "▲ " : "▼ ") + L.T("Dự báo", "Forecast");

            chkStartup.Size        = chkStartup.PreferredSize;
            lblHint.Size           = lblHint.PreferredSize;
            lblForecastToggle.Size = lblForecastToggle.PreferredSize;

            // Bottom row (inside the collapsed area): [checkbox] ...... [▼ Forecast] [Press ESC]
            chkStartup.Location        = new Point(12, BaseHeight - chkStartup.Height - 8);
            lblHint.Location           = new Point(FormWidth - lblHint.Width - 8, BaseHeight - lblHint.Height - 8);
            lblForecastToggle.Location = new Point(lblHint.Left - lblForecastToggle.Width - 6, lblHint.Top);
        }

        // ================================================================== position / monitors

        private Screen OwnScreen => Screen.AllScreens.FirstOrDefault(s => s.DeviceName == screenName) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];

        private static Point DefaultLocation(Screen s) =>
            new(s.WorkingArea.Right - FormWidth, s.WorkingArea.Top + (s.WorkingArea.Height - BaseHeight) / 2);

        private Point InitialLocation(Screen s)
        {
            var saved = AppSettings.GetPosition(PositionKey);
            return saved is { } p && IsVisibleAt(p) ? p : DefaultLocation(s);
        }

        /// <summary>At least the top 40 px (to grab it) must be on the allowed screen(s).</summary>
        private bool IsVisibleAt(Point p)
        {
            var grab = new Rectangle(p.X, p.Y, FormWidth, 40);
            Rectangle[] areas = clampToScreen ? [OwnScreen.WorkingArea] : [.. Screen.AllScreens.Select(s => s.WorkingArea)];
            return areas.Any(a => Rectangle.Intersect(a, grab) is { Width: >= 80, Height: >= 30 });
        }

        /// <summary>After a monitor change: a widget that ended up off-screen goes back to its default spot.</summary>
        public void EnsureOnScreen()
        {
            if (!clampToScreen && !Screen.AllScreens.Any(s => s.DeviceName == screenName))
                screenName = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).DeviceName;
            if (!IsVisibleAt(Location))
            {
                topBeforeExpand = null;
                Location = DefaultLocation(OwnScreen);
            }
        }

        private void OnDragStart(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            mouseDown       = true;
            dragging        = false;
            dragCursorStart = Cursor.Position;
            dragFormStart   = Location;
        }

        private void OnDragMove(object? sender, MouseEventArgs e)
        {
            if (!mouseDown) return;
            var p = Cursor.Position;
            int dx = p.X - dragCursorStart.X, dy = p.Y - dragCursorStart.Y;
            if (!dragging && Math.Abs(dx) + Math.Abs(dy) < 5) return; // a click, not a drag
            dragging = true;

            var target = new Point(dragFormStart.X + dx, dragFormStart.Y + dy);
            if (clampToScreen)
            {
                var a = OwnScreen.WorkingArea;
                target = new Point(Math.Clamp(target.X, a.Left, a.Right - Width), Math.Clamp(target.Y, a.Top, Math.Max(a.Top, a.Bottom - Height)));
            }
            Location = target;
        }

        private void OnDragEnd(object? sender, MouseEventArgs e)
        {
            mouseDown = false;
            if (!dragging) return;
            if (!IsVisibleAt(Location)) EnsureOnScreen();
            topBeforeExpand = null; // the user chose a new place
            if (!clampToScreen) screenName = Screen.FromControl(this).DeviceName;
            AppSettings.SetPosition(PositionKey, Location);
            BeginInvoke(() => dragging = false); // let the Click that follows MouseUp see it was a drag
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

            foreach (var p in (Panel[])[pnlSun, pnlHourly, pnlDaily]) p.Visible = forecastOpen;
            ApplyLanguageToControls(); // arrow ▼/▲
            Invalidate();
        }

        // ================================================================== painting

        /// <summary>Largest font (28pt → 12pt) that fits the city name.</summary>
        private void SetCityFont(string name)
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
        }

        /// <summary>City name with a soft outline, in whichever color reads best on the current wallpaper.</summary>
        private void PaintCity(Graphics g, Rectangle r)
        {
            if (cityFont is null) return;
            var name = app.Location.Name;
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
            if (Visuals.Contrast(brand, bg) < 4.5)
            {
                Color[] candidates = [Color.White, Color.FromArgb(255, 214, 79), Color.FromArgb(10, 37, 64)];
                best = candidates.MaxBy(c => Visuals.Contrast(c, bg));
            }
            cityColor  = best;
            cityShadow = Visuals.Luminance(best) > 0.4 ? Color.FromArgb(170, 0, 0, 0) : Color.FromArgb(150, 255, 255, 255);
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
            double keep = 1 - ScrimAlpha / 255.0; // OnPaint lays a black scrim over everything
            return Color.FromArgb((int)(raw.R * keep), (int)(raw.G * keep), (int)(raw.B * keep));
        }

        private void PaintTemperature(Graphics g, Rectangle r)
        {
            var w = app.Weather;
            string text = w is null ? (app.LastFetchFailed ? L.T("Lỗi", "Err") : "-- " + Units.Symbol) : Units.Temp(w.TempC, precise: true, suffix: true);
            using var brush = new SolidBrush(w is null ? Color.Gainsboro : Visuals.TemperatureColor(w.TempC));
            g.DrawString(text, FontTemp, brush, 6f, 4f);
        }

        private void PaintStats(Graphics g, Rectangle r)
        {
            var w = app.Weather;
            string[] lines =
            [
                $"{L.T("Độ ẩm", "Humidity")}: {(w is null ? "--" : $"{w.Humidity:0}%")}",
                $"{L.T("Gió", "Wind")}: {(w is null ? "--" : Units.Wind(w.WindKph))}",
                $"{L.T("Cảm giác như", "Feels like")}: {(w is null ? "--" + Units.Symbol : Units.Temp(w.FeelsLikeC, suffix: true))}",
            ];
            using var brush = new SolidBrush(Color.LightSeaGreen);
            for (int i = 0; i < lines.Length; i++)
                g.DrawString(lines[i], FontStats, brush, 6f, 2f + i * 22);
        }

        private void PaintAqi(Graphics g, Rectangle r)
        {
            int index = 0;
            string text = "AQI: --";
            if (app.UsAqi is { } aqi)
            {
                (index, var label) = Visuals.ClassifyAqi(aqi);
                text = L.T($"AQI: {aqi}  ({label})", $"Air quality: {aqi}  ({label})");
            }
            using (var dot = new SolidBrush(Visuals.AqiColor(index)))
                g.FillEllipse(dot, 8, 8, 10, 10);
            g.DrawString(text, FontAqi, Brushes.White, 24f, 4f);
        }

        private void PaintStatus(Graphics g, Rectangle r)
        {
            using var brush = new SolidBrush(app.LastFetchFailed && !app.IsUpdating ? Color.Salmon : Color.LightSeaGreen);
            g.DrawString(app.StatusLabel, FontStatus, brush, r, Visuals.Center);
        }

        private void PaintQuote(Graphics g, Rectangle r)
        {
            if (app.Quote is not { } q) return;
            using var brushText   = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
            using var brushAuthor = new SolidBrush(Color.FromArgb(200, 180, 220, 255));
            g.DrawString(q.Text, FontQuote, brushText, new RectangleF(6, 4, r.Width - 12, r.Height - 18), QuoteFormat);
            if (!string.IsNullOrEmpty(q.Author))
                g.DrawString($"— {q.Author}", FontAuthor, brushAuthor, 6f, r.Height - 14f);
        }

        /// <summary>
        /// Gregorian date, and (if enabled) the Vietnamese lunar date + Can Chi year underneath.
        /// Mùng 1 and Rằm (15th) are highlighted.
        /// </summary>
        private void PaintDate(Graphics g, Rectangle r)
        {
            var now = DateTime.Now;
            string solar = L.IsVi
                ? $"{L.Culture.DateTimeFormat.GetDayName(now.DayOfWeek)}, {now:dd/MM/yyyy}"
                : now.ToString("ddd, MMM d, yyyy", L.Culture);

            if (!AppSettings.ShowLunar)
            {
                g.DrawString(solar, FontDay, Brushes.White, r, Visuals.Center);
                return;
            }

            var lunar = LunarCalendar.FromSolar(now);
            string leap = lunar.Leap ? L.T(" nhuận", " leap") : "";
            string text = L.T($"Âm lịch {lunar.Day}/{lunar.Month}{leap} · {lunar.YearName}",
                              $"Lunar {lunar.Day}/{lunar.Month}{leap} · {lunar.YearName}");
            bool special = lunar.Day is 1 or 15;
            using var brush = new SolidBrush(special ? Color.FromArgb(255, 214, 79) : Color.FromArgb(240, 190, 225, 255));

            g.DrawString(solar, FontDay, Brushes.White, new RectangleF(0, 1, r.Width, 16), Visuals.Center);
            g.DrawString(text, FontLunar, brush, new RectangleF(0, 16, r.Width, 17), Visuals.Center);
        }

        /// <summary>"Sunrise 06:05 · Sunset 17:16" on the left, "UV 5 · Moderate" (coloured) on the right.</summary>
        private void PaintSun(Graphics g, Rectangle r)
        {
            var w = app.Weather;
            var today = w?.Today;
            using var dim = new SolidBrush(Color.FromArgb(225, 255, 255, 255));
            var left = today is { Sunrise: { } sr, Sunset: { } ss }
                ? L.T($"Mọc {sr:HH:mm}  ·  Lặn {ss:HH:mm}", $"Sunrise {sr:HH:mm}  ·  Sunset {ss:HH:mm}")
                : L.T("Không có giờ mặt trời mọc/lặn", "No sunrise/sunset data");
            g.DrawString(left, FontDay, dim, 8, 4);

            double? uv = w?.UvIndex ?? today?.UvMax;
            if (uv is { } u)
            {
                var (label, color) = Visuals.ClassifyUv(u);
                var text = $"UV {Math.Round(u)} · {label}";
                var width = g.MeasureString(text, FontDay).Width;
                using var b = new SolidBrush(color);
                g.DrawString(text, FontDay, b, r.Width - width - 8, 4);
            }
        }

        /// <summary>Next 24 h in 3-hour steps: time, icon (day/night), temperature, chance of rain.</summary>
        private void PaintHourly(Graphics g, Rectangle r)
        {
            var hours = app.Weather?.Hourly ?? [];
            using var dim = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            if (hours.Count == 0)
            {
                g.DrawString(L.T("Chưa có dự báo theo giờ", "No hourly forecast yet"), FontDay, dim, r, Visuals.Center);
                return;
            }

            int step = hours.Count >= 2 && hours[1].Time - hours[0].Time >= TimeSpan.FromHours(3) ? 1 : 3;
            var slots = Enumerable.Range(0, HourSlots).Select(i => i * step).Where(i => i < hours.Count).Select(i => hours[i]).ToList();
            float colW = (float)r.Width / HourSlots;
            using var rain = new SolidBrush(Color.FromArgb(130, 200, 255));
            var en = CultureInfo.GetCultureInfo("en-US");

            for (int i = 0; i < slots.Count; i++)
            {
                var h = slots[i];
                float x = i * colW;
                string label = i == 0 ? L.T("Bây giờ", "Now") : L.IsVi ? $"{h.Time:HH}h" : h.Time.ToString("h tt", en);
                g.DrawString(label, FontDaySmall, dim, new RectangleF(x, 3, colW, 14), Visuals.Center);

                var icon = app.GetIcon(h.Kind, h.IsDay, 26);
                g.DrawImage(icon, x + (colW - icon.Width) / 2f, 18);

                using (var t = new SolidBrush(Visuals.TemperatureColor(h.TempC)))
                    g.DrawString(Units.Temp(h.TempC), FontDay, t, new RectangleF(x, 45, colW, 16), Visuals.Center);

                if (h.RainChance is >= 10 and var chance)
                    DrawRain(g, rain, $"{chance}%", x, colW, 61);
            }
        }

        private void PaintDaily(Graphics g, Rectangle r)
        {
            var days = app.Weather?.Daily ?? [];
            using var dim = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            if (days.Count == 0)
            {
                g.DrawString(L.T("Chưa có dữ liệu dự báo", "No forecast data yet"), FontDay, dim, r, Visuals.Center);
                return;
            }

            var today   = DateOnly.FromDateTime(app.Weather!.LocalNow);
            int count   = Math.Min(days.Count, WeatherService.ForecastDays);
            float colW  = (float)r.Width / count;
            using var sep  = new Pen(Color.FromArgb(50, 255, 255, 255));
            using var rain = new SolidBrush(Color.FromArgb(130, 200, 255));

            for (int i = 0; i < count; i++)
            {
                var d = days[i];
                float x = i * colW;
                if (i > 0) g.DrawLine(sep, x, 12, x, r.Height - 12);

                string dayName = d.Date == today ? L.T("Hôm nay", "Today") : L.Culture.DateTimeFormat.GetAbbreviatedDayName(d.Date.DayOfWeek);
                g.DrawString(dayName, FontDay, Brushes.White, new RectangleF(x, 4, colW, 18), Visuals.Center);

                var icon = app.GetIcon(d.Kind, true, 34);
                g.DrawImage(icon, x + (colW - icon.Width) / 2f, 22);

                using (var hot = new SolidBrush(Visuals.TemperatureColor(d.MaxC)))
                {
                    // "28° / 21°" — max in its temperature color, min dimmed
                    string max = Units.Temp(d.MaxC), min = $" / {Units.Temp(d.MinC)}";
                    var maxW = g.MeasureString(max, FontDayTemp).Width;
                    var minW = g.MeasureString(min, FontDaySmall).Width;
                    float start = x + (colW - maxW - minW) / 2f;
                    g.DrawString(max, FontDayTemp, hot, start, 58);
                    g.DrawString(min, FontDaySmall, dim, start + maxW - 4, 61);
                }

                if (d.RainChance is { } chance)
                    DrawRain(g, rain, $"{chance}%", x, colW, 80);
            }
        }

        private static void DrawRain(Graphics g, Brush brush, string pct, float x, float colW, float y)
        {
            float textW = g.MeasureString(pct, FontDaySmall).Width, dropH = 9;
            float start = x + (colW - textW - dropH * 0.7f - 1) / 2f;
            Visuals.DrawDrop(g, brush, start, y + 3, dropH);
            g.DrawString(pct, FontDaySmall, brush, start + dropH * 0.7f, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Dark scrim so text stays readable on any wallpaper
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var scrim = new SolidBrush(Color.FromArgb(ScrimAlpha, 0, 0, 0));
            using var path  = Visuals.RoundedPath(ClientRectangle, 14);
            e.Graphics.FillPath(scrim, path);
        }
    }
}
