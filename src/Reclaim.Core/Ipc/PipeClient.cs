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

        /// <summary>
        /// Tiến trình giữ đầu pipe phía máy chủ phải chạy từ đúng file Reclaim.Service.exe đã cài (thư mục Program Files
        /// mà tài khoản thường không ghi được). Khi chưa cài đặt (chạy thử từ thư mục build) thì chấp nhận file cạnh ứng dụng.
        /// Chỉ từ chối khi xác định được đường dẫn và nó KHÔNG khớp; không đọc được thông tin tiến trình thì cho qua.
        /// </summary>
        private static bool ServerIsTrusted(NamedPipeClientStream pipe)
        {
            uint pid;
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid))
            {
                Log.Warn("Không lấy được PID của máy chủ pipe (lỗi " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "); bỏ qua bước xác minh.");
                return true;
            }

            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
            {
                Log.Warn("Không mở được tiến trình máy chủ pipe PID " + pid + " (lỗi " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "); bỏ qua bước xác minh.");
                return true;
            }
            try
            {
                var buffer = new StringBuilder(1024);
                uint size = (uint)buffer.Capacity;
                if (!Native.QueryFullProcessImageName(process, 0, buffer, ref size))
                {
                    Log.Warn("Không đọc được đường dẫn tiến trình máy chủ pipe PID " + pid + "; bỏ qua bước xác minh.");
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
    }
}
