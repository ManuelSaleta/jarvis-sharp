namespace Jarvis.Sharp.Services;

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Sharp.Security;
using Jarvis.Sharp.State;

public record DispatchResult(
    bool Dispatched,
    string BatchId,
    IReadOnlyList<CandidateItem> Approved,
    IReadOnlyList<CandidateItem> SkippedExisting,
    string SummaryText,
    int? StatusCode = null
);

public interface IMediaSyncDispatcher
{
    Task<DispatchResult> DispatchApprovedCandidatesAsync(
        string? batchId,
        IEnumerable<int> indices,
        string requestedBy,
        string? webhookUrl = null,
        string? webhookSecret = null,
        CancellationToken cancellationToken = default);

    Task<DispatchResult> DispatchDirectQueryAsync(
        string query,
        string requestedBy,
        string? webhookUrl = null,
        string? webhookSecret = null,
        CancellationToken cancellationToken = default);
}

public class MediaSyncDispatcher : IMediaSyncDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly CandidateStateStore _stateStore;
    private readonly IPlexService _plexService;

    public MediaSyncDispatcher(
        HttpClient httpClient,
        CandidateStateStore stateStore,
        IPlexService plexService)
    {
        _httpClient = httpClient;
        _stateStore = stateStore;
        _plexService = plexService;
    }

    public async Task<DispatchResult> DispatchApprovedCandidatesAsync(
        string? batchId,
        IEnumerable<int> indices,
        string requestedBy,
        string? webhookUrl = null,
        string? webhookSecret = null,
        CancellationToken cancellationToken = default)
    {
        var batch = _stateStore.GetBatch(batchId);
        if (batch == null)
        {
            return new DispatchResult(
                false,
                batchId ?? "none",
                Array.Empty<CandidateItem>(),
                Array.Empty<CandidateItem>(),
                "Candidate batch not found or expired (24h TTL).");
        }

        var selected = _stateStore.ResolveIndices(batch.BatchId, indices);
        if (selected.Count == 0)
        {
            return new DispatchResult(
                false,
                batch.BatchId,
                Array.Empty<CandidateItem>(),
                Array.Empty<CandidateItem>(),
                "No valid candidates matched the provided indices.");
        }

        return await DispatchItemsAsync(
            batch.BatchId,
            selected,
            requestedBy,
            webhookUrl,
            webhookSecret,
            cancellationToken);
    }

    public async Task<DispatchResult> DispatchDirectQueryAsync(
        string query,
        string requestedBy,
        string? webhookUrl = null,
        string? webhookSecret = null,
        CancellationToken cancellationToken = default)
    {
        var item = ParseDirectQuery(query);
        var batchId = $"direct:{Guid.NewGuid():N}";
        return await DispatchItemsAsync(
            batchId,
            new[] { item },
            requestedBy,
            webhookUrl,
            webhookSecret,
            cancellationToken);
    }

    private static CandidateItem ParseDirectQuery(string rawQuery)
    {
        var text = rawQuery.Trim();
        var mediaType = "Movie";

        if (text.StartsWith("tv:", StringComparison.OrdinalIgnoreCase))
        {
            mediaType = "TV";
            text = text[3..].Trim();
        }
        else if (text.StartsWith("movie:", StringComparison.OrdinalIgnoreCase))
        {
            mediaType = "Movie";
            text = text[6..].Trim();
        }
        else if (text.StartsWith("movies:", StringComparison.OrdinalIgnoreCase))
        {
            mediaType = "Movie";
            text = text[7..].Trim();
        }

        int? year = null;
        var yearMatch = Regex.Match(text, @"\s+\(?(19\d\d|20\d\d)\)?$");
        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var parsedYear))
        {
            year = parsedYear;
            text = text[..yearMatch.Index].Trim();
        }

        return new CandidateItem(1, text, year, mediaType);
    }

    private async Task<DispatchResult> DispatchItemsAsync(
        string batchId,
        IReadOnlyList<CandidateItem> selected,
        string requestedBy,
        string? webhookUrl,
        string? webhookSecret,
        CancellationToken cancellationToken)
    {
        var approved = new List<CandidateItem>();
        var skipped = new List<CandidateItem>();

        foreach (var item in selected)
        {
            var plexCheck = await _plexService.QueryPlexLibraryAsync(item.Title, item.Year, cancellationToken: cancellationToken);
            if (plexCheck.Exists)
            {
                skipped.Add(item);
            }
            else
            {
                approved.Add(item);
            }
        }

        if (approved.Count == 0)
        {
            return new DispatchResult(
                false,
                batchId,
                approved.AsReadOnly(),
                skipped.AsReadOnly(),
                "All selected items already exist in the Plex library; nothing dispatched.");
        }

        var resolvedUrl = webhookUrl ?? Environment.GetEnvironmentVariable("MEDIA_SYNC_WEBHOOK_URL");
        var resolvedSecret = webhookSecret ?? Environment.GetEnvironmentVariable("WEBHOOK_SECRET_TOKEN");

        if (string.IsNullOrWhiteSpace(resolvedUrl) || string.IsNullOrWhiteSpace(resolvedSecret))
        {
            throw new InvalidOperationException("MEDIA_SYNC_WEBHOOK_URL and WEBHOOK_SECRET_TOKEN must be configured.");
        }

        var payload = new
        {
            batch_id = batchId,
            requested_by = requestedBy,
            items = approved
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = HmacSigner.ComputeSignature(resolvedSecret, jsonBytes);

        using var request = new HttpRequestMessage(HttpMethod.Post, resolvedUrl);
        request.Content = new ByteArrayContent(jsonBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Signature-SHA256", signature);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var summary = $"Dispatched {approved.Count} item(s) to media-sync: [{string.Join(", ", approved.Select(a => a.Year.HasValue ? $"{a.Title} ({a.Year})" : a.Title))}].";
        if (skipped.Count > 0)
        {
            summary += $" Skipped pre-existing: [{string.Join(", ", skipped.Select(s => s.Title))}].";
        }

        return new DispatchResult(
            response.IsSuccessStatusCode,
            batchId,
            approved.AsReadOnly(),
            skipped.AsReadOnly(),
            summary,
            (int)response.StatusCode);
    }
}
