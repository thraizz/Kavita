using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Reading;
using Kavita.API.Services.SignalR;
using Kavita.Common;
using Kavita.Database.Extensions;
using Kavita.Models.DTOs;
using Kavita.Models.DTOs.Progress;
using Kavita.Models.DTOs.Reader;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Entities.Progress;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Reading;

public sealed class ReadingSessionService : IReadingSessionService, IDisposable, IAsyncDisposable
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<ReadingSessionService> _logger;
    private readonly HybridCache _cache;
    private readonly TimeSpan _sessionTimeout;
    private readonly Timer _cleanupTimer;
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> UserLocks = new();
    private bool _disposed;

    private static readonly HybridCacheEntryOptions ChapterFormatCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(30),
        LocalCacheExpiration = TimeSpan.FromMinutes(30)
    };

    public ReadingSessionService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<ReadingSessionService> logger,
        HybridCache cache,
        TimeSpan? sessionTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _cache = cache;
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromMinutes(10);
        var pollInterval1 = pollInterval ?? TimeSpan.FromMinutes(5);

        _cleanupTimer = new Timer(
            callback: _ => _ = RunCleanupAsync(),
            state: null,
            dueTime: pollInterval1,
            period: pollInterval1
        );
    }

    public async Task UpdateProgress(int userId, ProgressDto progressDto, ClientInfoData? clientInfo, int? deviceId,
        int? previousPage = null, string? previousBookScrollId = null, DateTime? previousProgressUtc = null)
    {
        // We need to lock per-user as progress events can come fast and duplicate, as we are using new DataContext per Background Task
        var userLock = UserLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

        await userLock.WaitAsync();

        try
        {
            _logger.LogDebug("Updating Reading Session for {UserId} on {ChapterId}", userId, progressDto.ChapterId);

            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IDataContext>();
            var eventHub = scope.ServiceProvider.GetRequiredService<IEventHub>();

            var session = await GetOrCreateSessionAsync(userId, progressDto, context, previousPage, previousBookScrollId);

            var activity = await UpdateActivityDataAsync(session, progressDto, clientInfo, deviceId, scope, context, previousPage, previousBookScrollId);

            // Sync clients (KOReader) only report positions, so the activity would last 0 seconds. Estimate it.
            if (previousPage.HasValue && activity != null)
            {
                ApplySyncReadingTime(context, session, activity, previousProgressUtc);
            }

            session.LastModified = DateTime.Now;
            session.LastModifiedUtc = DateTime.UtcNow;

            await context.SaveChangesAsync();

            await eventHub.SendMessageAsync(MessageFactory.ReadingSessionUpdate,
                MessageFactory.ReadingSessionUpdateEvent(userId, session.Id));
        }
        finally
        {
            userLock.Release();
        }
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task GenerateReadingSessionForChapters(int userId, int seriesId, Dictionary<int, int> chaptersMap, CancellationToken ct = default)
    {
        using var scope = _serviceScopeFactory.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<IDataContext>();
        var readerService = scope.ServiceProvider.GetRequiredService<IReaderService>();
        var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();

        var series = await context.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series == null) throw new KavitaNotFoundException();

        var chapterIds = chaptersMap.Keys.ToList();

        _logger.LogDebug("Generating Reading Session for {UserId} on {SeriesId} for {ChapterCount} chapters",
            userId, seriesId, chapterIds.Count);

        var chapters = await context.Chapter
            .Where(cp => chapterIds.Contains(cp.Id) && cp.Volume.SeriesId == seriesId)
            .ApplyDefaultChapterOrdering()
            .ProjectToWithProgress<Chapter, ChapterDto>(mapper, userId)
            .ToListAsync(ct);

        // ApplyDefaultChapterOrdering orders from first to last, we want last to first
        chapters.Reverse();

        Dictionary<int, HourEstimateRangeDto> estimatedHoursByChapter = [];
        foreach (var chapterId in chapters.Select(cp => cp.Id))
        {
            var page = chaptersMap[chapterId];
            estimatedHoursByChapter[chapterId] = await readerService
                .GetEstimateFromPageForChapter(userId, seriesId, chapterId, page);
        }

        var chapterSchedule = ScheduleChapters(estimatedHoursByChapter, DateTime.Now);

        AppUserReadingSession? currentSession = null;
        DateTime? currentSessionDate = null;
        List<AppUserReadingSession> addedSessions = [];

        foreach (var chapter in chapters)
        {
            var estimate = estimatedHoursByChapter[chapter.Id];
            if (estimate is { PageCount: 0, WordCount: 0 })
            {
                _logger.LogTrace("Not generating reading session for chapter {ChapterId} as nothing would be read", chapter.Id);
                continue;
            }

            var schedule = chapterSchedule[chapter.Id];
            var chapterDate = schedule.Start.Date;

            _logger.LogTrace("Chapter {ChapterId} will be read from {Start} to {End} [{Minutes}m]",
                chapter.Id, schedule.Start, schedule.End, schedule.End.Subtract(schedule.Start).TotalMinutes);

            if (currentSession == null || chapterDate != currentSessionDate)
            {
                currentSession = new AppUserReadingSession
                {
                    AppUserId = userId,
                    IsActive = false,
                    IsGenerated = true,
                    ActivityData = []
                };

                addedSessions.Add(currentSession);
                context.AppUserReadingSession.Add(currentSession);

                currentSessionDate = chapterDate;
            }

            currentSession.ActivityData.Add(new AppUserReadingSessionActivityData
            {
                LibraryId = series.LibraryId,
                SeriesId = seriesId,
                VolumeId = chapter.VolumeId,
                ChapterId = chapter.Id,
                StartPage = chapter.PagesRead,
                EndPage = chapter.Pages,
                StartTime = schedule.Start,
                StartTimeUtc = schedule.Start.ToUniversalTime(),
                EndTime = schedule.End,
                EndTimeUtc = schedule.End.ToUniversalTime(),
                PagesRead = estimate.PageCount,
                WordsRead = (int) estimate.WordCount,
                TotalPages = chapter.Pages,
                TotalWords = chapter.WordCount,
                Format = series.Format,
            });
        }

        foreach (var s in addedSessions)
        {
            s.StartTime = s.ActivityData.Min(ad => ad.StartTime);
            s.StartTimeUtc = s.ActivityData.Min(ad => ad.StartTimeUtc);
            s.EndTime = s.ActivityData.Max(ad => ad.EndTime);
            s.EndTimeUtc = s.ActivityData.Max(ad => ad.EndTimeUtc);
        }

        _logger.LogTrace("Generated {Count} reading sessions for {UserId} on {SeriesId} for {ChapterCount} chapters",
            addedSessions.Count, userId, seriesId, chapterIds.Count);

        await context.SaveChangesAsync(ct);
    }

    public static Dictionary<int, (DateTime Start, DateTime End)> ScheduleChapters(
        Dictionary<int, HourEstimateRangeDto> estimatedHoursByChapter, DateTime currentEnd)
    {
        var schedule = new Dictionary<int, (DateTime Start, DateTime End)>();

        foreach (var (chapterId, estimateRangeDto) in estimatedHoursByChapter)
        {
            // Don't allow chapters to go over 24h.
            var actualEstimate = Math.Min(estimateRangeDto.AvgHours, 24);

            var duration = TimeSpan.FromHours(actualEstimate);
            var start = currentEnd - duration;

            // If start is on a different day, push the whole chapter to end at midnight of current End's day
            if (start.Date < currentEnd.Date)
            {
                var midnight = currentEnd.Date; // midnight = start of currentEnd's date
                start = midnight - duration;
                currentEnd = midnight;
            }

            schedule[chapterId] = (start, currentEnd);
            currentEnd = start;
        }

        return schedule;
    }


    private async Task<AppUserReadingSession> GetOrCreateSessionAsync(int userId, ProgressDto dto, IDataContext context,
        int? previousPage, string? previousBookScrollId)
    {
        var cutoffUtc = DateTime.UtcNow - _sessionTimeout;
        var midnightToday = DateTime.Today;

        var existingSession = await context.AppUserReadingSession
            .Where(s => s.IsActive && s.AppUserId == userId)
            .Where(s => s.LastModifiedUtc >= cutoffUtc && s.StartTime >= midnightToday)
            .Include(s => s.ActivityData)
            .FirstOrDefaultAsync();

        if (existingSession != null)
        {
            _logger.LogDebug("Found existing session {SessionId} for user {UserId} for Chapter {ChapterId}", existingSession.Id, userId, dto.ChapterId);
            return existingSession;
        }


        var chapterFormat = await GetChapterFormatAsync(dto.ChapterId, context);
        var newSession = new AppUserReadingSession
        {
            AppUserId = userId,
            StartTime = DateTime.Now,
            StartTimeUtc = DateTime.UtcNow,
            LastModified = DateTime.Now,
            LastModifiedUtc = DateTime.UtcNow,
            IsActive = true,
            ActivityData = [NewActivityData(dto, chapterFormat, previousPage, previousBookScrollId)]
        };

        context.AppUserReadingSession.Add(newSession);
        await context.SaveChangesAsync();

        _logger.LogDebug("Created new session {SessionId} for user {UserId} for Chapter {ChapterId}", newSession.Id, userId, dto.ChapterId);
        return newSession;
    }

    /// <summary>
    /// Reading speed used to estimate the time between two sync updates. Measured from KOReader statistics
    /// (sum of words / sum of page seconds over 11 Kindle syncs, 2026-11-11..17).
    /// </summary>
    public const double SyncWordsPerMinute = 223;

    /// <summary>
    /// How long reading <paramref name="wordsRead"/> words took, for a sync update at <paramref name="endTime"/>:
    /// words / <see cref="SyncWordsPerMinute"/>, at most the time since the previous progress update,
    /// and never before the previous day's local midnight.
    /// </summary>
    public static TimeSpan EstimateSyncReadingTime(int wordsRead, DateTime endTimeUtc, DateTime endTime, DateTime? previousProgressUtc)
    {
        if (wordsRead <= 0) return TimeSpan.Zero;

        var duration = TimeSpan.FromMinutes(wordsRead / SyncWordsPerMinute);
        if (previousProgressUtc.HasValue && previousProgressUtc.Value <= endTimeUtc)
        {
            var sincePrevious = endTimeUtc - previousProgressUtc.Value;
            if (sincePrevious < duration) duration = sincePrevious;
        }

        var sinceYesterday = endTime - endTime.Date.AddDays(-1);
        if (sinceYesterday < duration) duration = sinceYesterday;

        return duration;
    }

    internal static void ApplySyncReadingTime(IDataContext context, AppUserReadingSession session,
        AppUserReadingSessionActivityData activity, DateTime? previousProgressUtc)
    {
        if (activity.EndTime == null || activity.EndTimeUtc == null) return;

        var end = activity.EndTime.Value;
        var endUtc = activity.EndTimeUtc.Value;
        var duration = EstimateSyncReadingTime(activity.WordsRead, endUtc, end, previousProgressUtc);
        var start = end - duration;
        var startUtc = endUtc - duration;
        if (start >= activity.StartTime) return;

        // Reading that started before midnight belongs to the previous day. A session can't span midnight
        // (CloseSession ends it at its start day's end), so the part before midnight gets its own closed session.
        var midnight = end.Date;
        if (start < midnight)
        {
            var before = midnight - start;
            var midnightUtc = startUtc + before;
            var fraction = before.TotalSeconds / duration.TotalSeconds;
            var pagesBefore = (int) Math.Round(activity.PagesRead * fraction);
            var wordsBefore = (int) Math.Round(activity.WordsRead * fraction);

            var previousDay = new AppUserReadingSessionActivityData
            {
                Format = activity.Format,
                ChapterId = activity.ChapterId,
                VolumeId = activity.VolumeId,
                SeriesId = activity.SeriesId,
                LibraryId = activity.LibraryId,
                StartPage = activity.StartPage,
                EndPage = activity.StartPage + pagesBefore,
                StartBookScrollId = activity.StartBookScrollId,
                StartTime = start,
                StartTimeUtc = startUtc,
                EndTime = midnight.AddTicks(-1),
                EndTimeUtc = midnightUtc.AddTicks(-1),
                PagesRead = pagesBefore,
                WordsRead = wordsBefore,
                TotalPages = activity.TotalPages,
                TotalWords = activity.TotalWords,
                ClientInfo = CopyClientInfo(activity.ClientInfo),
                DeviceIds = [..activity.DeviceIds],
            };
            context.AppUserReadingSession.Add(new AppUserReadingSession
            {
                AppUserId = session.AppUserId,
                IsActive = false,
                StartTime = previousDay.StartTime,
                StartTimeUtc = previousDay.StartTimeUtc,
                EndTime = previousDay.EndTime,
                EndTimeUtc = previousDay.EndTimeUtc,
                LastModified = DateTime.Now,
                LastModifiedUtc = DateTime.UtcNow,
                ActivityData = [previousDay],
            });

            activity.StartPage += pagesBefore;
            activity.PagesRead -= pagesBefore;
            activity.WordsRead -= wordsBefore;
            start = midnight;
            startUtc = midnightUtc;
        }

        activity.StartTime = start;
        activity.StartTimeUtc = startUtc;

        if (activity.StartTime < session.StartTime)
        {
            session.StartTime = activity.StartTime;
            session.StartTimeUtc = activity.StartTimeUtc;
        }
    }

    private static ClientInfoData? CopyClientInfo(ClientInfoData? clientInfo) => clientInfo == null
        ? null
        : System.Text.Json.JsonSerializer.Deserialize<ClientInfoData>(System.Text.Json.JsonSerializer.Serialize(clientInfo));

    private async Task<AppUserReadingSessionActivityData?> UpdateActivityDataAsync(AppUserReadingSession session, ProgressDto progressDto, ClientInfoData? clientInfo,
        int? deviceId, IServiceScope scope, IDataContext context, int? previousPage, string? previousBookScrollId)
    {
        var cutoffUtc = DateTime.UtcNow - _sessionTimeout;

        // Sync clients: each update counts from the previous progress, so never extend an earlier activity
        // (it would recount from its own start, and its pre-midnight part may already be split off)
        var existingActivity = session.ActivityData
            .Where(d => d.EndTimeUtc == null || (!previousPage.HasValue && d.EndTimeUtc >= cutoffUtc)) // End time works as a LastModified
            .FirstOrDefault(d => d.ChapterId == progressDto.ChapterId);

        var chapterFormat = await GetChapterFormatAsync(progressDto.ChapterId, context);

        if (existingActivity != null)
        {
            _logger.LogDebug("Updating Session {SessionId} with an existing Activity {ActivityId}", session.Id,  existingActivity.Id);
            await UpdateExistingActivityAsync(existingActivity, progressDto, clientInfo, deviceId, chapterFormat, scope);
            return existingActivity;
        }
        else
        {
            _logger.LogDebug("Updating Session {SessionId} with a new Activity", session.Id);
            var newActivity = NewActivityData(progressDto, chapterFormat, previousPage, previousBookScrollId);
            if (clientInfo != null)
            {
                newActivity.ClientInfo = clientInfo;
            }
            if (deviceId.HasValue)
            {
                newActivity.DeviceIds.Add(deviceId.Value);
            }
            session.ActivityData.Add(newActivity);

            // Started from the previous progress: record what was read up to this update right away
            if (previousPage.HasValue && previousPage.Value <= progressDto.PageNum)
            {
                await UpdateExistingActivityAsync(newActivity, progressDto, clientInfo, deviceId, chapterFormat, scope);
            }

            return newActivity;
        }
    }

    private async Task UpdateExistingActivityAsync(AppUserReadingSessionActivityData activity, ProgressDto progressDto, ClientInfoData? clientInfo,
        int? deviceId, MangaFormat chapterFormat, IServiceScope scope)
    {
        activity.PagesRead = progressDto.PageNum - activity.StartPage;
        activity.EndPage = progressDto.PageNum;
        activity.EndTime = DateTime.Now;
        activity.EndTimeUtc = DateTime.UtcNow;

        if (deviceId.HasValue && !activity.DeviceIds.Contains(deviceId.Value))
        {
            activity.DeviceIds.Add(deviceId.Value);
        }

        if (clientInfo != null)
        {
            activity.ClientInfo = clientInfo;
        }

        var cacheService = scope.ServiceProvider.GetRequiredService<ICacheService>();
        var chapter = await cacheService.Ensure(progressDto.ChapterId);

        activity.TotalPages = chapter?.Pages ?? 0;
        activity.TotalWords = chapter?.WordCount ?? 0;

        if (chapterFormat == MangaFormat.Epub && chapter != null && !string.IsNullOrEmpty(progressDto.BookScrollId))
        {
            await UpdateEpubActivityAsync(activity, progressDto, chapter, cacheService, scope);
        }
    }

    private async Task UpdateEpubActivityAsync(AppUserReadingSessionActivityData activity, ProgressDto progressDto, Chapter chapter,
        ICacheService cacheService, IServiceScope scope)
    {
        var bookService = scope.ServiceProvider.GetRequiredService<IBookService>();
        var cachedFilePath = cacheService.GetCachedFile(chapter);

        if (string.IsNullOrEmpty(activity.StartBookScrollId))
        {
            activity.StartBookScrollId = progressDto.BookScrollId;
            activity.WordsRead = 0;
        }
        else
        {
            try
            {
                activity.WordsRead = await bookService.GetWordCountBetweenXPaths(
                    cachedFilePath,
                    activity.StartBookScrollId,
                    activity.StartPage,
                    progressDto.BookScrollId!,
                    progressDto.PageNum
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error calculating words read for activity on chapter {ChapterId}",
                    activity.ChapterId);
            }
        }

        activity.EndBookScrollId = progressDto.BookScrollId;
    }

    private async Task RunCleanupAsync()
    {
        if (!await _cleanupLock.WaitAsync(TimeSpan.Zero))
        {
            _logger.LogDebug("Cleanup already in progress, skipping");
            return;
        }

        try
        {
            await CleanupExpiredSessionsAsync();
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private async Task CleanupExpiredSessionsAsync()
    {
        try
        {
            var cutoffUtc = DateTime.UtcNow - _sessionTimeout;
            var midnightToday = DateTime.Today;

            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IDataContext>();
            var eventHub = scope.ServiceProvider.GetRequiredService<IEventHub>();

            var expiredSessions = await context.AppUserReadingSession
                .Where(s => s.IsActive)
                .Where(s => s.LastModifiedUtc < cutoffUtc || s.StartTime < midnightToday)
                .Include(s => s.ActivityData)
                .ToListAsync();

            if (expiredSessions.Count == 0) return;

            _logger.LogInformation("Closing {Count} expired reading sessions", expiredSessions.Count);

            // Collect all chapter IDs to remove from cache
            var chapterIdsToInvalidate = expiredSessions
                .SelectMany(s => s.ActivityData.Select(a => a.ChapterId))
                .Distinct()
                .ToList();

            var allCompletedChapterIds = new List<int>();

            foreach (var session in expiredSessions)
            {
                _logger.LogDebug("Closing session {SessionId} for user {UserId}", session.Id, session.AppUserId);
                var completedIds = CloseSession(session);
                allCompletedChapterIds.AddRange(completedIds);
            }

            await context.SaveChangesAsync();

            // Batch cache invalidation after all sync work
            foreach (var chapterId in chapterIdsToInvalidate)
            {
                await _cache.RemoveAsync(GetChapterFormatCacheKey(chapterId));
            }

            // Batch notify after save
            foreach (var session in expiredSessions)
            {
                await eventHub.SendMessageAsync(
                    MessageFactory.ReadingSessionClose,
                    MessageFactory.ReadingSessionCloseEvent(session.AppUserId, session.Id));
            }

            // Batch update total reads
            if (allCompletedChapterIds.Count > 0)
            {
                var distinctChapterIds = allCompletedChapterIds.Distinct().ToList();
                await context.AppUserProgresses
                    .Where(p => distinctChapterIds.Contains(p.ChapterId))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.TotalReads, x => x.TotalReads + 1));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session cleanup");
        }
    }

    private List<int> CloseSession(AppUserReadingSession session)
    {
        var lastActivity = session.ActivityData
            .Where(ad => ad.EndTime.HasValue)
            .MaxBy(ad => ad.EndTime);

        var endTime = lastActivity?.EndTime ?? session.LastModified;
        var endTimeUtc = lastActivity?.EndTimeUtc ?? session.LastModifiedUtc;

        if (session.StartTime.Date < DateTime.Today)
        {
            var endOfStartDay = session.StartTime.Date.AddDays(1).AddTicks(-1);
            endTime = endOfStartDay;
            endTimeUtc = TimeZoneInfo.ConvertTimeToUtc(endOfStartDay);
        }

        session.IsActive = false;
        session.EndTime = endTime;
        session.EndTimeUtc = endTimeUtc;
        session.LastModified = DateTime.Now;
        session.LastModifiedUtc = DateTime.UtcNow;

        var completedChapterIds = session.ActivityData
            .Where(d => d.TotalPages > 0 && d.EndPage >= d.TotalPages)
            .Select(d => d.ChapterId)
            .Distinct()
            .ToList();

        _logger.LogDebug(
            "Closed session {SessionId} for user {UserId}, {ActivityCount} activities, {CompletedCount} completed chapters",
            session.Id, session.AppUserId, session.ActivityData.Count, completedChapterIds.Count);

        return completedChapterIds;
    }

    private async Task<MangaFormat> GetChapterFormatAsync(int chapterId, IDataContext context)
    {
        var cacheKey = GetChapterFormatCacheKey(chapterId);

        return await _cache.GetOrCreateAsync(
            cacheKey,
            (chapterId, context),
            static async (state, cancel) =>
                await state.context.MangaFile
                    .Where(f => f.ChapterId == state.chapterId)
                    .Select(f => f.Format)
                    .FirstOrDefaultAsync(cancel),
            ChapterFormatCacheOptions);
    }

    private static string GetChapterFormatCacheKey(int chapterId)
        => $"readingsession_chapter_format_{chapterId}";

    private static AppUserReadingSessionActivityData NewActivityData(ProgressDto dto, MangaFormat format,
        int? previousPage = null, string? previousBookScrollId = null)
    {
        if (previousPage.HasValue && previousPage.Value <= dto.PageNum)
        {
            return new AppUserReadingSessionActivityData(dto, previousPage.Value, format)
            {
                StartBookScrollId = previousBookScrollId
            };
        }

        var startPage = format == MangaFormat.Epub ? dto.PageNum : Math.Max(dto.PageNum - 1, 0);

        return new AppUserReadingSessionActivityData(dto, startPage, format);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cleanupTimer.Dispose();
        _cleanupLock.Dispose();

        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _cleanupTimer.DisposeAsync();
        _cleanupLock.Dispose();

        _disposed = true;
    }
}
