using System.Globalization;
using Sweeply.Core.Monitoring;

namespace Sweeply.Core.Tests;

public class PerformanceTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string CardA = "luid_0x00000000_0x00011EB5_phys_0";
    private const string CardB = "luid_0x00000000_0x0001234A_phys_0";

    [Fact]
    public void Gpu_use_is_the_busiest_engine_summed_over_processes()
    {
        var usage = GpuEngines.Busiest(new[]
        {
            ($"pid_1_{CardA}_eng_0_engtype_3D", 20.0),
            ($"pid_2_{CardA}_eng_0_engtype_3D", 15.0),         // same engine: 35 together
            ($"pid_2_{CardA}_eng_4_engtype_VideoDecode", 30.0),
            ($"pid_3_{CardB}_eng_0_engtype_3D", 5.0),
        })!.Value;
        Assert.Equal(35, usage.Percent, 3);
        Assert.Equal(0x00011EB5, usage.AdapterLuid);
    }

    [Fact]
    public void Idle_gpu_names_the_card_most_processes_use_and_bad_values_count_as_zero()
    {
        var usage = GpuEngines.Busiest(new[]
        {
            ($"pid_1_{CardB}_eng_0_engtype_3D", 0.0),
            ($"pid_1_{CardA}_eng_0_engtype_3D", double.NaN),
            ($"pid_2_{CardA}_eng_0_engtype_3D", -3.0),
            ($"pid_3_{CardA}_eng_1_engtype_Copy", 0.0),
            ("not a gpu counter", 50.0),
        })!.Value;
        Assert.Equal(0, usage.Percent);
        Assert.Equal(0x00011EB5, usage.AdapterLuid);
        Assert.Null(GpuEngines.Busiest(Array.Empty<(string, double)>()));
    }

    [Fact]
    public void Gpu_use_never_reads_over_100()
    {
        var usage = GpuEngines.Busiest(new[] { ($"pid_1_{CardA}_eng_0_engtype_3D", 70.0), ($"pid_2_{CardA}_eng_0_engtype_3D", 60.0) })!.Value;
        Assert.Equal(100, usage.Percent);
    }

    [Fact]
    public void Luid_parses_high_and_low_parts()
    {
        Assert.True(GpuEngines.TryParseLuid("0x00000001_0x00000002_phys_0", out long luid));
        Assert.Equal((1L << 32) | 2, luid);
        Assert.False(GpuEngines.TryParseLuid("0x0001_0x2", out _));
        Assert.False(GpuEngines.TryParseLuid("0xZZZZZZZZ_0x00000002", out _));
    }

    [Fact]
    public void History_keeps_one_point_before_the_window_and_places_points_by_time()
    {
        var start = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var history = new MetricHistory(TimeSpan.FromSeconds(10));
        for (double s = 0; s <= 21; s += 1.5) history.Add(start.AddSeconds(s), s); // the value is the time

        var now = start.AddSeconds(21);
        var points = history.Points(now);
        Assert.Equal(10.5, points.First().SecondsAgo, 3); // measured at 10.5 s: just before the window
        Assert.Equal(12, points[1].Value);
        Assert.Equal(0, points.Last().SecondsAgo, 3);
        Assert.Equal(21, history.Max(now));
        Assert.Equal(12, history.Min(now)); // the point before the window does not count
    }

    [Fact]
    public void History_starts_over_when_the_clock_goes_back_and_skips_bad_values()
    {
        var start = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var history = new MetricHistory(TimeSpan.FromMinutes(2));
        history.Add(start, 1);
        history.Add(start.AddSeconds(1), double.NaN);
        Assert.Equal(1, history.Count);
        history.Add(start.AddHours(-1), 2);
        Assert.Equal(1, history.Count);
        Assert.Equal(2, history.Points(start.AddHours(-1)).Single().Value);
        Assert.Null(new MetricHistory(TimeSpan.FromMinutes(2)).Min(start));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.7, 1)]
    [InlineData(1, 1)]
    [InlineData(1.2, 2)]
    [InlineData(3, 5)]
    [InlineData(7, 10)]
    [InlineData(48, 50)]
    [InlineData(120, 200)]
    public void Nice_scale_is_1_2_or_5_times_a_power_of_ten(double value, double expected)
    {
        Assert.Equal(expected, ChartScale.Nice(value), 6);
    }

    [Theory]
    [InlineData(0, 1024 * 1024, "1 MB/s")]
    [InlineData(3.4 * 1024 * 1024, 1024 * 1024, "5 MB/s")]
    [InlineData(120 * 1024, 128 * 1024, "200 KB/s")]
    [InlineData(900 * 1024, 0, "1 MB/s")]           // not "1000 KB/s"
    [InlineData(1.5 * 1024 * 1024 * 1024, 0, "2 GB/s")]
    public void Rate_scale_is_round_in_the_unit_shown(double bytesPerSecond, double minimum, string expected)
    {
        Assert.Equal(expected, RateFormatter.Scale(ChartScale.Rate(bytesPerSecond, minimum), Inv));
    }

    [Fact]
    public void Sampler_reads_memory_disk_reads_and_gpu_on_this_machine()
    {
        using var sampler = new SystemSampler();
        Thread.Sleep(200);
        var first = sampler.Sample(includeGpu: true);
        Assert.True(first.MemoryTotalBytes > 1L << 30, "less than 1 GB of memory reported");
        Assert.InRange(first.MemoryUsedBytes, 1, first.MemoryTotalBytes);
        Assert.True(first.DiskReadBytesPerSecond >= 0);
        Assert.Null(first.Gpu); // the first GPU reading only starts the counters
        Assert.True(first.CpuGigahertz is null or > 0.1 and < 10);

        Thread.Sleep(300);
        var second = sampler.Sample(includeGpu: true);
        if (second.Gpu is { } gpu) Assert.InRange(gpu.Percent, 0, 100);
        Assert.Null(sampler.Sample().Gpu); // not asked for
    }
}
