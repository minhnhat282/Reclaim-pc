# Reclaim

Ứng dụng Windows 10/11 nhẹ, dùng **nextdns CLI nguyên bản** (không sửa đổi) để lọc DNS theo cấu hình NextDNS của bạn,
kèm các lớp tự bảo vệ bằng mật khẩu. Mọi cài đặt lọc nâng cao chỉnh trên web my.nextdns.io.

## Yêu cầu build
- Visual Studio 2022 (hoặc 2019 16.9+) với workload **".NET desktop development"**
  và **.NET Framework 4.8 targeting pack**.
- `nextdns.exe` (Windows amd64, 1.49.0) đặt vào `third_party\nextdns\` - xem `third_party\nextdns\README.txt`.
- Không dùng gói NuGet nào.

## Build và chạy thử
1. Mở `Reclaim.sln`, chọn cấu hình **Release** (hoặc Debug), **Build Solution**.
   Kết quả nằm ở `build\Release\`: `Reclaim.exe`, `Reclaim.Service.exe`, `Reclaim.Core.dll`, `nextdns\nextdns.exe`.
2. Chạy `build\Release\Reclaim.exe` -> bấm **Cài đặt Reclaim** (hộp thoại UAC). Việc cài đặt sẽ:
   chép file vào `C:\Program Files\Reclaim`, đăng ký dịch vụ `ReclaimSvc` (tự chạy, tự khởi động lại khi lỗi),
   thêm mục vào *Apps & features* và Start Menu, rồi khởi động dịch vụ.
3. Nhập NextDNS ID, bật "Bật bảo vệ DNS", **Lưu và áp dụng**. Reclaim khởi động nextdns, kiểm tra nó trả lời,
   rồi mới trỏ DNS của các adapter về 127.0.0.1 (nhập sai ID sẽ KHÔNG làm mất mạng; cấu hình cũ được khôi phục).
4. Kiểm tra: mở https://test.nextdns.io - phải thấy `status: ok` và đúng profile.

Gỡ lỗi không cần cài dịch vụ: chạy VS với quyền Administrator, hoặc
`Reclaim.Service.exe run` trong console quản trị (đừng chạy song song với dịch vụ đã cài vì trùng cổng 53).
Nhật ký: `C:\ProgramData\Reclaim\reclaim.log`; nextdns ghi vào Event Viewer (nguồn "nextdns").

## Tạo file cài đặt cho máy khác
Cách nhanh: cài **Inno Setup 6** (miễn phí, https://jrsoftware.org/isdl.php), đặt `nextdns.exe` vào `third_party\nextdns\`,
rồi bấm đúp **`make-installer.bat`** -> được `installer\Output\ReclaimSetup.exe`.
Máy đích: Windows 10/11 64-bit có .NET Framework 4.8 (Windows 10 từ bản 1903 và Windows 11 đã có sẵn).
Cài im lặng: `ReclaimSetup.exe /VERYSILENT`. Gỡ cài đặt: Apps & features -> Reclaim (hỏi mật khẩu nếu đang bật bảo vệ).
Không dùng Inno Setup: chép nguyên thư mục `build\Release` sang máy khác, chạy `Reclaim.exe` -> "Cài đặt Reclaim".

## Kiến trúc
```
Reclaim.exe (WPF, chạy quyền user, không thường trú)
      │ named pipe "reclaim-ipc" (JSON)
      ▼
Reclaim.Service.exe (Windows service, LocalSystem)
      ├─ nextdns.exe run -config-file ...   (tiến trình con, tự khởi động lại)
      ├─ ép DNS adapter = 127.0.0.1 / ::1   (sự kiện mạng + kiểm tra mỗi 3 giây)
      └─ mật khẩu, token mở khóa, ACL/DACL tự bảo vệ
```
| Dự án | Vai trò |
|---|---|
| `Reclaim.Core` | `Domain` (mô hình, kiểm tra ID), `Security` (PBKDF2, token), `Application` (ReclaimEngine + interface/ports, **không phụ thuộc Windows**), `Contracts`/`Ipc` (giao thức), `Infrastructure` (nextdns, netsh, ACL, installer), `Hosting` (lắp ráp) |
| `Reclaim.Service` | Vỏ mỏng của dịch vụ + lệnh `install` / `uninstall` / `run` |
| `Reclaim.Desktop` | Giao diện WPF, không chứa nghiệp vụ |

Điểm gắn với nextdns **duy nhất** là `Infrastructure/NextDnsConfig.cs` (định dạng file cấu hình) và
`NextDnsProcessRunner.cs` (dòng lệnh `run -config-file`).

### Nâng cấp nextdns
1. Thay `third_party\nextdns\nextdns.exe` bằng bản mới, build lại.
2. Nếu đang bật bảo vệ: mở Reclaim -> mở khóa -> **Thêm… -> Tạm gỡ bảo vệ 5 phút**.
3. Chạy lại **Cài đặt** (từ `build\...\Reclaim.exe` khi chưa cài, hoặc `Reclaim.Service.exe install` bằng quyền quản trị).
   Cài đặt đè lên bản cũ, giữ nguyên cài đặt/mật khẩu, và ghi nhận mã băm SHA-256 mới của nextdns.exe.
Nếu nextdns đổi cờ/định dạng cấu hình, chỉ cần sửa `NextDnsConfig.cs`.

## Tự bảo vệ (cần đặt mật khẩu trước)
Mật khẩu: 8-64 ký tự ASCII (chữ, số, ký hiệu). PBKDF2-SHA256 600.000 vòng, muối ngẫu nhiên, chỉ dịch vụ xác thực.
Sai 5 lần thì bị khóa tạm (15 giây, gấp đôi mỗi lần, tối đa 15 phút). **Không có cách lấy lại mật khẩu.**

| Tùy chọn | Cơ chế |
|---|---|
| Chống gỡ cài đặt | Mục Apps & features chạy `Reclaim.exe /uninstall` (đòi mật khẩu); DACL dịch vụ bỏ quyền xóa; thư mục cài đặt chỉ SYSTEM ghi được |
| Chống thay đổi cài đặt | Đổi ID/bật-tắt cần token mở khóa; thư mục dữ liệu chỉ SYSTEM ghi; DNS adapter bị đổi tay sẽ bị trả lại |
| Chống vô hiệu hóa dịch vụ | DACL dịch vụ bỏ quyền start/stop/đổi cấu hình của Administrators; khóa registry dịch vụ; tự khởi động lại; chạy cả trong Safe Mode with Networking |

### Giới hạn (cần hiểu rõ)
Quản trị viên cục bộ quyết tâm vẫn vượt qua được (Safe Mode không có mạng, công cụ chạy quyền SYSTEM như PsExec -s,
khởi động từ USB, cài lại Windows...). Các lớp trên chặn thao tác thông thường (Task Manager, services.msc, `sc`, `net stop`,
xóa thư mục, gỡ trong Settings). Để mạnh nhất: dùng tài khoản **Standard** hằng ngày, mật khẩu admin Windows do người khác giữ.
Phiên bản này chưa chặn: DoH riêng của trình duyệt, VPN, phần mềm hard-code DNS (cổng 53/853 ra ngoài).
Hướng mở rộng: luật Windows Firewall chỉ cho nextdns.exe ra cổng 53/853, và policy tắt Secure DNS của Chrome/Edge/Firefox.

## Hành vi cần biết
- **DNS gốc được lưu ở hai nơi** (`dns-backup.json` và `HKLM\SOFTWARE\Reclaim\DnsBackup`); mất một nơi vẫn khôi phục được.
- **Dọn "mồ côi":** nếu cả hai bản sao lưu đều mất, adapter chỉ trỏ đúng `127.0.0.1`/`::1` mà không có bộ phân giải nào trả lời sẽ được trả về DHCP khi tắt bảo vệ, dừng dịch vụ hoặc gỡ cài đặt. Nếu có bộ phân giải đang trả lời ở loopback (có thể của bạn), Reclaim không đụng vào.
- **Khởi động cùng Windows:** dịch vụ `ReclaimSvc` được đăng ký `start= auto` (chạy trước khi đăng nhập, không cần ai đăng nhập) và tự khởi động lại khi lỗi. Phần bảo vệ DNS vì thế luôn chạy từ lúc bật máy. Cửa sổ `Reclaim.exe` chỉ để cấu hình nên không chạy nền.
- Chỉ một "chủ" Reclaim chạy cùng lúc: chạy `Reclaim.Service.exe run` khi dịch vụ đang chạy sẽ bị từ chối (tránh tranh cổng 53 và dừng nhầm nextdns của nhau).
- Bản sao lưu DNS phải được ghi xuống đĩa thành công thì Reclaim mới đổi DNS của adapter. Adapter có trong bản sao lưu nhưng đang vắng mặt được khôi phục thẳng trong registry khi tắt/gỡ.
- Bật tự bảo vệ: Reclaim áp dụng ACL/DACL **trước**, chỉ lưu cấu hình khi mọi lớp thành công; nếu lỗi sẽ hoàn nguyên và báo lỗi cụ thể.
- Khôi phục DNS: bản sao lưu của adapter nào chưa khôi phục được sẽ được giữ lại để thử lại; không bao giờ tự đổi DNS tĩnh gốc sang DHCP. Tắt bảo vệ/gỡ cài đặt bị hủy nếu không trả được DNS.
- Khi đã đặt mật khẩu, xem nhật ký nextdns cũng cần mở khóa. File cài đặt và sao lưu DNS được ghi nguyên tử (có `.bak`).
- Nếu nextdns chết/chưa chạy, DNS vẫn trỏ 127.0.0.1 nên mạng tạm mất cho tới khi dịch vụ dựng lại nextdns (vài giây) - chủ ý "fail-closed".
- `detect-captive-portals` tắt: không đăng nhập được Wi-Fi công cộng cần trang đăng nhập khi đang bật bảo vệ (tắt bảo vệ tạm để đăng nhập).
- Dừng dịch vụ chủ động (bảo trì/gỡ) sẽ trả DNS về ban đầu (khôi phục cả DNS tĩnh đã lưu trong `dns-backup.json`); tắt máy thì giữ nguyên.
- IPv6 trỏ về `::1` giống `nextdns activate`; nextdns chỉ nghe IPv4 nên truy vấn IPv6 thất bại ngay và Windows dùng IPv4.
- Reclaim không có telemetry; chỉ gọi mạng qua nextdns (và một truy vấn DNS thử khi áp dụng cấu hình).

## Giả định chưa kiểm chứng (kiểm tra khi chạy thử lần đầu)
Mã được viết mà không có trình biên dịch .NET để build thử, nên hãy kỳ vọng có thể phải sửa vài lỗi biên dịch nhỏ. Các hành vi cần xác nhận trên máy thật:
1. nextdns chạy foreground khi là tiến trình con của dịch vụ (mã nguồn cho thấy được, vì tiến trình cha không phải `services.exe`).
2. ID sai khiến NextDNS trả lỗi (SERVFAIL) chứ không âm thầm dùng cấu hình mặc định - ảnh hưởng độ chính xác của bước kiểm tra ID.
3. DACL của dịch vụ thực sự chặn `sc stop`/`net stop` từ Administrators (thử bằng cmd quản trị khi bật "Chống vô hiệu hóa dịch vụ").
4. `netsh ... set dnsservers ... validate=no` chạy tốt trên cả Win 10 và Win 11 của bạn.

## Build trên GitHub (Actions)

Workflow `.github/workflows/build.yml` chạy trên `windows-latest`: tải `nextdns.exe` bản phát hành chính thức (phiên bản đặt ở `NEXTDNS_VERSION` trong file workflow), build Release, tạo `ReclaimSetup.exe` bằng Inno Setup và đưa lên mục **Artifacts** của lần chạy.

- **Ghim hash (khuyến nghị):** lần chạy đầu, SHA-256 của `nextdns.exe` hiện ở phần Summary của lần chạy. Chép giá trị đó (một dòng) vào `third_party/nextdns/nextdns.sha256` và commit; từ đó build sẽ dừng nếu file tải về không khớp.
- **Tạo bản phát hành:** đẩy tag dạng `v1.0.1` thì workflow tự tạo GitHub Release kèm `ReclaimSetup.exe`.
- Muốn dùng `nextdns.exe` của riêng bạn: bỏ nó vào `third_party/nextdns/` (đã nằm trong `.gitignore`, nên cần `git add -f`) và workflow sẽ không tải nữa.
- File cài đặt chưa được ký số nên Windows SmartScreen có thể cảnh báo.
