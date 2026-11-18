using System;
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
    public void EstimateSyncReadingTime_NeverBeforePreviousDay()
    {
        var end = new DateTime(2026, 11, 17, 0, 32, 0);
        var duration = ReadingSessionService.EstimateSyncReadingTime(1_000_000, end.AddHours(-1), end, null);
        Assert.Equal(TimeSpan.FromMinutes(24 * 60 + 32), duration);
    }

    private static (AppUserReadingSession, AppUserReadingSessionActivityData) SyncActivity(DateTime end, int words, int startPage, int pages)
    {
        var activity = new AppUserReadingSessionActivityData
        {
            ChapterId = 8, StartPage = startPage, EndPage = startPage + pages, PagesRead = pages, WordsRead = words,
            StartTime = end, StartTimeUtc = end.AddHours(-1), EndTime = end, EndTimeUtc = end.AddHours(-1),
            StartBookScrollId = "//body/p[1]", DeviceIds = [1],
        };
        var session = new AppUserReadingSession
        {
            AppUserId = 1, IsActive = true, StartTime = end, StartTimeUtc = end.AddHours(-1), ActivityData = [activity],
        };
        return (session, activity);
    }

    [Fact]
    public async Task ApplySyncReadingTime_AcrossMidnight_SplitsIntoPreviousDaySession()
    {
        var (context, _, _) = await Setup();
        // 12,488 words at 223 wpm = 56 min, ending 00:32 → 24 min before midnight, 32 min after
        var end = new DateTime(2026, 11, 17, 0, 32, 0);
        var (session, activity) = SyncActivity(end, 12488, 38, 8);

        ReadingSessionService.ApplySyncReadingTime(context, session, activity, null);

        Assert.Equal(new DateTime(2026, 11, 17), activity.StartTime);
        Assert.Equal(new DateTime(2026, 11, 17), session.StartTime);

        var previousSession = context.AppUserReadingSession.Local.Single();
        var previous = previousSession.ActivityData.Single();
        Assert.False(previousSession.IsActive);
        Assert.Equal(new DateTime(2026, 11, 16, 23, 36, 0), previous.StartTime);
        Assert.Equal(new DateTime(2026, 11, 17).AddTicks(-1), previous.EndTime);
        Assert.Equal(previous.StartTime, previousSession.StartTime);
        Assert.Equal(previous.EndTime, previousSession.EndTime);

        // 24/56 of the reading happened before midnight
        Assert.Equal(5352, previous.WordsRead);
        Assert.Equal(12488 - 5352, activity.WordsRead);
        Assert.Equal(3, previous.PagesRead);
        Assert.Equal(38, previous.StartPage);
        Assert.Equal(41, previous.EndPage);
        Assert.Equal(41, activity.StartPage);
        Assert.Equal(5, activity.PagesRead);
        Assert.Equal("//body/p[1]", previous.StartBookScrollId);
        Assert.Equal([1], previous.DeviceIds);
    }

    [Fact]
    public async Task ApplySyncReadingTime_SameDay_NoSplit()
    {
        var (context, _, _) = await Setup();
        var end = new DateTime(2026, 11, 16, 22, 0, 0);
        var (session, activity) = SyncActivity(end, 2230, 10, 2);

        ReadingSessionService.ApplySyncReadingTime(context, session, activity, null);

        Assert.Empty(context.AppUserReadingSession.Local);
        Assert.Equal(end.AddMinutes(-10), activity.StartTime);
        Assert.Equal(end.AddMinutes(-10), session.StartTime);
        Assert.Equal(2230, activity.WordsRead);
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

    [Fact]
    public async Task UpdateProgress_TwoSyncUpdates_EachGetsOwnActivity()
    {
        var (context, service, dto) = await Setup();

        await service.UpdateProgress(1, dto, null, null, previousPage: 10);
        dto.PageNum = 30;
        await service.UpdateProgress(1, dto, null, null, previousPage: 25);

        var activities = await context.AppUserReadingSessionActivityData.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, activities.Count);
        Assert.Equal(15, activities[0].PagesRead);
        Assert.Equal(25, activities[1].StartPage);
        Assert.Equal(5, activities[1].PagesRead);
    }
}
