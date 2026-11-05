using System.Collections.Generic;
using Reclaim.Core.Application;
using Reclaim.Core.Domain;
using Reclaim.Core.Infrastructure;
using Xunit;

namespace Reclaim.Tests
{
    public class NextDnsConfigTests
    {
        [Fact]
        public void Render_WritesOneListenLinePerAddress()
        {
            string text = NextDnsConfig.Render("abc123", new List<string> { "127.0.0.1:53", "[::1]:53" }, "ctl");

            Assert.Contains("profile abc123\n", text);
            Assert.Contains("listen 127.0.0.1:53\n", text);
            Assert.Contains("listen [::1]:53\n", text);
            Assert.Contains("control ctl\n", text);
            Assert.Contains("max-ttl 5m\n", text);
            Assert.Contains("mdns disabled\n", text);
        }

        [Fact]
        public void Render_RejectsInvalidProfileId()
        {
            var ex = Assert.Throws<ReclaimException>(() =>
                NextDnsConfig.Render("abc 12\nlisten 0.0.0.0:53", new List<string> { "127.0.0.1:53" }, "ctl"));
            Assert.Equal(ErrorCodes.InvalidProfile, ex.Code);
        }

        [Fact]
        public void DefaultListens_AlwaysContainsIpv4Loopback()
        {
            Assert.Contains("127.0.0.1:53", NextDnsConfig.DefaultListens());
        }
    }

    public class DnsRouteResultTests
    {
        [Fact]
        public void NoAdaptersAtAll_IsUsable()
        {
            Assert.True(new DnsRouteResult().Usable);
        }

        [Fact]
        public void OnlyFailures_IsNotUsable_ButOneGoodAdapterIs()
        {
            var bad = new DnsRouteResult();
            bad.Failures.Add("Ethernet");
            Assert.False(bad.Usable);
            Assert.False(bad.AllOk);

            var partial = new DnsRouteResult { Compliant = 1 };
            partial.Failures.Add("VPN");
            Assert.True(partial.Usable);
            Assert.False(partial.AllOk);
            Assert.Equal("VPN", partial.Describe());
        }
    }
}
