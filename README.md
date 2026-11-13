# Weather Widget

Widget thời tiết nhỏ cho Windows 10/11 x64 (WinForms, .NET 10), sống ở khay hệ thống. Một file `WeatherWidget.exe`, không cần cài .NET.

## Tính năng
- Nhiệt độ, độ ẩm, gió, cảm giác như, AQI (US) tại vị trí hiện tại; icon theo ngày/đêm.
- **Dự báo** (nút ▼ Dự báo): giờ mặt trời mọc/lặn + UV, 24 giờ tới (mỗi 3 giờ), 6 ngày tới. Nguồn không có giờ mọc/lặn thì app tự tính (NOAA).
- **Thông báo**: sắp mưa trong 60 phút (dữ liệu mưa theo 15 phút), không khí xấu (AQI > 150). Mỗi sự kiện báo một lần.
- **Vị trí 3 lớp** — `LocationService.cs`:
  1. **Chọn thủ công**: khay → 📍 Vị trí → *Chọn thành phố…*; thành phố đã chọn được lưu (tối đa 8) để đổi nhanh. *Tự động định vị* để quay lại.
  2. **Định vị Windows** (Wi-Fi/GPS): cần bật *Settings → Privacy & security → Location*.
  3. **Theo IP** (ipwho.is → ipinfo.io): cấp thành phố; khi bật VPN sẽ ra vị trí máy chủ VPN.

  Không lớp nào được thì dùng vị trí tự động lần trước, cuối cùng là Hà Nội.
- **Nguồn thời tiết có fallback** (miễn phí, không cần API key) — `WeatherService.cs`: [Open-Meteo](https://open-meteo.com) → [MET Norway](https://api.met.no) → [wttr.in](https://wttr.in). Mỗi nguồn timeout 8s, lỗi thì bỏ qua 15 phút. Làm mới mỗi 15 phút; mất mạng thì thử lại sau 5 phút và giữ số liệu cũ.
- **Nhiều màn hình**: mỗi màn hình một widget (tắt được trong Cài đặt). Kéo widget đi đâu cũng được, vị trí được nhớ theo từng màn hình; rút màn hình ra thì widget của nó tự đóng.
- **Phím tắt** ẩn/hiện widget (mặc định `Ctrl+Alt+W`), `ESC` để ẩn.
- **Cài đặt** (khay → ⚙): ngôn ngữ, °C/°F, độ trong suốt, hình nền (online / thư mục ảnh / tắt), thông báo, phím tắt, thành phố đã lưu. Áp dụng ngay. Mở thẳng bằng `WeatherWidget.exe --settings`.
- **Ngôn ngữ** Tiếng Việt / English, mặc định theo Windows — `Strings.cs`.
- **Tự cập nhật** — `UpdateService.cs`: 1 phút sau khi khởi động rồi mỗi 6 giờ kiểm tra GitHub Releases; tải `WeatherWidget.exe`, kiểm tra kích thước + SHA-256, thay file và khởi động lại (đợi lúc widget đang ẩn). Sau đó hiện bảng *Có gì mới* (`Changelog.cs`). Exe để trong `Program Files` thì không tự cập nhật được.
- **Log & báo lỗi**: log ở `%LOCALAPPDATA%\WeatherWidget\logs` (giữ 7 ngày, không ghi toạ độ). Khay → 🐞 *Báo lỗi trên GitHub* mở trang tạo issue đã điền sẵn phiên bản, Windows, log gần nhất — bạn xem/sửa trước khi gửi.
- **Chỉ chạy 1 instance**: mở lần nữa sẽ hiện widget đang chạy.
- Hình nền xoay vòng (Bing + Wallhaven), cache `%TEMP%\FrmWeatherWidget` (tự xoá >14 ngày). Trích dẫn ZenQuotes. ♥ Ủng hộ trong menu khay.

## Cấu trúc
| File | Vai trò |
|---|---|
| `AppController.cs` | Dữ liệu, khay, hẹn giờ, thông báo, phím tắt, nhiều màn hình, hình nền, tự cập nhật |
| `WidgetForm.cs` | Một widget trên màn hình — chỉ vẽ dữ liệu của controller |
| `WeatherService.cs` / `LocationService.cs` / `UpdateService.cs` | Thời tiết, vị trí, cập nhật |
| `SettingsForm.cs`, `CityPickerForm.cs`, `WhatsNewForm.cs` | Các cửa sổ |
| `AppSettings.cs`, `Strings.cs`, `Changelog.cs`, `Visuals.cs`, `AppLog.cs`, `BugReport.cs` | Cài đặt, ngôn ngữ, ghi chú phiên bản, vẽ, log, báo lỗi |

## Build
```bash
dotnet build -c Release
```

Một file exe tự chạy (không cần cài .NET):
```bash
dotnet publish -c Release -r win-x64 -o publish
```

## Phát hành bản mới
1. Tăng `<Version>` / `<FileVersion>` trong `frmweatherwid.csproj` — tăng lần lượt số cuối (1.2.1 → 1.2.2 → 1.2.3…).
2. Thêm mục của phiên bản mới (vi + en) lên đầu `Changelog.cs`.
3. Commit, push, rồi đẩy tag:
   ```bash
   git tag v1.2.3 && git push origin v1.2.3
   ```
   GitHub Actions (`.github/workflows/build.yml`) kiểm tra tag khớp `<Version>`, build `WeatherWidget.exe` và tạo release với ghi chú lấy từ `Changelog.cs`. Các máy đang chạy sẽ tự cập nhật.
