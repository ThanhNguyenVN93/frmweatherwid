# Weather Widget

Widget thời tiết nhỏ cho Windows 10/11 x64 (WinForms, .NET 10), sống ở khay hệ thống. Một file `WeatherWidget.exe`, không cần cài .NET.

**Tải về:** mục [Releases](https://github.com/ThanhNguyenVN93/frmweatherwid/releases/latest). Chạy `WeatherWidget.exe` là xong — để file ở thư mục bạn có quyền ghi (Desktop, Documents…) để app tự cập nhật được.

## Tính năng
- Nhiệt độ, độ ẩm, gió, cảm giác như, AQI (US) tại vị trí hiện tại; icon theo ngày/đêm.
- **Dự báo** (nút ▼ Dự báo): giờ mặt trời mọc/lặn + UV, 24 giờ tới (mỗi 3 giờ), 6 ngày tới.
- **Thông báo**: sắp mưa trong 60 phút, không khí xấu (AQI > 150). Mỗi sự kiện báo một lần.
- **Ngày dương + âm lịch Việt Nam** (can chi, tính theo UTC+7), bật/tắt trong Cài đặt.
- **Vị trí 3 lớp**: chọn thành phố (lưu tối đa 8, mỗi thành phố giữ múi giờ riêng) → định vị Windows → theo IP; dự phòng là vị trí lần trước, rồi Hà Nội.
- **Nguồn thời tiết có fallback** (miễn phí, không cần API key): Open-Meteo → MET Norway → wttr.in. Mất mạng thì vẫn hiện thời tiết lần trước.
- **Nhiều màn hình**: mỗi màn hình một widget; kéo đi đâu cũng được, nhớ vị trí; sắc nét ở 125%/150% và khi các màn hình khác DPI.
- **Phím tắt** ẩn/hiện (mặc định `Ctrl+Alt+W`), `ESC` để ẩn.
- **Cài đặt** (khay → ⚙ hoặc `WeatherWidget.exe --settings`): ngôn ngữ Việt/Anh, °C/°F, độ trong suốt, hình nền (Bing + Unsplash / thư mục ảnh / tắt), thông báo, phím tắt, thành phố đã lưu.
- **Tự cập nhật** từ GitHub Releases (kiểm tra SHA-256); nếu bản mới không khởi động được thì **tự quay lại bản cũ**. Sau khi cập nhật hiện bảng *Có gì mới*.
- **Gỡ cài đặt sạch**: *Settings → Apps → Installed apps → Weather Widget → Uninstall*, hoặc nút 🗑 trong Cài đặt. Gỡ được cả khi đã lỡ xoá file exe.
- **Log & báo lỗi**: log ở `%LOCALAPPDATA%\WeatherWidget\logs` (7 ngày, không ghi toạ độ); khay → 🐞 mở trang tạo issue điền sẵn thông tin.

## Quyền riêng tư / Privacy
App **không thu thập và không gửi dữ liệu nào về cho tác giả**. Không có quảng cáo, không có telemetry.
Để hoạt động, app gọi các dịch vụ công khai sau (chỉ những thông tin cần thiết):

| Gửi đi | Tới | Để |
|---|---|---|
| Toạ độ (làm tròn 4 chữ số) | Open-Meteo, MET Norway, wttr.in | Lấy thời tiết, AQI |
| Toạ độ | BigDataCloud, Nominatim (OpenStreetMap) | Lấy tên thành phố |
| Tên thành phố bạn gõ | Open-Meteo Geocoding, Nominatim | Tìm thành phố |
| Địa chỉ IP (tự nhiên khi kết nối) | ipwho.is, ipinfo.io | Đoán vị trí khi định vị Windows tắt |
| — | GitHub API | Kiểm tra bản cập nhật |
| — | Bing, Lorem Picsum/Unsplash, Wikimedia, ZenQuotes | Hình nền, câu trích dẫn |

Cài đặt lưu trong registry `HKCU\Software\FrmWeatherWidget`; cache/log trong `%LOCALAPPDATA%\WeatherWidget` và `%TEMP%`. Gỡ cài đặt xoá hết.
Nút *Báo lỗi* chỉ **mở** trang GitHub với nội dung điền sẵn — bạn xem/sửa và tự quyết định gửi.

*This program does not send any data to its author. It contacts the public services listed above only to provide its features.*

## Nguồn dữ liệu & ghi công
Open-Meteo (CC BY 4.0) · MET Norway (CC BY 4.0) · wttr.in · © OpenStreetMap contributors (ODbL) · BigDataCloud · ipwho.is / ipinfo.io ·
Bing · Unsplash (Unsplash License, qua Lorem Picsum) · Wikimedia Commons (giấy phép theo từng ảnh) · Quotes provided by ZenQuotes API.
Chi tiết: khay → ℹ *Giới thiệu và nguồn dữ liệu*.

## Code signing policy
Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org) *(đang đăng ký — xem `docs/code-signing.md`)*.

- Committers and reviewers: [ThanhNguyenVN93](https://github.com/ThanhNguyenVN93)
- Approvers: [ThanhNguyenVN93](https://github.com/ThanhNguyenVN93)
- Chỉ các bản build từ mã nguồn của repo này trên GitHub Actions mới được ký; mỗi bản phát hành phải được người duyệt chấp thuận.

## Giấy phép
[GNU GPL v3 trở lên](LICENSE). Phần mềm tự do, **không có bất kỳ bảo hành nào** — bạn tự chịu trách nhiệm khi sử dụng.

---

## Cho nhà phát triển

| File | Vai trò |
|---|---|
| `AppController.cs` | Dữ liệu, khay, hẹn giờ, thông báo, phím tắt, nhiều màn hình, hình nền, tự cập nhật |
| `WidgetForm.cs` | Một widget trên màn hình — chỉ vẽ dữ liệu của controller, tự scale theo DPI |
| `WeatherService.cs` / `LocationService.cs` / `UpdateService.cs` | Thời tiết, vị trí, cập nhật |
| `Program.cs` | Một instance, khởi động lại sau cập nhật + quay lại bản cũ nếu lỗi |
| `AlertRules.cs`, `LunarCalendar.cs`, `Uninstaller.cs` | Quy tắc thông báo, âm lịch, gỡ cài đặt |
| `*Form.cs` | Cài đặt, chọn thành phố, có gì mới, giới thiệu |
| `tests/WeatherWidget.Tests` | Test (xUnit) — chạy trong CI trước mỗi bản build |

```bash
dotnet build -c Release
```
```bash
dotnet test tests/WeatherWidget.Tests/WeatherWidget.Tests.csproj
```
```bash
dotnet publish frmweatherwid.csproj -c Release -r win-x64 -o publish
```

`WEATHERWIDGET_SCALE=1.5` ép hệ số scale để kiểm tra giao diện 150% trên màn hình 100%.

### Phát hành bản mới
1. Tăng `<Version>` / `<FileVersion>` trong `frmweatherwid.csproj` — tăng lần lượt số cuối (1.2.3 → 1.2.4…).
2. Thêm mục của phiên bản mới (vi + en) lên đầu `Changelog.cs` (có test kiểm tra).
3. Commit, push, rồi đẩy tag:
   ```bash
   git tag v1.2.4 && git push origin v1.2.4
   ```
   GitHub Actions chạy test, build `WeatherWidget.exe`, (ký số), tạo release với ghi chú lấy từ `Changelog.cs`, (gửi lên winget). Các máy đang chạy sẽ tự cập nhật.
