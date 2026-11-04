using System;
using System.IO;
using System.IO.Pipes;
using Reclaim.Core.Contracts;
using Reclaim.Core.Domain;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Ipc
{
    public static class PipeClient
    {
        /// <exception cref="ReclaimException">Mã Unavailable nếu không kết nối được dịch vụ.</exception>
        public static Response Send(Request request)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(3000);
                    FrameIO.Write(pipe, Json.Serialize(request));
                    byte[] data = FrameIO.Read(pipe);
                    return Json.Deserialize<Response>(data);
                }
            }
            catch (TimeoutException)
            {
                throw Unavailable();
            }
            catch (IOException)
            {
                throw Unavailable();
            }
            catch (UnauthorizedAccessException)
            {
                throw Unavailable();
            }
            catch (InvalidDataException)
            {
                throw Unavailable();
            }
            catch (System.Runtime.Serialization.SerializationException)
            {
                throw Unavailable();
            }
        }

        private static ReclaimException Unavailable()
        {
            return new ReclaimException(ErrorCodes.Unavailable, "Dịch vụ Reclaim chưa chạy.");
        }
    }
}
