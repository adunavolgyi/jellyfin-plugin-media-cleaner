using System.Net;
using System.Text;
using FluentAssertions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaCleaner.Adapters;
using MediaCleaner.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

#if JELLYFIN_10_10
using JellyfinUser = Jellyfin.Data.Entities.User;
#else
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#endif

namespace MediaCleaner.Tests;

public class ArrSyncClientTests
{
    [Fact]
    public async Task Movie_IsRemovedFromRadarrWithoutDeletingFilesAndResetInSeerr()
    {
        var handler = new FakeHandler
        {
            ["GET http://radarr/api/v3/movie?tmdbId=603"] = "[{\"id\":7,\"tmdbId\":603}]",
            ["GET http://seerr/api/v1/movie/603"] = "{\"mediaInfo\":{\"id\":42}}",
        };
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Matrix", ProviderIds = { ["Tmdb"] = "603" } };

        await Sync(handler, Config(radarr: true, seerr: true), [movie]);

        handler.Requests.Should().Contain("DELETE http://radarr/api/v3/movie/7?deleteFiles=false&addImportExclusion=false");
        handler.Requests.Should().Contain("DELETE http://seerr/api/v1/media/42");
        handler.ApiKeys.Should().OnlyContain(x => x == "key");
    }

    [Fact]
    public async Task Series_IsRemovedFromSonarrWithoutDeletingFiles()
    {
        var handler = new FakeHandler { ["GET http://sonarr/api/v3/series?tvdbId=81"] = "[{\"id\":3,\"tvdbId\":81}]" };
        var series = new Series { Id = Guid.NewGuid(), Name = "Show", ProviderIds = { ["Tvdb"] = "81" } };

        await Sync(handler, Config(sonarr: true), [series]);

        handler.Requests.Should().Contain("DELETE http://sonarr/api/v3/series/3?deleteFiles=false&addImportListExclusion=false");
    }

    [Fact]
    public async Task Episodes_OfSurvivingSeries_AreUnmonitoredOnceAndRescanned()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show", ProviderIds = { ["Tvdb"] = "81" } };
        var handler = new FakeHandler
        {
            ["GET http://sonarr/api/v3/series?tvdbId=81"] = "[{\"id\":3,\"tvdbId\":81}]",
            ["GET http://sonarr/api/v3/episode?seriesId=3"] =
                "[{\"id\":10,\"seasonNumber\":1,\"episodeNumber\":1},{\"id\":11,\"seasonNumber\":1,\"episodeNumber\":2},{\"id\":12,\"seasonNumber\":1,\"episodeNumber\":3},{\"id\":13,\"seasonNumber\":2,\"episodeNumber\":1}]",
        };
        var e1 = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 1 };
        var e23 = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 2, IndexNumberEnd = 3 };

        await Sync(handler, Config(sonarr: true), [e1, e23], series);

        handler.Requests.Count(x => x.StartsWith("PUT")).Should().Be(1);
        handler.Bodies.Should().Contain("{\"episodeIds\":[10,11,12],\"monitored\":false}");
        handler.Requests.Should().Contain("POST http://sonarr/api/v3/command");
        handler.Bodies.Should().Contain("{\"name\":\"RescanSeries\",\"seriesId\":3}");
        handler.Requests.Should().NotContain(x => x.StartsWith("DELETE"));
    }

    [Fact]
    public async Task DisabledApps_SendNoRequests()
    {
        var handler = new FakeHandler();
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Matrix", ProviderIds = { ["Tmdb"] = "603" } };
        var config = Config(radarr: true, seerr: true);
        config.Radarr.Url = " ";
        config.Seerr.ApiKey = string.Empty;

        await Sync(handler, config, [movie]);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TestConnection_Radarr_ReportsAppNameAndVersion()
    {
        var handler = new FakeHandler { ["GET http://radarr/api/v3/system/status"] = "{\"appName\":\"Radarr\",\"version\":\"5.2.1\"}" };

        var (success, message) = await TestConnection(handler, "Radarr", "http://radarr", "key");

        success.Should().BeTrue();
        message.Should().Be("Connected to Radarr 5.2.1");
    }

    [Fact]
    public async Task TestConnection_Unauthorized_ReportsRejectedKeyWithoutEchoingIt()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.Unauthorized };

        var (success, message) = await TestConnection(handler, "Seerr", "http://seerr", "topsecret");

        success.Should().BeFalse();
        message.Should().Be("API key rejected");
        handler.Requests.Should().Equal("GET http://seerr/api/v1/auth/me");
    }

    [Fact]
    public async Task TestConnection_BlankValues_SendNoRequest()
    {
        var handler = new FakeHandler();

        var (success, message) = await TestConnection(handler, "Sonarr", "", "key");

        success.Should().BeFalse();
        message.Should().Be("Enter a URL and API key");
        handler.Requests.Should().BeEmpty();
    }

    private static Task<(bool Success, string Message)> TestConnection(FakeHandler handler, string app, string url, string apiKey) =>
        new ArrSyncClient(NullLogger<ArrSyncClient>.Instance, new HttpClient(handler))
            .TestConnectionAsync(app, new ArrIntegrationConfiguration { Url = url, ApiKey = apiKey }, CancellationToken.None);

    [Fact]
    public async Task ServerError_DoesNotThrow()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.InternalServerError };
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Matrix", ProviderIds = { ["Tmdb"] = "603" } };

        var act = () => Sync(handler, Config(radarr: true), [movie]);

        await act.Should().NotThrowAsync();
        handler.Requests.Should().ContainSingle();
    }

    private static Task Sync(FakeHandler handler, PluginConfiguration config, BaseItem[] deleted, params BaseItem[] catalogItems)
    {
        var catalog = new CleanupCatalog(
            [],
            [],
            catalogItems.ToDictionary(x => x.Id.ToString("N")),
            new Dictionary<string, JellyfinUser>());
        return new ArrSyncClient(NullLogger<ArrSyncClient>.Instance, new HttpClient(handler))
            .SyncDeletedAsync(deleted, catalog, config, CancellationToken.None);
    }

    private static PluginConfiguration Config(bool radarr = false, bool sonarr = false, bool seerr = false) => new()
    {
        Radarr = radarr ? new ArrIntegrationConfiguration { Url = "http://radarr/", ApiKey = "key" } : new(),
        Sonarr = sonarr ? new ArrIntegrationConfiguration { Url = "http://sonarr", ApiKey = "key" } : new(),
        Seerr = seerr ? new ArrIntegrationConfiguration { Url = "http://seerr", ApiKey = "key" } : new(),
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> responses = [];

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public List<string> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public List<string> ApiKeys { get; } = [];

        public string this[string key]
        {
            set => responses[key] = value;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri}";
            Requests.Add(key);
            ApiKeys.AddRange(request.Headers.GetValues("X-Api-Key"));
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status);
            }

            return responses.TryGetValue(key, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
