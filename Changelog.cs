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
            new(new Version(1, 2, 2),
                [
                    "Báo trước khi sắp mưa (trong 60 phút) và cảnh báo khi không khí xấu (AQI trên 150).",
                    "Icon theo ngày/đêm; giờ mặt trời mọc/lặn và chỉ số UV trong bảng dự báo.",
                    "Dự báo 24 giờ tới (mỗi 3 giờ) phía trên dự báo 6 ngày.",
                    "Phím tắt ẩn/hiện widget (mặc định Ctrl+Alt+W).",
                    "Lưu nhiều thành phố, đổi nhanh trong menu khay → 📍 Vị trí.",
                    "Cửa sổ Cài đặt: °C/°F, độ trong suốt, hình nền (online / thư mục ảnh / tắt), thông báo, phím tắt.",
                    "Kéo widget đi đâu cũng được và nhớ vị trí; mỗi màn hình một widget.",
                    "Ghi log lỗi và nút 🐞 Báo lỗi trên GitHub.",
                    "Ô ngày dưới ô trích dẫn: dương lịch + âm lịch Việt Nam (can chi), bật/tắt trong Cài đặt.",
                ],
                [
                    "Rain heads-up (within 60 min) and an unhealthy-air alert (AQI above 150).",
                    "Day/night icons; sunrise/sunset and UV index in the forecast panel.",
                    "Next 24 hours (every 3 h) above the 6-day forecast.",
                    "Show/hide hotkey (default Ctrl+Alt+W).",
                    "Save several cities and switch from the tray → 📍 Location.",
                    "Settings window: °C/°F, opacity, wallpaper (online / picture folder / off), alerts, hotkey.",
                    "Drag the widget anywhere and it stays there; one widget per monitor.",
                    "Error log and a 🐞 Report bug on GitHub button.",
                    "Date box under the quote: Gregorian + Vietnamese lunar date (Can Chi year); toggle in Settings.",
                ]),
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
