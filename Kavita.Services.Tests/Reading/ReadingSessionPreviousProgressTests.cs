using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Reading;
using Kavita.API.Services.SignalR;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.Progress;
using Kavita.Models.Entities.Progress;
using Kavita.Services.Builders;
using Kavita.Services.Reading;
using Kavita.Services.Tests.Cache;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Reading;

/// <summary>
/// Sync clients (KOReader) report progress after many pages. A new activity should start from the previously stored progress.
/// </summary>
public class ReadingSessionPreviousProgressTests(ITestOutputHelper helper): AbstractDbTest(helper)
{
    private async Task<(IDataContext, IReadingSessionService, ProgressDto)> Setup()
    {
        var (_, context, mapper) = await CreateDatabase();

        var user = new AppUserBuilder("Amelia", "amelia@localhot").Build();
        await context.AppUser.AddAsync(user);
        await context.SaveChangesAsync();

        var lib = await context.Library.FirstAsync();
        var series = new SeriesBuilder("Sync Client")
            .WithLibraryId(lib.Id)
            .WithVolume(new VolumeBuilder("1")
                .WithChapter(new ChapterBuilder("1").WithPages(50).Build())
                .Build())
            .Build();
        context.Series.Add(series);
        await context.SaveChangesAsync();

        var chapter = series.Volumes[0].Chapters[0];
        var cacheService = Substitute.For<ICacheService>();
        cacheService.Ensure(chapter.Id).Returns(chapter);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IDataContext)).Returns(context);
        serviceProvider.GetService(typeof(IEventHub)).Returns(Substitute.For<IEventHub>());
        serviceProvider.GetService(typeof(ICacheService)).Returns(cacheService);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var service = new ReadingSessionService(scopeFactory, Substitute.For<ILogger<ReadingSessionService>>(), new FakeHybridCache());

        var dto = new ProgressDto
        {
            ChapterId = chapter.Id, VolumeId = chapter.VolumeId, SeriesId = series.Id, LibraryId = lib.Id, PageNum = 25
        };
        return (context, service, dto);
    }

    private static async Task<AppUserReadingSessionActivityData> SingleActivity(IDataContext context)
        => await context.AppUserReadingSessionActivityData.SingleAsync();

    [Fact]
    public async Task UpdateProgress_WithPreviousPage_CountsPagesSincePreviousProgress()
    {
        var (context, service, dto) = await Setup();

        await service.UpdateProgress(1, dto, null, null, previousPage: 10);

        var activity = await SingleActivity(context);
        Assert.Equal(10, activity.StartPage);
        Assert.Equal(25, activity.EndPage);
        Assert.Equal(15, activity.PagesRead);
    }

    [Fact]
    public async Task UpdateProgress_WithoutPreviousPage_KeepsExistingBehaviour()
    {
        var (context, service, dto) = await Setup();

        await service.UpdateProgress(1, dto, null, null);

        var activity = await SingleActivity(context);
        Assert.Equal(24, activity.StartPage);
        Assert.Equal(1, activity.PagesRead);
    }

    [Fact]
    public async Task UpdateProgress_PreviousPageAhead_DoesNotCountBackwards()
    {
        var (context, service, dto) = await Setup();

        await service.UpdateProgress(1, dto, null, null, previousPage: 30);

        var activity = await SingleActivity(context);
        Assert.Equal(24, activity.StartPage);
        Assert.Equal(1, activity.PagesRead);
    }

    [Fact]
    public void EstimateSyncReadingTime_UsesReadingSpeed()
    {
        var end = new DateTime(2026, 11, 16, 22, 0, 0);
        var duration = ReadingSessionService.EstimateSyncReadingTime(2230, end, end, end.AddHours(-3));
        Assert.Equal(10, duration.TotalMinutes, 3);
    }

    [Fact]
    public void EstimateSyncReadingTime_CappedByTimeSincePreviousProgress()
    {
        var end = new DateTime(2026, 11, 16, 22, 0, 0);
        var duration = ReadingSessionService.EstimateSyncReadingTime(22300, end, end, end.AddMinutes(-20));
        Assert.Equal(TimeSpan.FromMinutes(20), duration);
    }

    [Fact]
    public void EstimateSyncReadingTime_NeverBeforeLocalMidnight()
    {
        var end = new DateTime(2026, 11, 17, 0, 32, 0);
        var duration = ReadingSessionService.EstimateSyncReadingTime(12474, end.AddHours(-1), end, null);
        Assert.Equal(TimeSpan.FromMinutes(32), duration);
    }

    [Fact]
    public void EstimateSyncReadingTime_NoWords_NoTime()
    {
        var end = new DateTime(2026, 11, 16, 22, 0, 0);
        Assert.Equal(TimeSpan.Zero, ReadingSessionService.EstimateSyncReadingTime(0, end, end, end.AddHours(-1)));
    }

    [Fact]
    public async Task UpdateProgress_WithoutWords_KeepsZeroDuration()
    {
        var (context, service, dto) = await Setup();

        await service.UpdateProgress(1, dto, null, null, previousPage: 10, previousProgressUtc: DateTime.UtcNow.AddHours(-1));

        var activity = await SingleActivity(context);
        Assert.Equal(activity.StartTimeUtc, activity.EndTimeUtc!.Value, TimeSpan.FromSeconds(5));
    }
}
