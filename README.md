# Weather Widget (Hanoi)

Widget thời tiết nhỏ cho Windows (WinForms, .NET 10), sống ở khay hệ thống.

## Tính năng
- Nhiệt độ, độ ẩm, gió, cảm giác như, AQI (US) cho Hà Nội (trạm Láng, 21.0245, 105.8412).
- **Nguồn thời tiết có fallback** (đều miễn phí, không cần API key) — `WeatherService.cs`:
  1. [Open-Meteo](https://open-meteo.com) — nguồn chính
  2. [MET Norway](https://api.met.no) — dự phòng 1 (cảm giác như tính theo công thức BoM)
  3. [wttr.in](https://wttr.in) — dự phòng 2

  Mỗi nguồn timeout 8s; nguồn nào lỗi sẽ bị bỏ qua 15 phút. AQI lấy từ Open-Meteo Air Quality (tùy chọn, lỗi không ảnh hưởng thời tiết).
- Làm mới mỗi 30 phút; nếu tất cả nguồn lỗi thì thử lại sau 5 phút và giữ số liệu cũ.
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
