using Flurl.Http.Testing;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests;

public class HardcoverSyncServiceTests(ITestOutputHelper helper) : AbstractDbTest(helper)
{
    private const string Api = "https://api.hardcover.app/v1/graphql";

    private async Task<(HardcoverSyncService, Kavita.Database.DataContext, Chapter)> Setup(int hardcoverId = 0)
    {
        var (unitOfWork, context, _) = await CreateDatabase();
        var lib = await context.Library.FirstAsync();
        var chapter = new ChapterBuilder("1").WithPages(47)
            .WithPerson(new PersonBuilder("Matt Dinniman").Build(), PersonRole.Writer).Build();
        chapter.TitleName = "The Dungeon Anarchist's Cookbook";
        chapter.HardcoverId = hardcoverId;
        context.Series.Add(new SeriesBuilder("Dungeon Crawler Carl").WithLibraryId(lib.Id)
            .WithVolume(new VolumeBuilder("1").WithChapter(chapter).Build()).Build());
        await context.SaveChangesAsync();

        return (new HardcoverSyncService(unitOfWork, Substitute.For<ILogger<HardcoverSyncService>>()), context, chapter);
    }

    [Theory]
    [InlineData(0.5f, 400, 200)]
    [InlineData(0.0001f, 400, 1)]
    [InlineData(1.2f, 400, 400)]
    [InlineData(0f, 400, 0)]
    [InlineData(0.5f, 0, 0)]
    public void ToProgressPages_ScalesToEditionPages(float percentage, int pages, int expected)
    {
        Assert.Equal(expected, HardcoverSyncService.ToProgressPages(percentage, pages));
    }

    [Fact]
    public async Task SyncProgress_UntrackedBook_LinksBookMarksReadingAndSetsProgress()
    {
        var (service, context, chapter) = await Setup();
        using var http = new HttpTest();
        http.RespondWithJson(new { data = new { search = new { ids = new[] { "11", "22" } } } })
            .RespondWithJson(new { data = new { books = new object[]
            {
                new { id = 11, title = "Dungeon Crawler Carl Omnibus" },
                new { id = 22, title = "The Dungeon Anarchist’s Cookbook" },
            } } })
            .RespondWithJson(new { data = new
            {
                books = new[] { new { editions = new[] { new { id = 5, pages = 600 } } } },
                user_books = Array.Empty<object>(),
            } })
            .RespondWithJson(new { data = new { insert_user_book = new
            {
                error = (string?) null,
                user_book = new { user_book_reads = new[] { new { id = 99, progress_pages = (int?) null, edition = new { pages = 600 } } } },
            } } })
            .RespondWithJson(new { data = new { update_user_book_read = new { error = (string?) null } } });

        await service.SyncProgress(chapter.Id, 0.72f, "token-untracked");

        Assert.Equal(22, (await context.Chapter.SingleAsync()).HardcoverId);
        Assert.Contains(http.CallLog, c => c.RequestBody.Contains("insert_user_book")
            && c.RequestBody.Contains("{\"object\":{\"book_id\":22,\"status_id\":2,\"edition_id\":5}}"));
        Assert.Contains(http.CallLog, c => c.RequestBody.Contains("update_user_book_read")
            && c.RequestBody.Contains("{\"id\":99,\"pages\":432}"));
        http.ShouldHaveCalled(Api).WithHeader("authorization", "Bearer token-untracked");
    }

    [Fact]
    public async Task SyncProgress_BookAlreadyRead_ChangesNothing()
    {
        var (service, _, chapter) = await Setup(hardcoverId: 22);
        using var http = new HttpTest();
        http.RespondWithJson(new { data = new
        {
            books = new[] { new { editions = new[] { new { id = 5, pages = 600 } } } },
            user_books = new[] { new { id = 3, user_id = 7, status_id = 3, user_book_reads = Array.Empty<object>() } },
        } });

        await service.SyncProgress(chapter.Id, 0.1f, "token-read");

        http.ShouldHaveMadeACall().Times(1);
        Assert.DoesNotContain(http.CallLog, c => c.RequestBody.Contains("mutation"));
    }

    [Fact]
    public async Task SyncProgress_TrackedBook_UpdatesOpenRead()
    {
        var (service, _, chapter) = await Setup(hardcoverId: 22);
        using var http = new HttpTest();
        http.RespondWithJson(new { data = new
            {
                books = new[] { new { editions = new[] { new { id = 5, pages = 600 } } } },
                user_books = new[] { new { id = 3, user_id = 7, status_id = 2, user_book_reads = new[]
                    { new { id = 44, progress_pages = 100, edition = new { pages = 500 } } } } },
            } })
            .RespondWithJson(new { data = new { update_user_book_read = new { error = (string?) null } } });

        await service.SyncProgress(chapter.Id, 0.5f, "token-tracked");

        http.ShouldHaveMadeACall().Times(2);
        Assert.Contains(http.CallLog, c => c.RequestBody.Contains("update_user_book_read")
            && c.RequestBody.Contains("{\"id\":44,\"pages\":250}"));
    }

    [Fact]
    public async Task SyncProgress_NoTitleMatch_DoesNotLinkOrTrack()
    {
        var (service, context, chapter) = await Setup();
        using var http = new HttpTest();
        http.RespondWithJson(new { data = new { search = new { ids = new[] { 11 } } } })
            .RespondWithJson(new { data = new { books = new[] { new { id = 11, title = "Something Else" } } } });

        await service.SyncProgress(chapter.Id, 0.5f, "token-nomatch");

        Assert.Equal(0, (await context.Chapter.SingleAsync()).HardcoverId);
        http.ShouldHaveMadeACall().Times(2);
    }
}
