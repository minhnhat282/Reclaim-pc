using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
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
        private bool _firstInstance = true;   // chỉ luồng AcceptLoop đọc/ghi
        private bool _useNativeFirst = true;   // tắt nếu tạo cá thể đầu tiên bằng Win32 gặp lỗi lạ
        private string _lastPipeError = string.Empty;
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

        /// <summary>Có tiến trình khác đã giữ tên pipe trước dịch vụ.</summary>
        private sealed class PipeNameTakenException : IOException
        {
            public PipeNameTakenException(string message) : base(message) { }
        }

        /// <summary>
        /// Tạo cá thể pipe ĐẦU TIÊN bằng CreateNamedPipe với FILE_FLAG_FIRST_PIPE_INSTANCE: nếu một tiến trình khác đã giữ tên
        /// này trước (giả mạo dịch vụ để nhận mật khẩu) thì thất bại thay vì âm thầm dùng chung tên. .NET Framework 4.8 từ chối
        /// cờ này khi truyền qua PipeOptions nên phải gọi thẳng Win32. Các cá thể sau tạo bằng NamedPipeServerStream thông thường
        /// với cùng chế độ (byte, chờ, tối đa MaxInstances) nên khớp với cá thể đầu.
        /// </summary>
        private static NamedPipeServerStream CreateFirstInstance(PipeSecurity security)
        {
            byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
            IntPtr descriptorPtr = Marshal.AllocHGlobal(descriptor.Length);
            try
            {
                Marshal.Copy(descriptor, 0, descriptorPtr, descriptor.Length);
                var attributes = new Native.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(Native.SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = descriptorPtr,
                    bInheritHandle = false
                };
                IntPtr handle = Native.CreateNamedPipe(@"\\.\pipe\" + AppPaths.PipeName,
                    Native.PIPE_ACCESS_DUPLEX | Native.FILE_FLAG_FIRST_PIPE_INSTANCE,
                    0 /* byte, byte, chờ */, MaxInstances, 4096, 4096, 0, ref attributes);
                if (handle == new IntPtr(-1))
                {
                    int error = Marshal.GetLastWin32Error();
                    // 5 = ACCESS_DENIED, 231 = PIPE_BUSY: tên đã có chủ.
                    if (error == 5 || error == 231) throw new PipeNameTakenException("CreateNamedPipe lỗi " + error);
                    throw new IOException("CreateNamedPipe lỗi " + error);
                }
                return new NamedPipeServerStream(PipeDirection.InOut, false, false, new SafePipeHandle(handle, true));
            }
            finally
            {
                Marshal.FreeHGlobal(descriptorPtr);
            }
        }

        private void AcceptLoop()
        {
            PipeSecurity security = BuildSecurity();
            while (!_stopped)
            {
                NamedPipeServerStream stream = null;
                try
                {
                    if (_firstInstance && _useNativeFirst)
                    {
                        stream = CreateFirstInstance(security);
                    }
                    else
                    {
                        if (_firstInstance && File.Exists(@"\\.\pipe\" + AppPaths.PipeName))
                        {
                            throw new PipeNameTakenException("tên pipe đã tồn tại trước khi dịch vụ tạo");
                        }
                        stream = new NamedPipeServerStream(AppPaths.PipeName, PipeDirection.InOut, MaxInstances,
                            PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
                    }
                    if (_firstInstance)
                    {
                        _firstInstance = false;
                        _log.Info("Pipe " + AppPaths.PipeName + " đã sẵn sàng" + (_useNativeFirst ? " (cá thể đầu có cờ FIRST_PIPE_INSTANCE)." : "."));
                    }
                    stream.WaitForConnection();
                    NamedPipeServerStream connection = stream;
                    stream = null;
                    ThreadPool.QueueUserWorkItem(delegate { Serve(connection); });
                }
                catch (PipeNameTakenException ex)
                {
                    if (stream != null) stream.Dispose();
                    if (_stopped) return;
                    LogPipeErrorOnce("Tên pipe " + AppPaths.PipeName + " đã bị một tiến trình khác chiếm trước (" + ex.Message + "). Thử lại mỗi 2 giây.");
                    Thread.Sleep(2000);
                }
                catch (Exception ex)
                {
                    if (stream != null) stream.Dispose();
                    if (_stopped) return;
                    if (_firstInstance && _useNativeFirst)
                    {
                        // Lỗi lạ khi tạo bằng Win32 (không phải "tên đã có chủ"): quay về cách tạo thông thường để dịch vụ vẫn chạy.
                        _useNativeFirst = false;
                        _log.Warn("Pipe: tạo cá thể đầu bằng Win32 thất bại (" + ex.Message + "); dùng cách tạo thông thường.");
                        continue;
                    }
                    LogPipeErrorOnce("Pipe: " + ex.Message);
                    Thread.Sleep(_firstInstance ? 2000 : 500);
                }
            }
        }

        // Chỉ ghi khi lỗi đổi, tránh tràn nhật ký trong vòng thử lại.
        private void LogPipeErrorOnce(string message)
        {
            if (message == _lastPipeError) return;
            _lastPipeError = message;
            _log.Warn(message);
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
