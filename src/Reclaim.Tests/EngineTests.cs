using System;
using System.Linq;
using Reclaim.Core.Application;
using Reclaim.Core.Contracts;
using Reclaim.Core.Domain;
using Xunit;

namespace Reclaim.Tests
{
    public class EngineEnableTests
    {
        [Fact]
        public void Enable_WithHealthyResolver_StartsRunnerRoutesDnsAndAppliesGuard()
        {
            var rig = new Rig();

            rig.EnableWith("abc123");

            Assert.Equal(new[] { "abc123" }, rig.Runner.Starts.ToArray());
            Assert.True(rig.Engine.GetState(null).Enforcing);
            Assert.True(rig.Store.Current.Enabled);
            Assert.Equal("abc123", rig.Store.Current.ProfileId);
            Assert.Contains("dns.route", rig.Events);
            Assert.Contains("guard.ensure", rig.Events);
        }

        [Fact]
        public void Enable_WithInvalidId_Throws_AndTouchesNothing()
        {
            var rig = new Rig();

            var ex = Assert.Throws<ReclaimException>(() => rig.Engine.SaveSettings(null, "xx", true));

            Assert.Equal(ErrorCodes.InvalidProfile, ex.Code);
            Assert.Empty(rig.Runner.Starts);
            Assert.Equal(0, rig.Dns.RouteCalls);
        }

        [Fact]
        public void Enable_WhenNextDnsNeverAnswers_Fails_AndNeverRoutesSystemDns()
        {
            var rig = new Rig(rcode: -1);

            var ex = Assert.Throws<ReclaimException>(() => rig.Engine.SaveSettings(null, "abc123", true));

            Assert.Equal(ErrorCodes.ApplyFailed, ex.Code);
            Assert.Equal(0, rig.Dns.RouteCalls);          // nhập sai ID không được làm mất mạng
            Assert.False(rig.Engine.GetState(null).Enforcing);
            Assert.False(rig.Store.Current.Enabled);
        }

        [Fact]
        public void Enable_WhenNoAdapterCanBeRouted_Fails()
        {
            var rig = new Rig();
            rig.Dns.Route = () =>
            {
                var r = new DnsRouteResult();
                r.Failures.Add("Ethernet");
                return r;
            };

            var ex = Assert.Throws<ReclaimException>(() => rig.Engine.SaveSettings(null, "abc123", true));

            Assert.Equal(ErrorCodes.ApplyFailed, ex.Code);
            Assert.Contains("Ethernet", ex.Message);
        }

        [Fact]
        public void Enable_WhenOnlySomeAdaptersFail_Succeeds_AndReportsTheFailure()
        {
            var rig = new Rig();
            rig.Dns.Route = () =>
            {
                var r = new DnsRouteResult { Compliant = 1 };
                r.Failures.Add("VPN");
                return r;
            };

            rig.EnableWith("abc123");

            StateDto state = rig.Engine.GetState(null);
            Assert.True(state.Enforcing);
            Assert.Contains("VPN", state.Warning);          // cảnh báo, không phải lỗi: bảo vệ vẫn chạy
            Assert.True(string.IsNullOrEmpty(state.LastError));
        }

        [Fact]
        public void ChangeId_ThatFailsVerification_LeavesRunningNextDnsUntouched()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Verifier.Result = "ID không dùng được";

            var ex = Assert.Throws<ReclaimException>(() => rig.Engine.SaveSettings(null, "zzz999", true));

            Assert.Equal("ID không dùng được", ex.Message);
            Assert.Equal(new[] { "abc123" }, rig.Runner.Starts.ToArray()); // không khởi động lại
            Assert.Equal(0, rig.Runner.Stops);
            Assert.True(rig.Runner.IsRunning);
            Assert.True(rig.Engine.GetState(null).Enforcing);
            Assert.Equal("abc123", rig.Store.Current.ProfileId);
        }

        [Fact]
        public void ChangeId_ThatPassesVerification_RestartsWithNewId()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");

            rig.Engine.SaveSettings(null, "zzz999", true);

            Assert.Equal(new[] { "abc123", "zzz999" }, rig.Runner.Starts.ToArray());
            Assert.Equal("zzz999", rig.Store.Current.ProfileId);
            Assert.Equal(new[] { "zzz999" }, rig.Verifier.Verified.ToArray());
        }

        [Fact]
        public void Save_WithSameId_WhileRunning_DoesNotRestartNextDns()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");

            rig.Engine.SaveSettings(null, "abc123", true);

            Assert.Equal(1, rig.Runner.Starts.Count);
            Assert.Empty(rig.Verifier.Verified);
        }
    }

    public class EngineDisableTests
    {
        [Fact]
        public void Disable_RemovesGuardBeforeResettingDns()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Events.Clear();

            rig.Engine.SaveSettings(null, "abc123", false);

            int guard = rig.Events.IndexOf("guard.remove");
            int reset = rig.Events.IndexOf("dns.reset");
            Assert.True(guard >= 0 && reset >= 0);
            Assert.True(guard < reset, "quy tắc chặn DNS phải được gỡ trước khi trả DNS");
            Assert.False(rig.Engine.GetState(null).Enforcing);
            Assert.False(rig.Store.Current.Enabled);
        }

        [Fact]
        public void Disable_WhenGuardCannotBeRemoved_KeepsProtection_AndNeverResetsDns()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Guard.RemoveResult = false;
            rig.Events.Clear();

            var ex = Assert.Throws<ReclaimException>(() => rig.Engine.SaveSettings(null, "abc123", false));

            Assert.Equal(ErrorCodes.ApplyFailed, ex.Code);
            // Trả DNS khi quy tắc chặn DNS ngoài còn đó = máy mất DNS: tuyệt đối không được làm.
            Assert.DoesNotContain("dns.reset", rig.Events);
            Assert.Equal(0, rig.Runner.Stops);
            Assert.True(rig.Engine.GetState(null).Enforcing);
            Assert.True(rig.Store.Current.Enabled);
            Assert.Contains("guard.ensure", rig.Events); // chặn đường vòng được áp lại
        }

        [Fact]
        public void Disable_WhenDnsCannotBeRestored_KeepsProtectionAndGuard()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Dns.ResetResult = false;
            rig.Events.Clear();

            var ex = Assert.Throws<ReclaimException>(() => rig.Engine.SaveSettings(null, "abc123", false));

            Assert.Equal(ErrorCodes.ApplyFailed, ex.Code);
            Assert.True(rig.Engine.GetState(null).Enforcing);
            Assert.True(rig.Runner.IsRunning);
            Assert.Equal(0, rig.Runner.Stops);
            Assert.True(rig.Store.Current.Enabled);
            // chặn đường vòng vừa gỡ phải được áp lại
            Assert.True(rig.Events.LastIndexOf("guard.ensure") > rig.Events.IndexOf("guard.remove"));
        }
    }

    public class EngineStartTests
    {
        [Fact]
        public void Start_WhenEnabled_StartsRunnerRoutesDnsAndAppliesGuard()
        {
            var rig = new Rig(configure: s => { s.Enabled = true; s.ProfileId = "abc123"; });

            rig.Engine.Start();
            Assert.True(rig.Engine.WaitForStartup(3000));

            Assert.Equal(new[] { "abc123" }, rig.Runner.Starts.ToArray());
            Assert.True(rig.Engine.GetState(null).Enforcing);
            Assert.Contains("guard.ensure", rig.Events);
        }

        [Fact]
        public void Start_DoesNotMoveDnsUntilNextDnsAnswers()
        {
            var rig = new Rig(rcode: -1, configure: s => { s.Enabled = true; s.ProfileId = "abc123"; });

            rig.Engine.Start();
            System.Threading.Thread.Sleep(100);

            // nextdns chưa trả lời: DNS của máy chưa bị đụng tới (tránh mất DNS vài giây lúc khởi động lại dịch vụ)
            Assert.Equal(0, rig.Dns.RouteCalls);
            Assert.False(rig.Engine.GetState(null).Enforcing);

            rig.Probe.Rcode = 3;
            Assert.True(rig.Engine.WaitForStartup(3000));
            Assert.True(rig.Dns.RouteCalls > 0);
            Assert.True(rig.Engine.GetState(null).Enforcing);
            Assert.Contains("guard.ensure", rig.Events);
        }

        [Fact]
        public void Start_MovesDnsAnywayAfterTimeout_FailClosed()
        {
            var rig = new Rig(rcode: -1, configure: s => { s.Enabled = true; s.ProfileId = "abc123"; });

            rig.Engine.Start();
            Assert.True(rig.Engine.WaitForStartup(3000));

            Assert.True(rig.Dns.RouteCalls > 0);
            Assert.True(rig.Engine.GetState(null).Enforcing);
        }

        [Fact]
        public void Start_PendingStartupIsCancelledWhenUserDisables()
        {
            var rig = new Rig(rcode: -1, configure: s => { s.Enabled = true; s.ProfileId = "abc123"; });
            rig.Engine.Start();

            rig.Engine.SaveSettings(null, "abc123", false); // người dùng tắt trong lúc nextdns còn đang khởi động
            Assert.True(rig.Engine.WaitForStartup(3000));

            Assert.False(rig.Engine.GetState(null).Enforcing); // luồng khởi động không được bật lại bảo vệ
            Assert.False(rig.Store.Current.Enabled);
        }

        [Fact]
        public void Start_WhenDisabled_RemovesLeftoverGuardBeforeRecoveringDns()
        {
            var rig = new Rig();

            rig.Engine.Start();

            Assert.Empty(rig.Runner.Starts);
            int guard = rig.Events.IndexOf("guard.remove");
            int recover = rig.Events.IndexOf("dns.recover");
            Assert.True(guard >= 0 && recover >= 0 && guard < recover);
        }

        [Fact]
        public void Start_WhenBinaryHashChanged_DoesNotStartNextDns()
        {
            var rig = new Rig(configure: s =>
            {
                s.Enabled = true;
                s.ProfileId = "abc123";
                s.NextDnsSha256 = "hash-old";
            });
            rig.Runner.Hash = "hash-new";

            rig.Engine.Start();

            Assert.Empty(rig.Runner.Starts);
            Assert.False(string.IsNullOrEmpty(rig.Engine.GetState(null).LastError));
        }

        [Fact]
        public void Tick_WhileEnforcing_ReroutesDns()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            int before = rig.Dns.RouteCalls;

            rig.Engine.Tick();

            Assert.True(rig.Dns.RouteCalls > before);
        }
    }

    public class EngineWatchdogTests
    {
        private static void TickTimes(Rig rig, int n)
        {
            for (int i = 0; i < n; i++) rig.Engine.Tick();
        }

        [Fact]
        public void NextDnsThatStopsAnsweringIsRestartedOnce()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Probe.Rcode = -1;

            TickTimes(rig, 6);

            // 3 lần không trả lời liên tiếp -> khởi động lại; sau đó chờ thời gian khởi động nguội nên không khởi động dồn dập
            Assert.Equal(2, rig.Runner.Starts.Count);
        }

        [Fact]
        public void NextDnsThatAnswersWithServfailIsNotRestarted_ButIssueIsReported()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Probe.Rcode = 2; // SERVFAIL: nextdns sống nhưng NextDNS không với tới được

            TickTimes(rig, 8);

            Assert.Equal(1, rig.Runner.Starts.Count);
            Assert.Contains("NextDNS", rig.Engine.GetState(null).LastError);
        }

        [Fact]
        public void NoRestartWhileTheMachineHasNoNetwork()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Probe.Rcode = -1;
            rig.Probe.NetworkAvailable = false;

            TickTimes(rig, 8);

            Assert.Equal(1, rig.Runner.Starts.Count);
            Assert.Contains("mạng", rig.Engine.GetState(null).LastError);
        }

        [Fact]
        public void NoResponseWhileOffline_DoesNotCountTowardsRestart()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Probe.Rcode = -1;
            rig.Probe.NetworkAvailable = false;
            TickTimes(rig, 6);                       // rớt mạng dài: không được dồn thành "3 lần không trả lời"

            rig.Probe.NetworkAvailable = true;
            TickTimes(rig, 2);
            Assert.Equal(1, rig.Runner.Starts.Count); // mới 2 lần sau khi có mạng lại

            TickTimes(rig, 1);
            Assert.Equal(2, rig.Runner.Starts.Count); // lần thứ 3 sau khi có mạng: khởi động lại
        }

        [Fact]
        public void IssueClearsByItselfOnceNextDnsRecovers()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Probe.Rcode = 2;
            TickTimes(rig, 3);
            Assert.False(string.IsNullOrEmpty(rig.Engine.GetState(null).LastError));

            rig.Probe.Rcode = 3;
            TickTimes(rig, 2);

            Assert.True(string.IsNullOrEmpty(rig.Engine.GetState(null).LastError));
        }

        [Fact]
        public void WhenRunnerIsDown_ReportsTheNextDnsErrorLine()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            rig.Runner.Running = false;
            rig.Runner.ProblemLine = "nextdns.error(3): listen udp 127.0.0.1:53: bind: Only one usage of each socket address";

            TickTimes(rig, 2);

            string error = rig.Engine.GetState(null).LastError;
            Assert.Contains("nextdns không chạy được", error);
            Assert.Contains("cổng 53", error);
        }

        [Fact]
        public void GuardIsReappliedPeriodically_WhenRuleWasRemoved()
        {
            var rig = new Rig();
            rig.EnableWith("abc123");
            int before = rig.Events.Count(e => e == "guard.ensure");

            TickTimes(rig, 3);

            Assert.True(rig.Events.Count(e => e == "guard.ensure") > before);
        }

        [Fact]
        public void GuardProblemsAreSurfacedButDoNotStopProtection()
        {
            var rig = new Rig();
            rig.Guard.Problems = new[] { "tường lửa (lỗi)" };

            rig.EnableWith("abc123");

            StateDto state = rig.Engine.GetState(null);
            Assert.True(state.Enforcing);
            Assert.Contains("tường lửa", state.Warning);
            Assert.True(string.IsNullOrEmpty(state.LastError));
        }
    }

    public class EnginePasswordTests
    {
        [Fact]
        public void SetInitialPassword_RejectsWeak_AcceptsValid_RejectsSecond()
        {
            var rig = new Rig();

            var weak = Assert.Throws<ReclaimException>(() => rig.Engine.SetInitialPassword("short"));
            Assert.Equal(ErrorCodes.WeakPassword, weak.Code);

            string token = rig.Engine.SetInitialPassword("correct horse 1");
            Assert.False(string.IsNullOrEmpty(token));
            Assert.True(rig.Engine.GetState(token).Unlocked);

            var second = Assert.Throws<ReclaimException>(() => rig.Engine.SetInitialPassword("another pass 2"));
            Assert.Equal(ErrorCodes.PasswordExists, second.Code);
        }

        [Fact]
        public void Unlock_IsThrottledAfterFiveWrongAttempts()
        {
            var rig = new Rig();
            rig.Engine.SetInitialPassword("correct horse 1");

            for (int i = 0; i < 5; i++)
            {
                var wrong = Assert.Throws<ReclaimException>(() => rig.Engine.Unlock("wrong password"));
                Assert.Equal(ErrorCodes.BadPassword, wrong.Code);
            }
            var throttled = Assert.Throws<ReclaimException>(() => rig.Engine.Unlock("correct horse 1"));

            Assert.Equal(ErrorCodes.Throttled, throttled.Code);
        }

        [Fact]
        public void SaveSettings_WithSettingsProtection_RequiresUnlock()
        {
            var rig = new Rig();
            string token = rig.Engine.SetInitialPassword("correct horse 1");
            rig.Engine.SetProtection(token, new ProtectionOptions { Settings = true });

            var ex = Assert.Throws<ReclaimException>(() => rig.Engine.SaveSettings(null, "abc123", true));
            Assert.Equal(ErrorCodes.NeedUnlock, ex.Code);

            rig.Engine.SaveSettings(token, "abc123", true); // có token thì được
            Assert.True(rig.Engine.GetState(token).Enforcing);
        }
    }

    public class EngineLogTests
    {
        [Fact]
        public void GetLog_ContainsServiceLogTailAndNextDnsLog()
        {
            var rig = new Rig();
            rig.Engine.LogTail = n => new System.Collections.Generic.List<string> { "dòng-dịch-vụ" };

            string[] lines = rig.Engine.GetLog(null);

            Assert.Contains("dòng-dịch-vụ", lines);
            Assert.Contains(lines, l => l.Contains("nextdns"));
        }
    }
}
