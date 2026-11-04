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

        private readonly Func<Request, Response> _handler;
        private readonly ILog _log;
        private volatile bool _stopped;
        private Thread _thread;

        public PipeServer(Func<Request, Response> handler, ILog log)
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
                    stream = new NamedPipeServerStream(AppPaths.PipeName, PipeDirection.InOut, MaxInstances,
                        PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
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
                    Thread.Sleep(500);
                }
            }
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

                Response response = _handler(request);

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
