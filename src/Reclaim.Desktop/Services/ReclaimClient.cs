using System.Threading.Tasks;
using Reclaim.Core.Contracts;
using Reclaim.Core.Domain;
using Reclaim.Core.Ipc;

namespace Reclaim.Desktop.Services
{
    /// <summary>
    /// Máy khách của dịch vụ Reclaim. Giao diện không chứa nghiệp vụ nào: mọi thứ (kể cả kiểm tra mật khẩu)
    /// nằm ở dịch vụ. Token mở khóa chỉ được giữ trong bộ nhớ của tiến trình này.
    /// </summary>
    internal sealed class ReclaimClient
    {
        public string Token { get; private set; }

        public async Task<StateDto> GetStateAsync()
        {
            Response r = await CallAsync(new Request { Op = Ops.GetState }, 10000).ConfigureAwait(false);
            if (r.State != null && !r.State.Unlocked) Token = null;
            return r.State;
        }

        public async Task UnlockAsync(string password)
        {
            Response r = await CallAsync(new Request { Op = Ops.Unlock, Password = password }, 30000).ConfigureAwait(false);
            Token = r.Token;
        }

        public async Task LockAsync()
        {
            await CallAsync(new Request { Op = Ops.Lock }).ConfigureAwait(false);
            Token = null;
        }

        public async Task SetPasswordAsync(string newPassword)
        {
            Response r = await CallAsync(new Request { Op = Ops.SetPassword, NewPassword = newPassword }, 30000).ConfigureAwait(false);
            Token = r.Token;
        }

        public async Task ChangePasswordAsync(string oldPassword, string newPassword)
        {
            Response r = await CallAsync(new Request { Op = Ops.ChangePassword, Password = oldPassword, NewPassword = newPassword }, 30000).ConfigureAwait(false);
            Token = r.Token;
        }

        public async Task RemovePasswordAsync(string password)
        {
            await CallAsync(new Request { Op = Ops.RemovePassword, Password = password }, 60000).ConfigureAwait(false);
            Token = null;
        }

        public async Task<StateDto> SaveAsync(string profileId, bool enabled)
        {
            Response r = await CallAsync(new Request { Op = Ops.Save, ProfileId = profileId, Enabled = enabled }, 180000).ConfigureAwait(false);
            return r.State;
        }

        public async Task<StateDto> SetProtectionAsync(ProtectionOptions options)
        {
            Response r = await CallAsync(new Request { Op = Ops.SetProtection, Protection = options }, 60000).ConfigureAwait(false);
            return r.State;
        }

        public async Task BeginMaintenanceAsync()
        {
            await CallAsync(new Request { Op = Ops.Maintenance }, 60000).ConfigureAwait(false);
        }

        public async Task<string[]> GetLogAsync()
        {
            Response r = await CallAsync(new Request { Op = Ops.GetLog }).ConfigureAwait(false);
            return r.Log ?? new string[0];
        }

        /// <summary>Đồng bộ, dùng khi đóng cửa sổ: thu hồi token ở dịch vụ.</summary>
        public void LockQuietly()
        {
            if (Token == null) return;
            try { PipeClient.Send(new Request { Op = Ops.Lock, Token = Token }); }
            catch (ReclaimException) { }
            Token = null;
        }

        private async Task<Response> CallAsync(Request request, int timeoutMs = PipeClient.DefaultTimeoutMs)
        {
            request.Token = Token;
            Response response = await Task.Run(() => PipeClient.Send(request, timeoutMs)).ConfigureAwait(false);
            if (!response.Ok)
            {
                if (response.Code == ErrorCodes.NeedUnlock) Token = null;
                throw new ReclaimException(response.Code ?? ErrorCodes.Internal, response.Message ?? "Lỗi không xác định.");
            }
            return response;
        }
    }
}
