using System.Globalization;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Tests;

public class MonitoringTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(999, "999 B/s")]
    [InlineData(1000, "1.0 KB/s")]
    [InlineData(850 * 1024, "850 KB/s")]
    [InlineData(1.2 * 1024 * 1024, "1.2 MB/s")]
    [InlineData(12.4 * 1024 * 1024, "12 MB/s")]
    [InlineData(1000 * 1024 * 1024, "1.0 GB/s")]
    public void Formats_rates_with_at_most_three_digits(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, RateFormatter.Format(bytesPerSecond, Inv));
    }

    [Theory]
    [InlineData(0, "0K")]
    [InlineData(500, "0K")]
    [InlineData(85 * 1024, "85K")]
    [InlineData(99.6 * 1024, "0.1M")]
    [InlineData(0.4 * 1024 * 1024, "0.4M")]
    [InlineData(9.96 * 1024 * 1024, "10M")]
    [InlineData(123.0 * 1024 * 1024, "123M")]
    [InlineData(1.5 * 1024 * 1024 * 1024, "1.5G")]
    public void Compact_rates_fit_four_characters(double bytesPerSecond, string expected)
    {
        string text = RateFormatter.Compact(bytesPerSecond, Inv);
        Assert.Equal(expected, text);
        Assert.True(text.Length <= 4);
    }

    [Theory]
    [InlineData(-5, "0")]
    [InlineData(double.NaN, "0")]
    [InlineData(37.4, "37")]
    [InlineData(99.6, "100")]
    [InlineData(130, "100")]
    public void Percent_is_a_whole_number_from_0_to_100(double percent, string expected)
    {
        Assert.Equal(expected, RateFormatter.Percent(percent, Inv));
    }

    [Fact]
    public void Bad_readings_show_as_zero()
    {
        Assert.Equal("0 B/s", RateFormatter.Format(double.NaN, Inv));
        Assert.Equal("0K", RateFormatter.Compact(-1, Inv));
    }

    [Fact]
    public void Sampler_returns_sane_values_on_this_machine()
    {
        using var sampler = new SystemSampler();
        Assert.True(sampler.HasCpu, "CPU counter not available");
        Assert.True(sampler.HasDiskWrite, "disk counter not available");

        // Keep one core busy so a working CPU counter cannot read exactly 0.
        var busyUntil = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < busyUntil) { }
        var sample = sampler.Sample();
        Assert.True(sample.CpuPercent > 0, "CPU reads 0 while a core was busy");
        Assert.InRange(sample.CpuPercent, 0, 100);
        Assert.True(sample.DiskWriteBytesPerSecond >= 0);
        Assert.True(sample.DownloadBytesPerSecond >= 0);
        Assert.True(sample.UploadBytesPerSecond >= 0);
    }
}
