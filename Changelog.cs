namespace frmweatherwid
{
    /// <summary>
    /// Release notes shown in the "What's new" dialog — bilingual so they follow the UI language.
    /// Add the new version at the TOP before publishing a release.
    /// </summary>
    internal static class Changelog
    {
        internal sealed record Entry(Version Version, string[] Vi, string[] En)
        {
            public string[] Items => L.IsVi ? Vi : En;
        }

        public static readonly Entry[] Entries =
        [
            new(new Version(1, 2, 1),
                [
                    "Chọn ngôn ngữ Tiếng Việt / English (khay → 🌐 Ngôn ngữ); tên địa danh cũng theo ngôn ngữ.",
                    "Dự báo 6 ngày: bấm ▼ Dự báo để xổ xuống (nhiệt độ cao/thấp, khả năng mưa).",
                    "Tên thành phố tự đổi màu cho dễ đọc trên mọi hình nền.",
                    "Bảng \"Có gì mới\" sau mỗi lần cập nhật.",
                    "Thêm mục ♥ Ủng hộ tác giả trong menu khay.",
                ],
                [
                    "Language option: Tiếng Việt / English (tray → 🌐 Language); place names follow it too.",
                    "6-day forecast: click ▼ Forecast to expand (high/low, chance of rain).",
                    "City name changes color to stay readable on any wallpaper.",
                    "\"What's new\" window after each update.",
                    "Added ♥ Donate to the tray menu.",
                ]),
            new(new Version(1, 2, 0),
                [
                    "Tự cập nhật từ GitHub Releases (kiểm tra kích thước + SHA-256 trước khi thay file).",
                    "Menu khay: hiện phiên bản và mục Kiểm tra cập nhật.",
                ],
                [
                    "Auto-update from GitHub Releases (size + SHA-256 verified before swapping the exe).",
                    "Tray menu shows the version and a Check for updates item.",
                ]),
            new(new Version(1, 1, 1),
                [
                    "Đổi tên thành WeatherWidget.exe, thêm icon và thông tin phiên bản.",
                ],
                [
                    "Renamed to WeatherWidget.exe, added an icon and version info.",
                ]),
            new(new Version(1, 1, 0),
                [
                    "Định vị: Windows Location → IP → chọn thành phố thủ công.",
                ],
                [
                    "Location: Windows Location → IP → pick a city manually.",
                ]),
            new(new Version(1, 0, 0),
                [
                    "Bản đầu tiên: thời tiết + AQI, nguồn dự phòng MET Norway / wttr.in, chỉ chạy 1 bản.",
                ],
                [
                    "First release: weather + AQI, MET Norway / wttr.in fallbacks, single instance.",
                ]),
        ];

        /// <summary>Entries newer than <paramref name="since"/> (exclusive) up to the running version; all when null.</summary>
        public static IEnumerable<Entry> Since(Version? since) =>
            Entries.Where(e => e.Version <= UpdateService.CurrentVersion && (since is null || e.Version > since));
    }
}
