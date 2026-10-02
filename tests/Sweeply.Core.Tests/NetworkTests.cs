using System.Globalization;
using System.Text;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Tests;

public class NetworkTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(24, "255.255.255.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(22, "255.255.252.0")]
    [InlineData(32, "255.255.255.255")]
    [InlineData(0, "0.0.0.0")]
    public void Subnet_masks(int prefix, string mask) => Assert.Equal(mask, NetworkAdapters.SubnetMask(prefix));

    [Theory]
    [InlineData(100_000_000, "100 Mbps")]
    [InlineData(1_201_000_000, "1.2 Gbps")]
    [InlineData(2_500_000_000, "2.5 Gbps")]
    [InlineData(5_500_000, "5.5 Mbps")]
    [InlineData(0, "0 Mbps")]
    public void Bit_rates(double bps, string text) => Assert.Equal(text, NetworkAdapters.BitRate(bps, Inv));

    [Theory]
    [InlineData(5745, 149, "5 GHz")]
    [InlineData(2437, 6, "2.4 GHz")]
    [InlineData(5975, 5, "6 GHz")]        // 6 GHz channel numbers overlap 2.4 GHz ones: the frequency decides
    [InlineData(null, 11, "2.4 GHz")]
    [InlineData(null, 36, "5 GHz")]
    [InlineData(null, null, "")]
    public void Wifi_band(int? mhz, int? channel, string band) => Assert.Equal(band, NetworkAdapters.Band(mhz, channel));

    [Fact]
    public void Wifi_standards_and_mac_addresses()
    {
        Assert.Equal("Wi-Fi 6 (802.11ax)", NetworkAdapters.Standard(10));
        Assert.Equal("Wi-Fi 5 (802.11ac)", NetworkAdapters.Standard(8));
        Assert.Equal("", NetworkAdapters.Standard(0));
        Assert.Equal("02-00-5E-00-53-01", NetworkAdapters.FormatMac(new byte[] { 2, 0, 0x5E, 0, 0x53, 1 }));
        Assert.Equal("", NetworkAdapters.FormatMac(Array.Empty<byte>()));
    }

    [Fact]
    public void Ssids_are_utf8_or_shown_as_hex()
    {
        Assert.Equal("咖啡馆 Wi-Fi", NetworkAdapters.DecodeSsid(Encoding.UTF8.GetBytes("咖啡馆 Wi-Fi")));
        Assert.Equal("FFFE41", NetworkAdapters.DecodeSsid(new byte[] { 0xFF, 0xFE, 0x41 }));
    }

    [Fact]
    public void Adapters_read_on_this_machine()
    {
        var adapters = NetworkAdapters.Read();
        Assert.NotEmpty(adapters);
        Assert.DoesNotContain(adapters, a => a.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase));
        // Real connected cards come first.
        var firstVirtual = adapters.ToList().FindIndex(a => !a.IsHardware);
        if (firstVirtual >= 0) Assert.DoesNotContain(adapters.Skip(firstVirtual), a => a.IsHardware && a.IsUp);
        foreach (var wifi in adapters.Where(a => a.Wifi is not null)) Assert.InRange(wifi.Wifi!.SignalPercent, 0, 100);
    }
}

public class NetworkCountingTests
{
    [Fact]
    public void Each_byte_counts_once()
    {
        var adapters = new (string Item, int Index, bool Hardware)[]
        {
            ("WLAN", 29, true),
            ("WLAN-WFP Native MAC Layer LightWeight Filter-0000", 0, false),
            ("WLAN-QoS Packet Scheduler-0000", 0, false),
            ("Meta Tunnel", 4, false),
            ("VMware VMnet8", 37, false),
        };
        Assert.Equal(new[] { "WLAN" }, SystemSampler.Counted(adapters));
    }

    [Fact]
    public void Without_a_real_card_the_adapters_with_an_ip_stack_count()
    {
        var adapters = new (string Item, int Index, bool Hardware)[]
        {
            ("vEthernet (External)", 12, false),
            ("Intel Ethernet-QoS Packet Scheduler-0000", 0, false),
        };
        Assert.Equal(new[] { "vEthernet (External)" }, SystemSampler.Counted(adapters));
        Assert.Empty(SystemSampler.Counted(Array.Empty<(string, int, bool)>()));
    }
}
