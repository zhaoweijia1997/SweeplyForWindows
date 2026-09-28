using System.Globalization;
using Sweeply.Core;

namespace Sweeply.Core.Tests;

public class SizeFormatterTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10 * 1024 * 1024, "10.0 MB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    [InlineData(3L * 1024 * 1024 * 1024 * 1024, "3.00 TB")]
    public void Formats_like_explorer(long bytes, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(bytes, Inv));
    }

    [Fact]
    public void Uses_the_culture_decimal_separator()
    {
        Assert.Equal("1,50 KB", SizeFormatter.Format(1536, new CultureInfo("es-ES")));
    }

    [Fact]
    public void Rejects_negative_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SizeFormatter.Format(-1, Inv));
    }
}
