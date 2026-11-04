using System;
using Reclaim.Core.Application;
using Reclaim.Core.Contracts;
using Reclaim.Core.Domain;

namespace Reclaim.Core.Ipc
{
    /// <summary>Ánh xạ Request -> ReclaimEngine. Không chứa nghiệp vụ.</summary>
    public sealed class RequestRouter
    {
        private readonly ReclaimEngine _engine;
        private readonly ILog _log;

        public RequestRouter(ReclaimEngine engine, ILog log)
        {
            _engine = engine;
            _log = log;
        }

        public Response Handle(Request r)
        {
            try
            {
                if (r == null || string.IsNullOrEmpty(r.Op))
                    return Fail(ErrorCodes.BadRequest, "Yêu cầu không hợp lệ.");

                switch (r.Op)
                {
                    case Ops.GetState:
                        return Ok(_engine.GetState(r.Token), null);

                    case Ops.Unlock:
                    {
                        string t = _engine.Unlock(r.Password);
                        return Ok(_engine.GetState(t), t);
                    }
                    case Ops.Lock:
                        _engine.Lock(r.Token);
                        return Ok(_engine.GetState(null), null);

                    case Ops.SetPassword:
                    {
                        string t = _engine.SetInitialPassword(r.NewPassword);
                        return Ok(_engine.GetState(t), t);
                    }
                    case Ops.ChangePassword:
                    {
                        string t = _engine.ChangePassword(r.Password, r.NewPassword);
                        return Ok(_engine.GetState(t), t);
                    }
                    case Ops.RemovePassword:
                        _engine.RemovePassword(r.Password);
                        return Ok(_engine.GetState(null), null);

                    case Ops.SetProtection:
                        return Ok(_engine.SetProtection(r.Token, r.Protection), null);

                    case Ops.Save:
                        return Ok(_engine.SaveSettings(r.Token, r.ProfileId, r.Enabled), null);

                    case Ops.Maintenance:
                        return Ok(_engine.BeginMaintenance(r.Token), null);

                    case Ops.GetLog:
                        return new Response { Ok = true, Log = _engine.GetLog(r.Token) };

                    default:
                        return Fail(ErrorCodes.BadRequest, "Yêu cầu không được hỗ trợ.");
                }
            }
            catch (ReclaimException ex)
            {
                return Fail(ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error("Handle(" + (r == null ? "?" : r.Op) + "): " + ex);
                return Fail(ErrorCodes.Internal, "Lỗi nội bộ. Xem nhật ký dịch vụ.");
            }
        }

        private static Response Ok(StateDto state, string token)
        {
            return new Response { Ok = true, State = state, Token = token };
        }

        private static Response Fail(string code, string message)
        {
            return new Response { Ok = false, Code = code, Message = message };
        }
    }
}
