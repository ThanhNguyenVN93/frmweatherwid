namespace frmweatherwid
{
    /// <summary>
    /// Version, licence and the data-source credits the providers' licences require
    /// (Open-Meteo & MET Norway: CC BY 4.0, OpenStreetMap: ODbL, ZenQuotes: attribution, …).
    /// </summary>
    internal sealed class AboutForm : Form
    {
        private static readonly (string Name, string What, string Licence, string Url)[] Sources =
        [
            ("Open-Meteo",          "Thời tiết, AQI, tìm thành phố|Weather, AQI, city search", "CC BY 4.0",                  "https://open-meteo.com"),
            ("MET Norway",          "Thời tiết dự phòng|Fallback weather",                     "CC BY 4.0 / NLOD",           "https://api.met.no"),
            ("wttr.in",             "Thời tiết dự phòng|Fallback weather",                     "WorldWeatherOnline data",    "https://wttr.in"),
            ("OpenStreetMap",       "Tên địa danh (Nominatim)|Place names (Nominatim)",        "© OpenStreetMap contributors, ODbL", "https://www.openstreetmap.org/copyright"),
            ("BigDataCloud",        "Tên địa danh|Place names",                                "Free client-side API",       "https://www.bigdatacloud.com"),
            ("ipwho.is / ipinfo.io","Vị trí theo IP|IP location",                              "Free tier",                  "https://ipwho.is"),
            ("Bing",                "Hình nền|Wallpapers",                                     "© as credited on each image","https://www.bing.com"),
            ("Unsplash (Lorem Picsum)", "Hình nền|Wallpapers",                                 "Unsplash License",           "https://unsplash.com/license"),
            ("Wikimedia Commons",   "Hình nền dự phòng|Fallback wallpapers",                   "Licence per image",          "https://commons.wikimedia.org"),
            ("ZenQuotes",           "Câu trích dẫn|Quotes",                                    "Quotes provided by ZenQuotes API", "https://zenquotes.io"),
        ];

        public AboutForm()
        {
            var version = UpdateService.CurrentVersion.ToString(3);
            Text            = L.T("Giới thiệu", "About") + " — Weather Widget";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            StartPosition   = FormStartPosition.CenterScreen;
            ShowInTaskbar   = true;
            ClientSize      = new Size(480, 520);
            BackColor       = Color.FromArgb(27, 29, 35);
            ForeColor       = Color.Gainsboro;
            Font            = new Font("Segoe UI", 9.5f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var title = new Label
            {
                Bounds    = new Rectangle(16, 12, 448, 32),
                Font      = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = Color.FromArgb(77, 184, 255),
                Text      = $"Weather Widget v{version}",
            };
            var copyright = new Label
            {
                Bounds = new Rectangle(16, 46, 448, 40),
                Text   = L.T("© 2026 Nguyen Thanh · Phần mềm tự do theo giấy phép GNU GPL v3 trở lên.\nKhông có bất kỳ bảo hành nào — bạn tự chịu trách nhiệm khi sử dụng.",
                             "© 2026 Nguyen Thanh · Free software under the GNU GPL v3 or later.\nComes with absolutely no warranty — use at your own risk."),
            };
            var links = new FlowLayoutPanel { Bounds = new Rectangle(16, 90, 448, 26), FlowDirection = FlowDirection.LeftToRight };
            links.Controls.Add(Link(L.T("Mã nguồn (GitHub)", "Source code (GitHub)"), AppController.RepoUrl));
            links.Controls.Add(Link(L.T("Giấy phép GPL-3.0", "GPL-3.0 licence"), "https://www.gnu.org/licenses/gpl-3.0.html"));
            links.Controls.Add(Link(L.T("Quyền riêng tư", "Privacy"), AppController.RepoUrl + "#quyền-riêng-tư--privacy"));

            var header = new Label
            {
                Bounds    = new Rectangle(16, 124, 448, 22),
                Font      = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(77, 184, 255),
                Text      = L.T("Nguồn dữ liệu", "Data sources"),
            };

            var list = new FlowLayoutPanel
            {
                Bounds        = new Rectangle(16, 150, 456, 318),
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                AutoScroll    = true,
            };
            foreach (var (name, what, licence, url) in Sources)
            {
                var parts = what.Split('|');
                var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 0, 6) };
                row.Controls.Add(Link(name, url, bold: true));
                row.Controls.Add(new Label
                {
                    AutoSize  = true,
                    MaximumSize = new Size(420, 0),
                    ForeColor = Color.FromArgb(160, 165, 175),
                    Font      = new Font("Segoe UI", 8.5f),
                    Text      = $"{(L.IsVi ? parts[0] : parts[^1])} · {licence}",
                    Margin    = new Padding(3, 0, 0, 0),
                });
                list.Controls.Add(row);
            }

            var ok = new Button
            {
                Bounds    = new Rectangle(364, 478, 100, 30),
                Text      = "OK",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(25, 100, 190),
                ForeColor = Color.White,
            };
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += (_, _) => Close();

            Controls.AddRange([title, copyright, links, header, list, ok]);
            Visuals.ScaleDialog(this);
            AcceptButton = ok;
            CancelButton = ok;
        }

        private static LinkLabel Link(string text, string url, bool bold = false)
        {
            var link = new LinkLabel
            {
                AutoSize         = true,
                Text             = text,
                LinkColor        = Color.FromArgb(120, 190, 255),
                ActiveLinkColor  = Color.White,
                VisitedLinkColor = Color.FromArgb(120, 190, 255),
                LinkBehavior     = LinkBehavior.HoverUnderline,
                Margin           = new Padding(0, 0, 14, 0),
            };
            if (bold) link.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            link.LinkClicked += (_, _) => AppController.OpenUrl(url);
            return link;
        }
    }
}
