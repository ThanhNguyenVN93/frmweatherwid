namespace frmweatherwid
{
    /// <summary>
    /// Settings window — every change is saved and applied immediately (no OK/Cancel).
    /// Rebuilt when the language changes; values re-synced when something changes elsewhere (tray, widget).
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private static readonly Color Bg     = Color.FromArgb(27, 29, 35);
        private static readonly Color Panel2 = Color.FromArgb(38, 41, 49);
        private static readonly Color Accent = Color.FromArgb(77, 184, 255);
        private const int W = 460, Pad = 16;

        private readonly AppController app;
        private bool syncing;

        // controls that SyncValues() refreshes
        private ComboBox cboLanguage = null!;
        private RadioButton rbC = null!, rbF = null!, rbOnline = null!, rbFolder = null!, rbNone = null!;
        private TrackBar trkOpacity = null!;
        private Label lblOpacity = null!, lblHotkeyState = null!;
        private CheckBox chkStartup = null!, chkMonitors = null!, chkLunar = null!, chkRain = null!, chkAqi = null!;
        private TextBox txtFolder = null!, txtHotkey = null!;
        private ListBox lstCities = null!;

        public SettingsForm(AppController app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            StartPosition   = FormStartPosition.CenterScreen;
            ShowInTaskbar   = true;
            BackColor       = Bg;
            ForeColor       = Color.Gainsboro;
            Font            = new Font("Segoe UI", 9.5f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Build();
            L.Changed       += OnLanguageChanged;
            app.DataChanged += OnDataChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                L.Changed       -= OnLanguageChanged;
                app.DataChanged -= OnDataChanged;
            }
            base.Dispose(disposing);
        }

        private void OnLanguageChanged() => Build();

        private void OnDataChanged()
        {
            if (!IsDisposed) SyncValues();
        }

        // ================================================================== layout

        private void Build()
        {
            SuspendLayout();
            Controls.Clear();
            Text = L.T("Cài đặt — Weather Widget", "Settings — Weather Widget");
            int y = Pad;

            // ---------------- General
            y = Header(L.T("Chung", "General"), y);
            AddLabel(L.T("Ngôn ngữ", "Language"), y + 3);
            cboLanguage = new ComboBox { Bounds = new Rectangle(170, y, 150, 26), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Color.White };
            cboLanguage.Items.AddRange(["Tiếng Việt", "English"]);
            cboLanguage.SelectedIndexChanged += (_, _) => { if (!syncing) L.Set(cboLanguage.SelectedIndex == 0 ? AppLanguage.Vietnamese : AppLanguage.English); };
            Controls.Add(cboLanguage);
            y += 32;

            AddLabel(L.T("Đơn vị nhiệt độ", "Temperature unit"), y + 2);
            rbC = Radio("°C", 170, y, (_, _) => { if (!syncing && rbC.Checked) AppSettings.Unit = TempUnit.Celsius; });
            rbF = Radio("°F", 230, y, (_, _) => { if (!syncing && rbF.Checked) AppSettings.Unit = TempUnit.Fahrenheit; });
            var unitGroup = new Panel { Bounds = new Rectangle(170, y, 140, 24) };
            rbC.Location = new Point(0, 0); rbF.Location = new Point(60, 0);
            unitGroup.Controls.AddRange([rbC, rbF]);
            Controls.Add(unitGroup);
            y += 30;

            AddLabel(L.T("Độ trong suốt", "Opacity"), y + 6);
            trkOpacity = new TrackBar { Bounds = new Rectangle(162, y, 220, 30), Minimum = 50, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10, BackColor = Bg };
            lblOpacity = new Label { Bounds = new Rectangle(386, y + 6, 50, 20) };
            trkOpacity.ValueChanged += (_, _) =>
            {
                lblOpacity.Text = $"{trkOpacity.Value}%";
                if (!syncing) AppSettings.Opacity = trkOpacity.Value / 100.0;
            };
            Controls.AddRange([trkOpacity, lblOpacity]);
            y += 38;

            chkStartup  = Check(L.T("Tự khởi động cùng Windows", "Run at Windows startup"), y, (_, _) => { if (!syncing) app.StartupEnabled = chkStartup.Checked; });
            y += 26;
            chkMonitors = Check(L.T("Hiện widget trên mọi màn hình", "Show a widget on every monitor"), y, (_, _) => { if (!syncing) AppSettings.AllMonitors = chkMonitors.Checked; });
            y += 26;
            chkLunar = Check(L.T("Hiện ngày âm lịch (Việt Nam)", "Show the Vietnamese lunar date"), y, (_, _) => { if (!syncing) AppSettings.ShowLunar = chkLunar.Checked; });
            y += 34;

            // ---------------- Wallpaper
            y = Header(L.T("Hình nền", "Wallpaper"), y);
            var wpGroup = new Panel { Bounds = new Rectangle(Pad, y, W - 2 * Pad, 84) };
            rbOnline = Radio(L.T("Ảnh online (Bing + Unsplash)", "Online photos (Bing + Unsplash)"), 0, 0, (_, _) => { if (!syncing && rbOnline.Checked) AppSettings.Wallpaper = WallpaperMode.Online; });
            rbFolder = Radio(L.T("Ảnh trong thư mục:", "Pictures in a folder:"), 0, 28, (_, _) => { if (!syncing && rbFolder.Checked) ChooseFolderIfNeeded(); });
            rbNone   = Radio(L.T("Không dùng (nền tối)", "None (dark background)"), 0, 56, (_, _) => { if (!syncing && rbNone.Checked) AppSettings.Wallpaper = WallpaperMode.None; });
            txtFolder = new TextBox { Bounds = new Rectangle(170, 27, 180, 24), ReadOnly = true, BackColor = Panel2, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            var btnFolder = Button(L.T("Chọn…", "Browse…"), new Rectangle(356, 25, 72, 27), (_, _) => ChooseFolder());
            wpGroup.Controls.AddRange([rbOnline, rbFolder, rbNone, txtFolder, btnFolder]);
            Controls.Add(wpGroup);
            y += 92;

            // ---------------- Alerts
            y = Header(L.T("Thông báo", "Alerts"), y);
            chkRain = Check(L.T("Báo trước khi sắp mưa (trong 60 phút)", "Warn when rain is coming (within 60 min)"), y, (_, _) => { if (!syncing) AppSettings.RainAlert = chkRain.Checked; });
            y += 26;
            chkAqi  = Check(L.T("Cảnh báo không khí xấu (AQI trên 150)", "Warn about unhealthy air (AQI above 150)"), y, (_, _) => { if (!syncing) AppSettings.AqiAlert = chkAqi.Checked; });
            y += 34;

            // ---------------- Hotkey
            y = Header(L.T("Phím tắt ẩn/hiện", "Show/hide hotkey"), y);
            txtHotkey = new TextBox { Bounds = new Rectangle(Pad, y, 160, 26), ReadOnly = true, BackColor = Panel2, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center, Cursor = Cursors.Hand };
            txtHotkey.KeyDown += HotkeyBox_KeyDown;
            lblHotkeyState = new Label { Bounds = new Rectangle(Pad + 170, y + 3, W - 2 * Pad - 170, 22), AutoEllipsis = true };
            Controls.AddRange([txtHotkey, lblHotkeyState]);
            y += 30;
            AddLabel(L.T("Bấm vào ô rồi nhấn tổ hợp phím (có Ctrl hoặc Alt). Backspace để tắt.",
                         "Click the box and press a combination (with Ctrl or Alt). Backspace turns it off."), y, small: true);
            y += 42;

            // ---------------- Saved cities
            y = Header(L.T("Thành phố đã lưu", "Saved cities"), y);
            lstCities = new ListBox { Bounds = new Rectangle(Pad, y, 300, 92), BackColor = Panel2, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
            var btnAdd    = Button(L.T("Thêm…", "Add…"), new Rectangle(Pad + 310, y, 118, 28), async (_, _) => await app.PickCityAsync(this));
            var btnUse    = Button(L.T("Dùng", "Use"), new Rectangle(Pad + 310, y + 32, 118, 28), async (_, _) =>
            {
                if (lstCities.SelectedItem is SavedCity c) await app.UseCityAsync(c);
            });
            var btnRemove = Button(L.T("Xoá", "Remove"), new Rectangle(Pad + 310, y + 64, 118, 28), (_, _) =>
            {
                if (lstCities.SelectedItem is SavedCity c) AppSettings.ForgetCity(c);
            });
            lstCities.Format += (_, e) => { if (e.ListItem is SavedCity c) e.Value = c.Name; };
            Controls.AddRange([lstCities, btnAdd, btnUse, btnRemove]);
            y += 104;

            // ---------------- Footer
            var sep = new Label { Bounds = new Rectangle(Pad, y, W - 2 * Pad, 1), BackColor = Color.FromArgb(60, 64, 74) };
            Controls.Add(sep);
            y += 12;
            Controls.Add(Button(L.T("📄 Thư mục log", "📄 Log folder"), new Rectangle(Pad, y, 130, 30), (_, _) =>
            {
                Directory.CreateDirectory(AppLog.Dir);
                AppController.OpenUrl(AppLog.Dir);
            }));
            Controls.Add(Button(L.T("🐞 Báo lỗi", "🐞 Report bug"), new Rectangle(Pad + 138, y, 120, 30), (_, _) => BugReport.Open()));
            var uninstall = Button(L.T("🗑 Gỡ cài đặt…", "🗑 Uninstall…"), new Rectangle(Pad + 266, y, W - 2 * Pad - 266, 30), (_, _) => ConfirmUninstall());
            uninstall.ForeColor = Color.Salmon;
            Controls.Add(uninstall);
            y += 38;
            var close = Button(L.T("Đóng", "Close"), new Rectangle(W - Pad - 100, y, 100, 30), (_, _) => Close());
            close.BackColor = Color.FromArgb(25, 100, 190);
            close.ForeColor = Color.White;
            Controls.Add(close);
            CancelButton = close;
            y += 30 + Pad;

            ClientSize = new Size(W, y);
            Visuals.ScaleDialog(this); // sharp on 125%/150% screens (bounds follow the DPI-sized fonts)
            SyncValues();
            ResumeLayout();
        }

        private int Header(string text, int y)
        {
            Controls.Add(new Label { Bounds = new Rectangle(Pad, y, W - 2 * Pad, 22), Text = text, ForeColor = Accent, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) });
            return y + 26;
        }

        private void AddLabel(string text, int y, bool small = false) =>
            Controls.Add(new Label
            {
                Bounds    = new Rectangle(Pad, y, small ? W - 2 * Pad : 150, small ? 38 : 22),
                Text      = text,
                ForeColor = small ? Color.FromArgb(150, 155, 165) : ForeColor,
                Font      = small ? new Font("Segoe UI", 8.5f) : Font,
            });

        private CheckBox Check(string text, int y, EventHandler onChange)
        {
            var c = new CheckBox { Bounds = new Rectangle(Pad, y, W - 2 * Pad, 24), Text = text, FlatStyle = FlatStyle.Flat };
            c.CheckedChanged += onChange;
            Controls.Add(c);
            return c;
        }

        private static RadioButton Radio(string text, int x, int y, EventHandler onChange)
        {
            var r = new RadioButton { Location = new Point(x, y), AutoSize = true, Text = text, FlatStyle = FlatStyle.Flat };
            r.CheckedChanged += onChange;
            return r;
        }

        private static Button Button(string text, Rectangle bounds, EventHandler onClick)
        {
            var b = new Button { Bounds = bounds, Text = text, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Color.White };
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 88);
            b.Click += onClick;
            return b;
        }

        // ================================================================== values

        private void SyncValues()
        {
            syncing = true;
            try
            {
                cboLanguage.SelectedIndex = L.IsVi ? 0 : 1;
                rbC.Checked = !Units.F;
                rbF.Checked = Units.F;
                trkOpacity.Value = (int)Math.Round(AppSettings.Opacity * 100);
                lblOpacity.Text  = $"{trkOpacity.Value}%";
                chkStartup.Checked  = app.StartupEnabled;
                chkMonitors.Checked = AppSettings.AllMonitors;
                chkLunar.Checked    = AppSettings.ShowLunar;

                rbOnline.Checked = AppSettings.Wallpaper == WallpaperMode.Online;
                rbFolder.Checked = AppSettings.Wallpaper == WallpaperMode.Folder;
                rbNone.Checked   = AppSettings.Wallpaper == WallpaperMode.None;
                txtFolder.Text   = AppSettings.WallpaperFolder;

                chkRain.Checked = AppSettings.RainAlert;
                chkAqi.Checked  = AppSettings.AqiAlert;

                txtHotkey.Text = AppController.HotkeyText(AppSettings.Hotkey);
                (lblHotkeyState.Text, lblHotkeyState.ForeColor) =
                    AppSettings.Hotkey == Keys.None ? (L.T("Đang tắt", "Off"), Color.Gray)
                    : app.HotkeyActive ? (L.T("✓ Đang hoạt động", "✓ Active"), Color.FromArgb(120, 220, 120))
                    : (L.T("✗ Bị chương trình khác dùng — chọn tổ hợp khác", "✗ Taken by another program — pick another"), Color.Salmon);

                var selected = lstCities.SelectedItem;
                lstCities.Items.Clear();
                foreach (var c in AppSettings.SavedCities) lstCities.Items.Add(c);
                if (selected is not null && lstCities.Items.Contains(selected)) lstCities.SelectedItem = selected;
            }
            finally
            {
                syncing = false;
            }
        }

        private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            var key = e.KeyCode;
            if (key is Keys.Back or Keys.Delete && e.Modifiers == Keys.None)
            {
                AppSettings.Hotkey = Keys.None;
                return;
            }
            if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return; // wait for the real key
            if (!e.Control && !e.Alt) return; // plain letters would steal normal typing
            AppSettings.Hotkey = e.KeyData;
        }

        private void ConfirmUninstall()
        {
            var answer = MessageBox.Show(this,
                L.T("Gỡ Weather Widget khỏi máy?\n\n• Tắt app và bỏ khỏi khởi động cùng Windows\n• Xoá cài đặt, thành phố đã lưu, log và ảnh nền đã tải\n• Xoá file WeatherWidget.exe\n\nKhông hoàn tác được.",
                    "Remove Weather Widget from this PC?\n\n• Close it and remove it from Windows startup\n• Delete settings, saved cities, logs and cached wallpapers\n• Delete WeatherWidget.exe\n\nThis can't be undone."),
                L.T("Gỡ cài đặt", "Uninstall"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes) Uninstaller.UninstallNow();
        }

        private void ChooseFolderIfNeeded()
        {
            if (Directory.Exists(AppSettings.WallpaperFolder)) AppSettings.Wallpaper = WallpaperMode.Folder;
            else ChooseFolder();
        }

        private void ChooseFolder()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description  = L.T("Chọn thư mục chứa ảnh (jpg, png, bmp)", "Choose a folder with pictures (jpg, png, bmp)"),
                SelectedPath = Directory.Exists(AppSettings.WallpaperFolder)
                    ? AppSettings.WallpaperFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                AppSettings.WallpaperFolder = dlg.SelectedPath;
                AppSettings.Wallpaper = WallpaperMode.Folder;
            }
            else
            {
                SyncValues(); // cancelled → put the radio back where it was
            }
        }
    }
}
