# Thành phần bên thứ ba

## NextDNS CLI

- Dự án: https://github.com/nextdns/nextdns
- Giấy phép: MIT
- Reclaim đóng gói `nextdns.exe` nguyên bản (không sửa đổi). Văn bản giấy phép đầy đủ được kèm theo
  tại `nextdns\LICENSE.txt`, cạnh `nextdns.exe` trong thư mục cài đặt.
- Khi build bằng GitHub Actions, file này được lấy từ repo gốc của nextdns ở đúng thẻ phiên bản đang dùng.
  Khi build cục bộ, chép `LICENSE` từ repo nextdns vào `third_party\nextdns\LICENSE.txt`.
