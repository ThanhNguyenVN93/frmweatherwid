# Weather Widget

Widget thời tiết nhỏ cho Windows 10 (2004+) / 11 (WinForms, .NET 10), sống ở khay hệ thống.

## Tính năng
- Nhiệt độ, độ ẩm, gió, cảm giác như, AQI (US) tại vị trí hiện tại.
- **Vị trí 3 lớp** — `LocationService.cs`:
  1. **Chọn thủ công**: khay → 📍 Vị trí → *Chọn thành phố…* (tìm qua Open-Meteo Geocoding → Nominatim). Khi đã chọn, app không tự định vị nữa; bấm *Tự động định vị* để quay lại.
  2. **Định vị Windows** (Wi-Fi/GPS): cần bật *Settings → Privacy & security → Location* và *Let desktop apps access your location*.
  3. **Theo IP** (ipwho.is → ipinfo.io): cấp thành phố; khi bật VPN sẽ ra vị trí máy chủ VPN.

  Không lớp nào được thì dùng vị trí tự động lần trước, cuối cùng là Hà Nội. Định vị lại khi khởi động, khi máy thức dậy từ Sleep và khi bấm *Cập nhật thời tiết*.
  Tên thành phố lấy qua BigDataCloud → Nominatim.
- **Nguồn thời tiết có fallback** (đều miễn phí, không cần API key) — `WeatherService.cs`:
  1. [Open-Meteo](https://open-meteo.com) — nguồn chính
  2. [MET Norway](https://api.met.no) — dự phòng 1 (cảm giác như tính theo công thức BoM)
  3. [wttr.in](https://wttr.in) — dự phòng 2

  Mỗi nguồn timeout 8s; nguồn nào lỗi sẽ bị bỏ qua 15 phút. AQI lấy từ Open-Meteo Air Quality (tùy chọn, lỗi không ảnh hưởng thời tiết).
- Làm mới mỗi 30 phút; nếu tất cả nguồn lỗi thì thử lại sau 5 phút và giữ số liệu cũ.
- **Tự cập nhật** — `UpdateService.cs`: 1 phút sau khi khởi động rồi mỗi 6 giờ, app kiểm tra bản mới trên GitHub Releases. Có bản mới thì tải `WeatherWidget.exe` về cạnh exe hiện tại, kiểm tra kích thước + SHA-256, rồi thay file và tự khởi động lại (đợi lúc widget đang ẩn). Khay → *Kiểm tra cập nhật* để làm ngay. Chỉ bản phát hành (`WeatherWidget.exe` single-file) mới tự cập nhật; bản build trong `bin\` thì không.
- **Ngôn ngữ** Tiếng Việt / English (khay → 🌐), mặc định theo ngôn ngữ Windows — `Strings.cs`.
- **Dự báo 6 ngày**: nút ▼ Dự báo ở góc dưới xổ bảng dự báo (cao/thấp, khả năng mưa).
- **Có gì mới**: tự hiện sau mỗi lần cập nhật, hoặc khay → 📝 Có gì mới — `Changelog.cs`.
- **♥ Ủng hộ** (menu khay): mở https://nguyenthanhdev.com/donate/ trong trình duyệt mặc định.
- **Chỉ chạy 1 instance**: mở lần nữa sẽ hiện widget đang chạy thay vì mở bản mới.
- Hình nền xoay vòng (Bing 1024x768 + Wallhaven), cache tại `%TEMP%\FrmWeatherWidget` (tự xoá file >14 ngày).
- Trích dẫn từ ZenQuotes, đổi mỗi 15s.
- Tự bật "Run at Startup" ở lần chạy đầu; bỏ tick sẽ được giữ nguyên.

## Sử dụng
- Click trái icon khay: ẩn/hiện. Click phải: menu (cập nhật thời tiết, thoát).
- `ESC`: ẩn widget.

## Build
```bash
dotnet build -c Release
```

Một file exe tự chạy (không cần cài .NET):
```bash
dotnet publish -c Release -r win-x64 -o publish
```

Bản build sẵn: xem mục **Releases** của repo.

## Phát hành bản mới
1. Tăng `<Version>` / `<FileVersion>` trong `frmweatherwid.csproj` — tăng lần lượt số cuối (1.2.0 → 1.2.1 → 1.2.2…).
   Thêm mục của phiên bản mới (vi + en) lên đầu `Changelog.cs` — app hiện nó trong bảng *Có gì mới* sau khi tự cập nhật.
2. `dotnet publish -c Release -r win-x64 -o publish`
3. `gh release create vX.Y.Z publish/WeatherWidget.exe --title vX.Y.Z --notes "..."` — tên file phải đúng là `WeatherWidget.exe`; các máy đang chạy sẽ tự cập nhật.
