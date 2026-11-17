namespace Jarvis.Sharp.Services;

using System.Text.Json;
using System.Text.RegularExpressions;
using Plex.Core.Http;
using Plex.Core.Http.Models;

public record PlexQueryResult(bool Exists, string Title, int? Year, string Source);

public interface IPlexService
{
    Task<PlexQueryResult> QueryPlexLibraryAsync(
        string title,
        int? year = null,
        string? plexUrl = null,
        string? plexToken = null,
        string? existingInventoryPath = null,
        CancellationToken cancellationToken = default);
}

public partial class PlexService : IPlexService
{
    private readonly IPlexClient _plexClient;

    [GeneratedRegex("[^a-zA-Z0-9 ]", RegexOptions.Compiled)]
    private static partial Regex AlphanumericRegex();

    public PlexService(IPlexClient plexClient)
    {
        _plexClient = plexClient;
    }

    public async Task<PlexQueryResult> QueryPlexLibraryAsync(
        string title,
        int? year = null,
        string? plexUrl = null,
        string? plexToken = null,
        string? existingInventoryPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var normalizedTarget = NormalizeTitle(title);
        var resolvedUrl = (plexUrl ?? Environment.GetEnvironmentVariable("PLEX_URL") ?? string.Empty).Trim().TrimEnd('/');
        var resolvedToken = (plexToken ?? Environment.GetEnvironmentVariable("PLEX_TOKEN") ?? string.Empty).Trim();
        var resolvedInventory = existingInventoryPath ?? Environment.GetEnvironmentVariable("EXISTING_MOVIES_INDEX_PATH") ?? "/state/existing-movies.json";

        if (!string.IsNullOrEmpty(resolvedUrl) && !string.IsNullOrEmpty(resolvedToken))
        {
            PlexClient? overrideClient = null;
            try
            {
                var configuredUrl = _plexClient.Options.ServerUri?.ToString().TrimEnd('/') ?? string.Empty;
                var useInjectedClient =
                    string.Equals(configuredUrl, resolvedUrl, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_plexClient.Options.Token, resolvedToken, StringComparison.Ordinal);

                IPlexClient client;
                if (useInjectedClient)
                {
                    client = _plexClient;
                }
                else
                {
                    overrideClient = new PlexClient(new PlexClientOptions
                    {
                        ServerUrl = resolvedUrl,
                        Token = resolvedToken,
                        Product = "Jarvis.Sharp"
                    });
                    client = overrideClient;
                }

                var response = await client.GetAsync<PlexMediaContainer>(
                    $"/search?query={Uri.EscapeDataString(title)}",
                    cancellationToken);

                var metadataList = response?.MediaContainer?.Metadata;
                if (metadataList != null)
                {
                    foreach (var item in metadataList)
                    {
                        var itemTitle = item.Title ?? string.Empty;
                        var itemYear = item.Year;

                        if (NormalizeTitle(itemTitle) == normalizedTarget &&
                            (year == null || itemYear == null || year == itemYear))
                        {
                            return new PlexQueryResult(true, itemTitle, itemYear, "Plex REST API");
                        }
                    }
                }
            }
            catch
            {
                // Fall back to local inventory on connection failure
            }
            finally
            {
                overrideClient?.Dispose();
            }
        }

        // Local Inventory JSON Fallback
        if (File.Exists(resolvedInventory))
        {
            try
            {
                var json = await File.ReadAllTextAsync(resolvedInventory, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        var entryTitle = elem.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        var entryYear = elem.TryGetProperty("year", out var y) && y.TryGetInt32(out var py) ? py : (int?)null;

                        if (NormalizeTitle(entryTitle) == normalizedTarget)
                        {
                            if (year == null || entryYear == null || year == entryYear)
                            {
                                return new PlexQueryResult(true, entryTitle, entryYear, "Local Inventory File");
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback returns false
            }
        }

        return new PlexQueryResult(false, title, year, "None");
    }

    private static string NormalizeTitle(string title)
    {
        return AlphanumericRegex().Replace(title.ToLowerInvariant(), string.Empty).Trim();
    }
}
