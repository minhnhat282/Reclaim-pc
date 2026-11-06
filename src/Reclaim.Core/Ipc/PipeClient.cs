using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Reclaim.Core.Contracts;
using Reclaim.Core.Domain;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Ipc
{
    public static class PipeClient
    {
        public const int DefaultTimeoutMs = 15000;

        /// <summary>Nhật ký phía giao diện (đặt bởi Reclaim.exe). Mặc định không ghi.</summary>
        public static Reclaim.Core.Application.ILog Log = NullLog.Instance;

        public static Response Send(Request request)
        {
            return Send(request, DefaultTimeoutMs);
        }

        /// <summary>
        /// Gửi một yêu cầu, chờ tối đa timeoutMs cho phản hồi (không gửi mật khẩu cho tiến trình lạ: danh tính máy chủ
        /// được kiểm tra trước khi gửi bất cứ thứ gì).
        /// </summary>
        /// <exception cref="ReclaimException">
        /// Unavailable (không kết nối được), BadServer (tiến trình giữ pipe không phải dịch vụ Reclaim đã cài),
        /// Timeout (dịch vụ không phản hồi kịp), ConnectionLost (đứt kết nối giữa chừng), Protocol (phản hồi không hợp lệ),
        /// Denied (không có quyền mở pipe).
        /// </exception>
        public static Response Send(Request request, int timeoutMs)
        {
            NamedPipeClientStream pipe = null;
            Timer watchdog = null;
            bool timedOut = false;
            try
            {
                pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.None,
                    TokenImpersonationLevel.Identification);
                pipe.Connect(3000);

                if (!ServerIsTrusted(pipe))
                {
                    Log.Error("Tiến trình giữ pipe không phải Reclaim.Service.exe đã cài; không gửi yêu cầu " + request.Op + ".");
                    throw new ReclaimException(ErrorCodes.BadServer,
                        "Tiến trình đang giữ kênh liên lạc không phải dịch vụ Reclaim đã cài. Không gửi thông tin tới nó.");
                }

                NamedPipeClientStream local = pipe;
                watchdog = new Timer(delegate
                {
                    timedOut = true;
                    try { local.Dispose(); } catch { }
                }, null, timeoutMs, Timeout.Infinite);

                FrameIO.Write(pipe, Json.Serialize(request));
                byte[] data = FrameIO.Read(pipe);
                return Json.Deserialize<Response>(data);
            }
            catch (ReclaimException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                throw new ReclaimException(ErrorCodes.Unavailable, "Dịch vụ Reclaim chưa chạy.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new ReclaimException(ErrorCodes.Denied, "Không có quyền kết nối tới dịch vụ Reclaim.");
            }
            catch (Exception ex)
            {
                Log.Warn("Liên lạc với dịch vụ lỗi (" + (request == null ? "?" : request.Op) + "): " + ex.GetType().Name + ": " + ex.Message);
                if (timedOut)
                {
                    throw new ReclaimException(ErrorCodes.Timeout,
                        "Dịch vụ Reclaim không phản hồi trong " + (timeoutMs / 1000) + " giây.");
                }
                if (ex is IOException || ex is ObjectDisposedException)
                {
                    throw new ReclaimException(ErrorCodes.ConnectionLost, "Dịch vụ Reclaim đã đóng kết nối đột ngột.");
                }
                if (ex is InvalidDataException || ex is System.Runtime.Serialization.SerializationException)
                {
                    throw new ReclaimException(ErrorCodes.Protocol, "Phản hồi từ dịch vụ Reclaim không hợp lệ.");
                }
                throw;
            }
            finally
            {
                if (watchdog != null) watchdog.Dispose();
                if (pipe != null) pipe.Dispose();
            }
        }

        // Mỗi lý do "không xác minh được" chỉ ghi một lần cho mỗi lần chạy giao diện (giao diện hỏi dịch vụ mỗi ~2,5 giây).
        private static readonly System.Collections.Generic.HashSet<string> _logged = new System.Collections.Generic.HashSet<string>();

        private static void LogOnce(string key, string message)
        {
            lock (_logged)
            {
                if (!_logged.Add(key)) return;
            }
            Log.Warn(message);
        }

        /// <summary>
        /// Xác minh tiến trình giữ đầu pipe phía máy chủ chính là dịch vụ Reclaim đã đăng ký.
        ///  1. Cách chính: hỏi Service Control Manager PID của dịch vụ "Reclaim" và so với PID của máy chủ pipe. Tài khoản thường
        ///     được phép hỏi trạng thái dịch vụ (DACL dịch vụ có quyền LC cho người dùng), trong khi KHÔNG mở được tiến trình
        ///     SYSTEM của dịch vụ (OpenProcess báo lỗi 5), nên cách đường dẫn tiến trình không dùng được cho tài khoản thường.
        ///  2. Dự phòng (SCM không cho biết PID: chưa cài đặt, đang chạy thử từ thư mục build): so đường dẫn file thực thi nếu mở được.
        /// Chỉ từ chối khi xác định được và KHÔNG khớp; không xác minh được thì cho qua và ghi nhật ký một lần.
        /// </summary>
        private static bool ServerIsTrusted(NamedPipeClientStream pipe)
        {
            uint pid;
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid))
            {
                LogOnce("pid", "Không lấy được PID của máy chủ pipe (lỗi " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "); bỏ qua bước xác minh.");
                return true;
            }

            uint servicePid = QueryServicePid();
            if (servicePid != 0) return servicePid == pid;

            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
            {
                LogOnce("open", "SCM không cho biết PID dịch vụ và không mở được tiến trình máy chủ pipe (lỗi " +
                                System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "); bỏ qua bước xác minh.");
                return true;
            }
            try
            {
                var buffer = new StringBuilder(1024);
                uint size = (uint)buffer.Capacity;
                if (!Native.QueryFullProcessImageName(process, 0, buffer, ref size))
                {
                    LogOnce("path", "Không đọc được đường dẫn tiến trình máy chủ pipe; bỏ qua bước xác minh.");
                    return true;
                }

                string actual = Path.GetFullPath(buffer.ToString());
                string installed = Path.GetFullPath(Path.Combine(AppPaths.InstallDir, AppPaths.ServiceExeName));
                if (string.Equals(actual, installed, StringComparison.OrdinalIgnoreCase)) return true;

                if (!File.Exists(installed))
                {
                    string local = Path.GetFullPath(Path.Combine(AppPaths.BaseDir, AppPaths.ServiceExeName));
                    return string.Equals(actual, local, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
            finally
            {
                Native.CloseHandle(process);
            }
        }

        /// <returns>PID tiến trình của dịch vụ Reclaim theo SCM; 0 nếu không biết (chưa cài, đang dừng, không đủ quyền).</returns>
        private static uint QueryServicePid()
        {
            IntPtr manager = Native.OpenSCManager(null, null, Native.SC_MANAGER_CONNECT);
            if (manager == IntPtr.Zero) return 0;
            try
            {
                IntPtr service = Native.OpenService(manager, AppPaths.ServiceName, Native.SERVICE_QUERY_STATUS);
                if (service == IntPtr.Zero) return 0;
                try
                {
                    const int size = 36; // sizeof(SERVICE_STATUS_PROCESS)
                    IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                    try
                    {
                        int needed;
                        if (!Native.QueryServiceStatusEx(service, 0, buffer, size, out needed)) return 0;
                        return (uint)System.Runtime.InteropServices.Marshal.ReadInt32(buffer, 28); // dwProcessId
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    Native.CloseServiceHandle(service);
                }
            }
            finally
            {
                Native.CloseServiceHandle(manager);
            }
        }
    }
}
