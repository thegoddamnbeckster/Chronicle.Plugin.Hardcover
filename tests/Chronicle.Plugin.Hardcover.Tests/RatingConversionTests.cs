namespace Chronicle.Plugin.Hardcover.Tests;

public class RatingConversionTests
{
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(3.0, 6)]
    [InlineData(3.5, 7)]
    [InlineData(4.5, 9)]
    [InlineData(5.0, 10)]
    [InlineData(7.0, 10)]   // never above the scale
    public void StarsAreDoubled(double stars, int expected) =>
        Assert.Equal(expected, HardcoverImportProvider.ToChronicleRating(stars));

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(-1.0)]
    public void NoRating_IsNotARatingOfOne(double? stars) =>
        Assert.Null(HardcoverImportProvider.ToChronicleRating(stars));
}
