using System;
using System.Collections.Generic;
using Reclaim.Core.Application;
using Reclaim.Core.Domain;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Tests
{
    internal sealed class FakeStore : ISettingsStore
    {
        public AppSettings Current = AppSettings.CreateDefault();
        public AppSettings Load() { return Current.Clone(); }
        public void Save(AppSettings settings) { Current = settings.Clone(); }
    }

    internal sealed class FakeDns : IDnsConfigurator
    {
        private readonly List<string> _events;
        public bool ResetResult = true;
        public Func<DnsRouteResult> Route = () => new DnsRouteResult { Compliant = 1 };
        public int RouteCalls;

        public FakeDns(List<string> events) { _events = events; }

        public DnsRouteResult RouteToLoopback()
        {
            RouteCalls++;
            lock (_events) { _events.Add("dns.route"); }
            return Route();
        }

        public bool ResetAll()
        {
            lock (_events) { _events.Add("dns.reset"); }
            return ResetResult;
        }

        public bool RecoverOrphans(Func<bool> resolverAnswers)
        {
            lock (_events) { _events.Add("dns.recover"); }
            return true;
        }
    }

    internal sealed class FakeRunner : INextDnsRunner
    {
        private readonly List<string> _events;
        public readonly List<string> Starts = new List<string>();
        public int Stops;
        public string Hash = "hash-1";
        public string ProblemLine;
        public bool Running;

        public FakeRunner(List<string> events) { _events = events; }

        public bool IsRunning { get { return Running; } }
        public int RestartCount { get { return Starts.Count; } }
        public int ConsecutiveFailures { get { return 0; } }
        public string ComputeBinaryHash() { return Hash; }

        public void Start(string profileId)
        {
            Starts.Add(profileId);
            Running = true;
            lock (_events) { _events.Add("runner.start:" + profileId); }
        }

        public void Stop()
        {
            Stops++;
            Running = false;
            lock (_events) { _events.Add("runner.stop"); }
        }

        public IList<string> RecentLog() { return new List<string>(); }
        public string LastProblemLine() { return ProblemLine; }
        public void Dispose() { }
    }

    internal sealed class FakeProbe : IHealthProbe
    {
        public int Rcode;            // RCODE mà "nextdns" trả lời; -1 = không trả lời
        public bool NetworkAvailable = true;

        public FakeProbe(int rcode) { Rcode = rcode; }

        public bool IsResolverHealthy() { return Rcode == 0 || Rcode == 3; }
        public bool IsResolverAnswering() { return Rcode >= 0; }
        public int QueryRcode(int timeoutMs) { return Rcode; }
        public bool IsNetworkAvailable() { return NetworkAvailable; }
    }

    internal sealed class FakeHardener : IProtectionHardener
    {
        public void Apply(ProtectionOptions options) { }
    }

    internal sealed class FakeGuard : IBypassGuard
    {
        private readonly List<string> _events;
        public IList<string> Problems = new List<string>();
        public bool RemoveResult = true;

        public FakeGuard(List<string> events) { _events = events; }

        public IList<string> Ensure()
        {
            lock (_events) { _events.Add("guard.ensure"); }
            return Problems;
        }

        public bool Remove()
        {
            lock (_events) { _events.Add("guard.remove"); }
            return RemoveResult;
        }
    }

    internal sealed class FakeVerifier : IProfileVerifier
    {
        public string Result;   // null = ID dùng được
        public readonly List<string> Verified = new List<string>();

        public string Verify(string profileId, int timeoutMs)
        {
            Verified.Add(profileId);
            return Result;
        }
    }

    /// <summary>Bộ giả lập đủ để chạy ReclaimEngine mà không đụng tới Windows.</summary>
    internal sealed class Rig
    {
        public readonly List<string> Events = new List<string>();
        public readonly FakeStore Store = new FakeStore();
        public readonly FakeDns Dns;
        public readonly FakeRunner Runner;
        public readonly FakeProbe Probe;
        public readonly FakeGuard Guard;
        public readonly FakeVerifier Verifier = new FakeVerifier();
        public readonly ReclaimEngine Engine;

        public Rig(int rcode = 3, Action<AppSettings> configure = null)
        {
            if (configure != null) configure(Store.Current);
            Dns = new FakeDns(Events);
            Runner = new FakeRunner(Events);
            Probe = new FakeProbe(rcode);
            Guard = new FakeGuard(Events);
            Engine = new ReclaimEngine(Store, Dns, Runner, Probe, new FakeHardener(), Guard, Verifier, NullLog.Instance, FastOptions());
        }

        /// <summary>Mọi mốc thời gian về ~0 để test chạy nhanh và xác định.</summary>
        public static EngineOptions FastOptions()
        {
            return new EngineOptions
            {
                HealthTimeout = TimeSpan.FromMilliseconds(300),
                ProbeTimeoutMs = 10,
                PollSleepMs = 5,
                WatchdogInterval = TimeSpan.Zero,
                WatchdogRetryInterval = TimeSpan.Zero,
                WatchdogProbeTimeoutMs = 10,
                IssueAfter = TimeSpan.Zero,
                NoResponseChecksBeforeRestart = 3,
                UnhealthyRestartAfter = TimeSpan.Zero,
                MinRestartGap = TimeSpan.Zero,
                GuardCheckInterval = TimeSpan.Zero,
                VerifyTimeout = TimeSpan.FromMilliseconds(100)
            };
        }

        /// <summary>Bật bảo vệ với một ID hợp lệ và xác nhận thành công (điểm xuất phát của nhiều test).</summary>
        public void EnableWith(string id)
        {
            Engine.SaveSettings(null, id, true);
        }
    }
}
