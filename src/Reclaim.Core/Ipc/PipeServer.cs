using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Reclaim.Core.Application;
using Reclaim.Core.Contracts;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Ipc
{
    /// <summary>
    /// Máy chủ named pipe: mỗi kết nối = 1 yêu cầu + 1 phản hồi (JSON, có khung độ dài).
    /// Pipe cho phép người dùng cục bộ đã xác thực kết nối, chặn truy cập từ mạng.
    /// Quyền thực sự được kiểm soát bằng mật khẩu/token trong ReclaimEngine, không phải bằng ACL của pipe.
    /// </summary>
    public sealed class PipeServer : IDisposable
    {
        private const int MaxInstances = 16;
        private const int IoTimeoutMs = 5000;

        private readonly Func<Request, bool, Response> _handler;
        private bool _firstInstance = true; // chỉ luồng AcceptLoop đọc/ghi
        private readonly ILog _log;
        private volatile bool _stopped;
        private Thread _thread;

        /// <param name="handler">(yêu cầu, người gọi có quyền quản trị không) -> phản hồi. Quyền quản trị chỉ được xác định cho SetPassword.</param>
        public PipeServer(Func<Request, bool, Response> handler, ILog log)
        {
            _handler = handler;
            _log = log;
        }

        public void Start()
        {
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "reclaim-pipe" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stopped = true; // luồng nền sẽ kết thúc cùng tiến trình
        }

        private static PipeSecurity BuildSecurity()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return security;
        }

        private void AcceptLoop()
        {
            PipeSecurity security = BuildSecurity();
            while (!_stopped)
            {
                NamedPipeServerStream stream = null;
                try
                {
                    // Cá thể đầu tiên dùng FirstPipeInstance: nếu một tiến trình khác đã chiếm tên pipe trước (giả mạo dịch vụ
                    // để nhận mật khẩu), việc tạo pipe thất bại thay vì âm thầm cùng dùng chung tên.
                    PipeOptions options = _firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None;
                    stream = new NamedPipeServerStream(AppPaths.PipeName, PipeDirection.InOut, MaxInstances,
                        PipeTransmissionMode.Byte, options, 4096, 4096, security);
                    _firstInstance = false;
                    stream.WaitForConnection();
                    NamedPipeServerStream connection = stream;
                    stream = null;
                    ThreadPool.QueueUserWorkItem(delegate { Serve(connection); });
                }
                catch (Exception ex)
                {
                    if (stream != null) stream.Dispose();
                    if (_stopped) return;
                    _log.Warn("Pipe: " + ex.Message);
                    Thread.Sleep(_firstInstance ? 2000 : 500);
                }
            }
        }

        /// <summary>Phải gọi sau khi đã đọc dữ liệu từ máy khách (yêu cầu của ImpersonateNamedPipeClient).</summary>
        private static bool IsClientAdmin(NamedPipeServerStream stream)
        {
            bool admin = false;
            try
            {
                stream.RunAsClient(delegate
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                });
            }
            catch (Exception)
            {
                admin = false;
            }
            return admin;
        }

        private void Serve(NamedPipeServerStream stream)
        {
            // Chống máy khách treo: không gửi xong yêu cầu (hoặc không nhận phản hồi) trong 5 giây thì đóng kết nối.
            var watchdog = new Timer(delegate { try { stream.Dispose(); } catch { } }, null, IoTimeoutMs, Timeout.Infinite);
            try
            {
                Request request;
                try
                {
                    byte[] data = FrameIO.Read(stream);
                    request = Json.Deserialize<Request>(data);
                }
                finally
                {
                    watchdog.Dispose();
                }

                // Chỉ SetPassword cần biết người gọi có phải quản trị viên (token đã nâng quyền) hay không.
                bool callerIsAdmin = request != null && request.Op == Ops.SetPassword && IsClientAdmin(stream);
                Response response = _handler(request, callerIsAdmin);

                // Máy khách không chịu đọc phản hồi cũng không được giữ kết nối quá IoTimeoutMs.
                var writeWatchdog = new Timer(delegate { try { stream.Dispose(); } catch { } }, null, IoTimeoutMs, Timeout.Infinite);
                try
                {
                    FrameIO.Write(stream, Json.Serialize(response));
                    stream.WaitForPipeDrain();
                }
                finally
                {
                    writeWatchdog.Dispose();
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Pipe client: " + ex.Message);
            }
            finally
            {
                try { stream.Dispose(); } catch { }
            }
        }
    }
}
