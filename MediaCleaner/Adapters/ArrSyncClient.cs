using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaCleaner.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Adapters;

/// <summary>
/// Mirrors Jellyfin deletions into Radarr, Sonarr and Seerr. Jellyfin has already removed the files,
/// so these apps are never asked to delete files. Failures are logged and never propagate.
/// </summary>
internal sealed class ArrSyncClient(ILogger<ArrSyncClient> logger, HttpClient httpClient)
{
    public async Task SyncDeletedAsync(
        IReadOnlyCollection<BaseItem> deletedItems,
        CleanupCatalog catalog,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        // Per-run, so one unreachable app is skipped after its first failure; the client outlives runs.
        var failedApps = new HashSet<ArrIntegrationConfiguration>();
        async Task Guarded(ArrIntegrationConfiguration app, string what, Func<Task> action)
        {
            if (failedApps.Contains(app))
            {
                return;
            }

            try
            {
                await action();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Only an unreachable app is skipped; HTTP errors and bad payloads affect just this item.
                if (ex is HttpRequestException { StatusCode: null } or TaskCanceledException)
                {
                    failedApps.Add(app);
                }
                logger.LogWarning("Radarr/Sonarr/Seerr sync failed for {What}: {Message}; skipping this app for the rest of this run", what, ex.Message);
            }
        }

        var deletedSeriesIds = deletedItems.OfType<Series>().Select(x => x.Id).ToHashSet();
        var episodesBySeries = new Dictionary<Guid, List<Episode>>();
        foreach (var item in deletedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (item)
            {
                case Movie movie:
                    await Guarded(config.Radarr, $"movie '{movie.Name}'", () =>
                        RemoveFromArr(config.Radarr, "movie", "tmdbId", movie.GetProviderId(MetadataProvider.Tmdb), "deleteFiles=false&addImportExclusion=false", cancellationToken));
                    await Guarded(config.Seerr, $"movie '{movie.Name}' in Seerr", () =>
                        ResetSeerr(config.Seerr, "movie", movie.GetProviderId(MetadataProvider.Tmdb), cancellationToken));
                    break;
                case Series series:
                    await Guarded(config.Sonarr, $"series '{series.Name}'", () =>
                        RemoveFromArr(config.Sonarr, "series", "tvdbId", series.GetProviderId(MetadataProvider.Tvdb), "deleteFiles=false&addImportListExclusion=false", cancellationToken));
                    await Guarded(config.Seerr, $"series '{series.Name}' in Seerr", () =>
                        ResetSeerr(config.Seerr, "tv", series.GetProviderId(MetadataProvider.Tmdb), cancellationToken));
                    break;
                case Episode episode when !deletedSeriesIds.Contains(episode.SeriesId):
                    if (!episodesBySeries.TryGetValue(episode.SeriesId, out var list))
                    {
                        episodesBySeries[episode.SeriesId] = list = [];
                    }

                    list.Add(episode);
                    break;
            }
        }

        foreach (var (seriesId, episodes) in episodesBySeries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Guarded(config.Sonarr, $"episodes of series {seriesId:N}", () =>
            {
                var series = catalog.ItemsById.GetValueOrDefault(seriesId.ToString("N")) ?? episodes[0].Series;
                return UnmonitorEpisodes(config.Sonarr, series?.GetProviderId(MetadataProvider.Tvdb), episodes, cancellationToken);
            });
        }
    }

    private async Task RemoveFromArr(ArrIntegrationConfiguration app, string resource, string idParam, string? providerId, string deleteQuery, CancellationToken ct)
    {
        if (!IsEnabled(app) || !HasId(providerId, resource))
        {
            return;
        }

        var id = FindId(await Send(HttpMethod.Get, app, $"/api/v3/{resource}?{idParam}={providerId}", null, ct), idParam, providerId!);
        if (id is not null)
        {
            await Send(HttpMethod.Delete, app, $"/api/v3/{resource}/{id}?{deleteQuery}", null, ct);
        }
    }

    private async Task ResetSeerr(ArrIntegrationConfiguration app, string type, string? tmdbId, CancellationToken ct)
    {
        if (!IsEnabled(app) || !HasId(tmdbId, type))
        {
            return;
        }

        var info = await Send(HttpMethod.Get, app, $"/api/v1/{type}/{tmdbId}", null, ct);
        if (info is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("mediaInfo", out var media)
            && media.ValueKind == JsonValueKind.Object
            && media.TryGetProperty("id", out var id))
        {
            await Send(HttpMethod.Delete, app, $"/api/v1/media/{id.GetInt32()}", null, ct);
        }
    }

    private async Task UnmonitorEpisodes(ArrIntegrationConfiguration app, string? tvdbId, List<Episode> deleted, CancellationToken ct)
    {
        if (!IsEnabled(app) || !HasId(tvdbId, "episodes"))
        {
            return;
        }

        var seriesId = FindId(await Send(HttpMethod.Get, app, $"/api/v3/series?tvdbId={tvdbId}", null, ct), "tvdbId", tvdbId!);
        if (seriesId is null)
        {
            return;
        }

        var wanted = deleted
            .Where(x => x.ParentIndexNumber.HasValue && x.IndexNumber.HasValue)
            .SelectMany(x => Enumerable
                .Range(x.IndexNumber!.Value, Math.Max(1, (x.IndexNumberEnd ?? x.IndexNumber.Value) - x.IndexNumber.Value + 1))
                .Select(number => (Season: x.ParentIndexNumber!.Value, Number: number)))
            .ToHashSet();
        if (await Send(HttpMethod.Get, app, $"/api/v3/episode?seriesId={seriesId}", null, ct) is not { ValueKind: JsonValueKind.Array } arrEpisodes)
        {
            return;
        }

        var ids = arrEpisodes.EnumerateArray()
            .Where(e => wanted.Contains((e.GetProperty("seasonNumber").GetInt32(), e.GetProperty("episodeNumber").GetInt32())))
            .Select(e => e.GetProperty("id").GetInt32())
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        await Send(HttpMethod.Put, app, "/api/v3/episode/monitor", new { episodeIds = ids, monitored = false }, ct);
        await Send(HttpMethod.Post, app, "/api/v3/command", new { name = "RescanSeries", seriesId }, ct);
    }

    private static bool IsEnabled(ArrIntegrationConfiguration app) =>
        !string.IsNullOrWhiteSpace(app.Url) && !string.IsNullOrWhiteSpace(app.ApiKey);

    private bool HasId(string? providerId, string what)
    {
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            return true;
        }

        logger.LogDebug("Skipping Radarr/Sonarr/Seerr sync for {What}: no provider id", what);
        return false;
    }

    // Only an entry whose provider id matches is returned, so a proxy ignoring the query filter can't cause a wrong delete.
    private static int? FindId(JsonElement? array, string providerProperty, string providerId) =>
        array is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray()
                .Where(e => e.TryGetProperty(providerProperty, out var v) && v.ToString() == providerId.Trim())
                .Select(e => (int?)e.GetProperty("id").GetInt32())
                .FirstOrDefault()
            : null;

    /// <summary>Checks the given (possibly unsaved) URL and API key. Never includes the API key in the message.</summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(string app, ArrIntegrationConfiguration config, CancellationToken cancellationToken)
    {
        if (!IsEnabled(config))
        {
            return (false, "Enter a URL and API key");
        }

        var isSeerr = app == "Seerr";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            // Seerr's /status is unauthenticated, so /auth/me is used to validate the key.
            var body = await Send(HttpMethod.Get, config, isSeerr ? "/api/v1/auth/me" : "/api/v3/system/status", null, timeout.Token);
            if (body is null)
            {
                return (false, "HTTP 404");
            }

            if (isSeerr)
            {
                return (true, "Connected to Seerr");
            }

            string Read(string name) => body.Value.TryGetProperty(name, out var v) ? v.ToString() : string.Empty;
            return (true, $"Connected to {Read("appName")} {Read("version")}".TrimEnd());
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (false, "API key rejected");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            return (false, $"HTTP {(int)status}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "Timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or InvalidOperationException or NotSupportedException)
        {
            return (false, ex.Message);
        }
    }

    // Returns the parsed body for GETs; null for 404 and for non-GET requests.
    private async Task<JsonElement?> Send(HttpMethod method, ArrIntegrationConfiguration app, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, app.Url.Trim().TrimEnd('/') + path);
        request.Headers.Add("X-Api-Key", app.ApiKey.Trim());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (method != HttpMethod.Get)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }
}
