# Ký số exe & đưa lên winget — hướng dẫn

Mục tiêu: `WeatherWidget.exe` có chữ ký số → Windows SmartScreen không cảnh báo "Unknown publisher", và bản tự cập nhật
được đảm bảo là do bạn phát hành. Workflow (`.github/workflows/build.yml`) đã có sẵn bước ký và bước winget;
chúng **tự bỏ qua** cho tới khi bạn cấu hình xong các mục dưới đây.

---

## Cách 1 — SignPath Foundation (miễn phí cho mã nguồn mở)

### 1. Điều kiện (repo đã đáp ứng)
- [x] Repo public, giấy phép OSI (GPL-3.0-or-later — file `LICENSE`)
- [x] Đã có bản phát hành, exe được build trên CI (GitHub Actions)
- [x] README có mục **Code signing policy** và **Quyền riêng tư / Privacy**
- [ ] **Bạn bật 2FA** cho tài khoản GitHub: *GitHub → Settings → Password and authentication → Two-factor authentication*

### 2. Đăng ký
1. Vào **https://signpath.org** → *Apply* (chương trình Open Source).
2. Điền form: tên dự án `Weather Widget`, repo `https://github.com/ThanhNguyenVN93/weather-widget`,
   mô tả ngắn, loại file cần ký: **Windows executable (.exe), Authenticode**.
3. Chờ duyệt (thường vài ngày – vài tuần). Họ có thể hỏi thêm qua email.

### 3. Sau khi được duyệt — cấu hình trên SignPath
Trong trang SignPath của dự án:
- Tạo **Project** (slug ví dụ `weatherwidget`).
- **Artifact configuration** slug `initial`: một file PE `WeatherWidget.exe` nằm trong file zip artifact của GitHub.
- **Signing policy** slug `release-signing` (chứng chỉ của SignPath Foundation, cần người duyệt).
- Thêm **Trusted build system** = GitHub.com (để chỉ exe build trên GitHub Actions của repo này mới được ký).
- Tạo **API token** cho một CI user có quyền *Submitter* trên project.

### 4. Cấu hình trên GitHub (repo → *Settings → Secrets and variables → Actions*)
| Loại | Tên | Giá trị |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | API token ở bước 3 |
| Variable | `SIGNPATH_ORGANIZATION_ID` | Organization ID (trong SignPath → *Settings*) |
| Variable | `SIGNPATH_PROJECT_SLUG` | ví dụ `weatherwidget` |

Xong. Lần đẩy tag tiếp theo, workflow sẽ gửi exe sang SignPath, **chờ bạn bấm Approve** trên signpath.io
(bắt buộc mỗi bản phát hành), rồi đưa bản đã ký vào release. Workflow kiểm tra chữ ký hợp lệ trước khi phát hành.

> Lưu ý: SmartScreen dựa trên "danh tiếng". Những tuần đầu sau khi ký vẫn có thể cảnh báo cho tới khi đủ lượt tải.

## Cách 2 — Chứng chỉ Certum Open Source (trả phí, ~25–70 €/năm)
Cấp cho cá nhân sau khi xác minh giấy tờ, không cần duyệt từng bản. Khoá thường nằm trên thẻ/USB token hoặc
dịch vụ cloud của Certum (SimplySign) → phải ký trên máy có khoá rồi tải exe đã ký lên release. Nếu chọn cách này,
báo tôi để đổi bước ký trong workflow.

---

## winget (`winget install ThanhNguyenVN93.WeatherWidget`)
Không cần ký số.
1. Tạo **Personal access token (classic)** tại *GitHub → Settings → Developer settings → Personal access tokens → Tokens (classic)*,
   chỉ tick quyền **`public_repo`**, đặt hạn dùng tuỳ ý.
2. Thêm secret **`WINGET_TOKEN`** vào repo (như bảng ở trên).
3. Lần đẩy tag tiếp theo, CI tạo manifest (`.github/scripts/winget-manifests.ps1`) và mở pull request vào
   `microsoft/winget-pkgs` dưới tên tài khoản của bạn. Bản đầu tiên được người của Microsoft duyệt (vài ngày);
   các bản sau thường được duyệt tự động.

Cài qua winget: exe nằm trong `%LOCALAPPDATA%\Microsoft\WinGet\Packages\…`, app vẫn tự cập nhật bình thường,
và việc gỡ do winget đảm nhận (app không đăng ký thêm mục thứ hai trong *Installed apps*).
