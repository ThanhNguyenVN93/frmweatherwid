using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace frmweatherwid
{
    /// <summary>Drawing helpers and colour/label rules shared by the widgets, tray icon and dialogs.</summary>
    internal static class Visuals
    {
        public static readonly StringFormat Center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        public static GraphicsPath RoundedPath(Rectangle rect, int d)
        {
            var path = new GraphicsPath();
            path.AddArc(rect.Left,      rect.Top,        d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top,        d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d,   0, 90);
            path.AddArc(rect.Left,      rect.Bottom - d, d, d,  90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color TemperatureColor(double tempC) => tempC switch
        {
            <= 0  => Color.FromArgb(100, 180, 255), // freezing — ice blue
            <= 10 => Color.FromArgb( 80, 220, 255), // cold     — cyan
            <= 18 => Color.FromArgb(100, 255, 200), // cool     — mint
            <= 25 => Color.FromArgb(255, 230,  60), // mild     — yellow
            <= 32 => Color.FromArgb(255, 150,  30), // warm     — orange
            _     => Color.FromArgb(255,  70,  40), // hot      — red-orange
        };

        /// <summary>US AQI bands; Vietnamese names follow the VN AQI scale (Tốt → Nguy hại).</summary>
        public static (int Index, string Label) ClassifyAqi(int aqi) => aqi switch
        {
            <= 50  => (1, L.T("Tốt", "Good")),
            <= 100 => (2, L.T("Trung bình", "Moderate")),
            <= 150 => (3, L.T("Kém", "Unhealthy (Sensitive)")),
            <= 200 => (4, L.T("Xấu", "Unhealthy")),
            <= 300 => (5, L.T("Rất xấu", "Very Unhealthy")),
            _      => (6, L.T("Nguy hại", "Hazardous")),
        };

        public static Color AqiColor(int index) => index switch
        {
            1 => Color.FromArgb( 80, 220,  80),
            2 => Color.FromArgb(255, 220,  50),
            3 => Color.FromArgb(255, 160,  30),
            4 => Color.FromArgb(255,  60,  60),
            5 => Color.FromArgb(180,  50, 180),
            6 => Color.FromArgb(180,  30,  30),
            _ => Color.Gray,
        };

        /// <summary>WHO UV index levels.</summary>
        public static (string Label, Color Color) ClassifyUv(double uv) => uv switch
        {
            < 3  => (L.T("Thấp", "Low"),            Color.FromArgb(120, 220, 120)),
            < 6  => (L.T("Trung bình", "Moderate"), Color.FromArgb(255, 220,  60)),
            < 8  => (L.T("Cao", "High"),            Color.FromArgb(255, 150,  40)),
            < 11 => (L.T("Rất cao", "Very high"),   Color.FromArgb(255,  80,  80)),
            _    => (L.T("Cực cao", "Extreme"),     Color.FromArgb(200, 100, 255)),
        };

        /// <summary>Weather glyph; night swaps the sun for a moon (no ☀ at 21:00 any more).</summary>
        public static Bitmap CreateWeatherIcon(WeatherKind kind, Size size, bool isDay = true)
        {
            var emoji = kind switch
            {
                WeatherKind.Clear        => isDay ? "☀️" : "🌙",
                WeatherKind.PartlyCloudy => isDay ? "⛅" : "☁️",
                WeatherKind.Cloudy       => "☁️",
                WeatherKind.Fog          => "☁️", // 🌫️ renders as a framed blob in GDI+ → cloud + mist lines below
                WeatherKind.Drizzle      => isDay ? "🌦️" : "🌧️",
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
            g.DrawString(emoji, font, Brushes.White, area, Center);

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

        public static Bitmap CreatePlaceholderIcon(Size size)
        {
            var bmp = new Bitmap(size.Width, size.Height);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Color.FromArgb(240, 240, 200));
            int d = Math.Min(size.Width, size.Height) - 12;
            g.FillEllipse(b, 6, 6, d, d);
            return bmp;
        }

        /// <summary>Small rain-drop glyph (the 💧 emoji has no glyph in Segoe UI and shows as a box).</summary>
        public static void DrawDrop(Graphics g, Brush brush, float x, float y, float h)
        {
            float w = h * 0.7f;
            using var path = new GraphicsPath();
            path.AddLine(x + w / 2, y, x + w, y + h * 0.58f);
            path.AddArc(x, y + h - w, w, w, 0, 180);
            path.CloseFigure();
            g.FillPath(brush, path);
        }

        public static double Luminance(Color c)
        {
            static double Ch(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        /// <summary>WCAG contrast ratio (1 … 21).</summary>
        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
    }
}
