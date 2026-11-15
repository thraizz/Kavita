namespace Kavita.Models.DTOs.Statistics;

public sealed record ProfileStatBarDto
{
    public int BooksRead { get; set; }
    public int ComicsRead { get; set; }
    public int PagesRead { get; set; }
    public int WordsRead { get; set; }
    /// <summary>
    /// Words read as printed pages, 0 if <see cref="Entities.Enums.UserPreferences.AppUserSocialPreferences.WordsPerPrintedPage"/> is not configured
    /// </summary>
    public int PrintedPagesRead { get; set; }
    public int AuthorsRead { get; set; }
    public int Reviews { get; set; }
    public int Ratings { get; set; }
}
