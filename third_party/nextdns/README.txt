Đặt file nextdns.exe (Windows amd64, bản 1.49.0 hoặc mới hơn) vào thư mục này.

Cách 1 - tải bản phát hành:
  https://github.com/nextdns/nextdns/releases  ->  nextdns_1.49.0_windows_amd64.zip
  (giải nén, lấy nextdns.exe)

Cách 2 - tự build từ mã nguồn (cần Go):
  cd nextdns-1.49.0
  set GOOS=windows
  set GOARCH=amd64
  go build -trimpath -ldflags "-s -w -X main.version=1.49.0" -o nextdns.exe .

Khi build Reclaim, file này được tự chép vào <thư mục build>\nextdns\nextdns.exe.
Reclaim KHÔNG sửa nextdns. Nâng cấp = thay file này rồi build/cài đặt lại.
