using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Flurl.Http;
using Kavita.API.Database;
using Kavita.API.Repositories;
using Kavita.Models.Entities.Enums;
using Microsoft.Extensions.Logging;

namespace Kavita.Services;

/// <summary>
/// Pushes KOReader sync progress to Hardcover (hardcover.app) with a personal API token from
/// <see cref="TokenVariable"/>. Links the book by title and writer on first sync and marks it as
/// Currently Reading if it is not tracked yet. A book already marked Read is left alone.
/// </summary>
public class HardcoverSyncService(IUnitOfWork unitOfWork, ILogger<HardcoverSyncService> logger)
{
    public const string TokenVariable = "KAVITA_HARDCOVER_TOKEN";
    private const string ApiUrl = "https://api.hardcover.app/v1/graphql";
    private const int StatusReading = 2;
    private const int StatusRead = 3;

    private const string OpenReadFields =
        "user_book_reads(where: {finished_at: {_is_null: true}}, order_by: {started_at: desc}, limit: 1) { id progress_pages edition { pages } }";

    public static string? Token => Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } t ? t : null;

    public async Task SyncProgress(int chapterId, float percentage)
    {
        if (Token is not { } token) return;
        try
        {
            await SyncProgress(chapterId, percentage, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hardcover sync failed for Chapter {ChapterId}", chapterId);
        }
    }

    internal async Task SyncProgress(int chapterId, float percentage, string token)
    {
        var chapter = await unitOfWork.ChapterRepository.GetChapterAsync(chapterId, ChapterIncludes.People);
        if (chapter == null) return;

        if (chapter.HardcoverId <= 0)
        {
            var writer = chapter.People.Where(p => p.Role == PersonRole.Writer).Select(p => p.Person.Name).FirstOrDefault();
            var bookId = await FindBookId(token, chapter.TitleName, writer);
            if (bookId == null)
            {
                logger.LogWarning("Hardcover has no book titled {Title} by {Writer}, set the Hardcover id on Chapter {ChapterId} by hand",
                    chapter.TitleName, writer, chapterId);
                return;
            }

            chapter.HardcoverId = bookId.Value;
            unitOfWork.ChapterRepository.Update(chapter);
            await unitOfWork.CommitAsync();
        }

        // A token with read:library sees only its owner's user_books, so no user id (read:me) is needed
        var data = await Query(token,
            $$$"""
            query ($id: Int!) {
              books(where: {id: {_eq: $id}}) {
                editions(where: {_or: [{reading_format_id: {_is_null: true}}, {reading_format_id: {_neq: 2}}]},
                         order_by: {users_count: desc_nulls_last}, limit: 1) { id pages }
              }
              user_books(where: {book_id: {_eq: $id}}) { id user_id status_id {{{OpenReadFields}}} }
            }
            """, new JsonObject { ["id"] = chapter.HardcoverId });
        var book = data["books"]?.AsArray().FirstOrDefault();
        if (book == null) return;

        var userBooks = data["user_books"]?.AsArray() ?? [];
        if (userBooks.Select(b => b?["user_id"]?.GetValue<int>()).Distinct().Count() > 1)
        {
            logger.LogWarning("Hardcover token sees other users' libraries, use a token with only read:library and write:library");
            return;
        }

        var edition = book["editions"]?.AsArray().FirstOrDefault();
        var userBook = userBooks.FirstOrDefault();
        JsonNode? read;

        if (userBook == null)
        {
            var insert = new JsonObject { ["book_id"] = chapter.HardcoverId, ["status_id"] = StatusReading };
            if (edition?["id"] != null) insert["edition_id"] = edition["id"]!.GetValue<int>();
            var result = (await Query(token,
                $$$"""
                mutation ($object: UserBookCreateInput!) {
                  insert_user_book(object: $object) { error user_book { {{{OpenReadFields}}} } }
                }
                """, new JsonObject { ["object"] = insert }))["insert_user_book"];
            read = OpenRead(result);
        }
        else if (userBook["status_id"]?.GetValue<int>() == StatusRead)
        {
            return;
        }
        else
        {
            read = userBook["user_book_reads"]?.AsArray().FirstOrDefault();
            if (read == null)
            {
                var result = (await Query(token,
                    $$$"""
                    mutation ($id: Int!) {
                      update_user_book(id: $id, object: {status_id: 2}) { error user_book { {{{OpenReadFields}}} } }
                    }
                    """, new JsonObject { ["id"] = userBook["id"]!.GetValue<int>() }))["update_user_book"];
                read = OpenRead(result);
            }
        }

        if (read == null) return;

        var pages = read["edition"]?["pages"]?.GetValue<int?>() ?? edition?["pages"]?.GetValue<int?>() ?? 0;
        var progressPages = ToProgressPages(percentage, pages);
        if (progressPages <= 0 || progressPages == read["progress_pages"]?.GetValue<int?>()) return;

        await Query(token,
            """
            mutation ($id: Int!, $pages: Int!) {
              update_user_book_read(id: $id, object: {progress_pages: $pages}) { error }
            }
            """, new JsonObject { ["id"] = read["id"]!.GetValue<int>(), ["pages"] = progressPages });
    }

    public static int ToProgressPages(float percentage, int editionPages)
    {
        if (editionPages <= 0 || percentage <= 0) return 0;
        return Math.Clamp((int)Math.Round(percentage * editionPages, MidpointRounding.AwayFromZero), 1, editionPages);
    }

    public static string NormalizeTitle(string title)
    {
        return new string(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private async Task<int?> FindBookId(string token, string title, string? writer)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var ids = (await Query(token,
            "query ($q: String!) { search(query: $q, query_type: \"Book\", per_page: 5, page: 1) { ids } }",
            new JsonObject { ["q"] = $"{title} {writer}".Trim() }))["search"]?["ids"]?.AsArray()
            .Select(id => int.TryParse(id?.ToString(), out var v) ? v : 0).Where(v => v > 0).ToList();
        if (ids == null || ids.Count == 0) return null;

        var books = (await Query(token, "query ($ids: [Int!]!) { books(where: {id: {_in: $ids}}) { id title } }",
            new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()) }))["books"]?.AsArray();

        // Keep the search ranking; Hasura's _in returns rows in any order
        var wanted = NormalizeTitle(title);
        return ids.Cast<int?>().FirstOrDefault(id => books?.Any(b =>
            b?["id"]?.GetValue<int>() == id && NormalizeTitle(b["title"]?.GetValue<string>() ?? string.Empty) == wanted) == true);
    }

    private JsonNode? OpenRead(JsonNode? mutationResult)
    {
        if (mutationResult?["error"]?.GetValue<string?>() is { Length: > 0 } error)
        {
            logger.LogWarning("Hardcover could not mark the book as Currently Reading: {Error}", error);
            return null;
        }
        return mutationResult?["user_book"]?["user_book_reads"]?.AsArray().FirstOrDefault();
    }

    private static async Task<JsonNode> Query(string token, string query, JsonObject? variables = null)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables ?? new JsonObject() }.ToJsonString();
        var bearer = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token : $"Bearer {token}";

        var response = await ApiUrl
            .WithHeader("authorization", bearer)
            .WithHeader("User-Agent", "Kavita fork Hardcover sync")
            .WithHeader("Content-Type", "application/json")
            .PostStringAsync(body)
            .ReceiveString();

        var json = JsonNode.Parse(response) ?? throw new InvalidOperationException("Empty Hardcover response");
        if (json["errors"] is JsonArray { Count: > 0 } errors)
            throw new InvalidOperationException($"Hardcover GraphQL error: {errors.ToJsonString()}");
        return json["data"] ?? throw new InvalidOperationException("Hardcover response has no data");
    }
}
