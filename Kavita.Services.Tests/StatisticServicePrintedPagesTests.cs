namespace Kavita.Services.Tests;

public class StatisticServicePrintedPagesTests
{
    [Theory]
    [InlineData(201667, 275, 733)]
    [InlineData(137, 275, 0)]
    [InlineData(138, 275, 1)]
    [InlineData(275, 275, 1)]
    [InlineData(201667, 0, 0)]
    [InlineData(201667, -5, 0)]
    [InlineData(0, 275, 0)]
    public void ToPrintedPages_ConvertsWordsByWordsPerPage(long wordsRead, int wordsPerPage, int expected)
    {
        Assert.Equal(expected, StatisticService.ToPrintedPages(wordsRead, wordsPerPage));
    }
}
