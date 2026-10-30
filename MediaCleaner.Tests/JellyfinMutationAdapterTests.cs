using FluentAssertions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaCleaner.Adapters;
using MediaCleaner.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

#if JELLYFIN_10_10
using JellyfinActivityLog = Jellyfin.Data.Entities.ActivityLog;
using JellyfinUser = Jellyfin.Data.Entities.User;
#else
using JellyfinActivityLog = Jellyfin.Database.Implementations.Entities.ActivityLog;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
#endif

namespace MediaCleaner.Tests;

public class JellyfinMutationAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_MarksOnlyPlannedDecisionsBeforeDeletionAndNotifiesOnlyAfterSuccess()
    {
        var blockedItem = Item("blocked-season", MediaItemKind.Season, "Protected season");
        var deletedItem = Item("deleted-movie", MediaItemKind.Movie, "Deleted movie");
        var blockedDecision = Decision(blockedItem, markUnplayedUserIds: ["user"]);
        var deletedDecision = Decision(
            deletedItem,
            markUnplayedUserIds: ["user"],
            matchedRules: ["first rule", "second rule"]);
        var events = new List<string>();
        var blockedEntity = new RecordingMovie { Id = Guid.NewGuid(), Name = blockedItem.Name };
        var deletedEntity = new RecordingMovie
        {
            Id = Guid.NewGuid(),
            Name = deletedItem.Name,
            MarkUnplayedCallback = () => events.Add("mark-unplayed"),
        };
        var user = new JellyfinUser("User", "test", "test");
        var activities = new List<JellyfinActivityLog>();
        var libraryManager = new Mock<ILibraryManager>();
        var activityManager = new Mock<IActivityManager>();
        libraryManager
            .Setup(x => x.DeleteItem(deletedEntity, It.IsAny<DeleteOptions>(), true))
            .Callback(() => events.Add("delete"));
        activityManager
            .Setup(x => x.CreateAsync(It.IsAny<JellyfinActivityLog>()))
            .Callback<JellyfinActivityLog>(activity =>
            {
                events.Add("activity");
                activities.Add(activity);
            })
            .Returns(Task.CompletedTask);
        var adapter = new JellyfinMutationAdapter(
            NullLogger<JellyfinMutationAdapter>.Instance,
            libraryManager.Object,
            activityManager.Object);
        var plan = new CleanupPlan(
            [blockedDecision, deletedDecision],
            [new DeletionOperation(deletedItem.Id, deletedItem.Kind, deletedItem.Name)],
            []);
        var catalog = Catalog(
            new Dictionary<string, JellyfinUser>(StringComparer.OrdinalIgnoreCase) { ["user"] = user },
            (blockedItem.Id, blockedEntity),
            (deletedItem.Id, deletedEntity));

        await adapter.ExecuteAsync(plan, catalog, CancellationToken.None);

        events.Should().Equal("mark-unplayed", "delete", "activity");
        activities.Should().ContainSingle();
        activities[0].Name.Should().Be(deletedDecision.Notification.Title);
        activities[0].Overview.Should().Contain("Path:");
        activities[0].Overview.Should().Contain(deletedItem.Path);
        activities[0].Overview.Should().Contain("Reason: test reason");
        activities[0].Overview.Should().Contain("Matched rules: first rule, second rule");
        activities[0].Overview.Should().Contain("Result: successfully deleted");
        activities.Should().NotContain(activity => activity.Name == blockedDecision.Notification.Title);
        libraryManager.Verify(x => x.DeleteItem(blockedEntity, It.IsAny<DeleteOptions>(), true), Times.Never);
        blockedEntity.MarkUnplayedCount.Should().Be(0);
        deletedEntity.MarkUnplayedCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_MarksBeforeDeletionButDoesNotNotifyWhenDeletionFails()
    {
        var item = Item("failed", MediaItemKind.Movie, "Failed movie");
        var entity = new RecordingMovie { Id = Guid.NewGuid(), Name = item.Name };
        var user = new JellyfinUser("User", "test", "test");
        var libraryManager = new Mock<ILibraryManager>();
        var activityManager = new Mock<IActivityManager>();
        libraryManager
            .Setup(x => x.DeleteItem(entity, It.IsAny<DeleteOptions>(), true))
            .Throws(new InvalidOperationException("delete failed"));
        var adapter = new JellyfinMutationAdapter(
            NullLogger<JellyfinMutationAdapter>.Instance,
            libraryManager.Object,
            activityManager.Object);
        var decision = Decision(item, markUnplayedUserIds: ["user"]);
        var plan = new CleanupPlan(
            [decision],
            [new DeletionOperation(item.Id, item.Kind, item.Name)],
            []);
        var catalog = Catalog(
            new Dictionary<string, JellyfinUser>(StringComparer.OrdinalIgnoreCase) { ["user"] = user },
            (item.Id, entity));

        await adapter.ExecuteAsync(plan, catalog, CancellationToken.None);

        activityManager.Verify(x => x.CreateAsync(It.IsAny<JellyfinActivityLog>()), Times.Never);
        entity.MarkUnplayedCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotDeleteWhenMarkUnplayedFails()
    {
        var item = Item("failed-mark-unplayed", MediaItemKind.Movie, "Failed mark unplayed");
        var entity = new RecordingMovie
        {
            Id = Guid.NewGuid(),
            Name = item.Name,
            MarkUnplayedCallback = () => throw new InvalidOperationException("mark unplayed failed"),
        };
        var user = new JellyfinUser("User", "test", "test");
        var libraryManager = new Mock<ILibraryManager>();
        var activityManager = new Mock<IActivityManager>();
        var adapter = new JellyfinMutationAdapter(
            NullLogger<JellyfinMutationAdapter>.Instance,
            libraryManager.Object,
            activityManager.Object);
        var decision = Decision(item, markUnplayedUserIds: ["user"]);
        var plan = new CleanupPlan(
            [decision],
            [new DeletionOperation(item.Id, item.Kind, item.Name)],
            []);
        var catalog = Catalog(
            new Dictionary<string, JellyfinUser>(StringComparer.OrdinalIgnoreCase) { ["user"] = user },
            (item.Id, entity));

        var act = () => adapter.ExecuteAsync(plan, catalog, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("mark unplayed failed");
        libraryManager.Verify(x => x.DeleteItem(entity, It.IsAny<DeleteOptions>(), true), Times.Never);
        activityManager.Verify(x => x.CreateAsync(It.IsAny<JellyfinActivityLog>()), Times.Never);
    }

    [Fact]
    public void GetDecisionsForItemIds_ExcludesItemsOutsideTheProvidedSet()
    {
        var blocked = Decision(Item("blocked", MediaItemKind.Season, "Blocked"), markUnplayedUserIds: ["user"]);
        var failed = Decision(Item("failed", MediaItemKind.Movie, "Failed"), markUnplayedUserIds: ["user"]);
        var deleted = Decision(Item("deleted", MediaItemKind.Movie, "Deleted"), markUnplayedUserIds: ["user"]);

        var result = JellyfinMutationAdapter.GetDecisionsForItemIds(
                [blocked, failed, deleted],
                new HashSet<string>(["DELETED"], StringComparer.OrdinalIgnoreCase))
            .ToList();

        result.Should().ContainSingle().Which.Should().BeSameAs(deleted);
    }

    [Fact]
    public async Task ExecuteAsync_MarksCascadedEpisodesUnplayedForTheirSeasonDecision()
    {
        var seasonItem = Item("season", MediaItemKind.Season, "Season") with { EpisodeIds = ["episode"] };
        var seasonEntity = new RecordingMovie { Id = Guid.NewGuid(), Name = "Season" };
        var episodeEntity = new RecordingMovie { Id = Guid.NewGuid(), Name = "Episode" };
        var user = new JellyfinUser("User", "test", "test");
        var adapter = new JellyfinMutationAdapter(
            NullLogger<JellyfinMutationAdapter>.Instance,
            Mock.Of<ILibraryManager>(),
            Mock.Of<IActivityManager>(x => x.CreateAsync(It.IsAny<JellyfinActivityLog>()) == Task.CompletedTask));
        var plan = new CleanupPlan(
            [Decision(seasonItem, markUnplayedUserIds: ["user"])],
            [
                new DeletionOperation("episode", MediaItemKind.Episode, "Episode"),
                new DeletionOperation("season", MediaItemKind.Season, "Season"),
            ],
            []);
        var catalog = Catalog(
            new Dictionary<string, JellyfinUser>(StringComparer.OrdinalIgnoreCase) { ["user"] = user },
            ("episode", episodeEntity),
            ("season", seasonEntity));

        await adapter.ExecuteAsync(plan, catalog, CancellationToken.None);

        episodeEntity.MarkUnplayedCount.Should().Be(1);
        seasonEntity.MarkUnplayedCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsSeasonDeletion_WhenOneOfItsEpisodesFailedToDelete()
    {
        var seasonItem = Item("season", MediaItemKind.Season, "Season") with { EpisodeIds = ["episode"] };
        var seasonEntity = new RecordingMovie { Id = Guid.NewGuid(), Name = "Season" };
        var episodeEntity = new RecordingMovie { Id = Guid.NewGuid(), Name = "Episode" };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(x => x.DeleteItem(episodeEntity, It.IsAny<DeleteOptions>(), true))
            .Throws<InvalidOperationException>();
        var adapter = new JellyfinMutationAdapter(
            NullLogger<JellyfinMutationAdapter>.Instance,
            libraryManager.Object,
            Mock.Of<IActivityManager>(x => x.CreateAsync(It.IsAny<JellyfinActivityLog>()) == Task.CompletedTask));
        var plan = new CleanupPlan(
            [Decision(seasonItem)],
            [
                new DeletionOperation("episode", MediaItemKind.Episode, "Episode"),
                new DeletionOperation("season", MediaItemKind.Season, "Season"),
            ],
            []);
        var catalog = Catalog(("episode", episodeEntity), ("season", seasonEntity)) with { Items = [seasonItem] };

        await adapter.ExecuteAsync(plan, catalog, CancellationToken.None);

        libraryManager.Verify(x => x.DeleteItem(seasonEntity, It.IsAny<DeleteOptions>(), true), Times.Never);
    }

    private static CleanupCatalog Catalog(params (string Id, BaseItem Item)[] items) =>
        Catalog(new Dictionary<string, JellyfinUser>(StringComparer.OrdinalIgnoreCase), items);

    private static CleanupCatalog Catalog(
        IReadOnlyDictionary<string, JellyfinUser> users,
        params (string Id, BaseItem Item)[] items) =>
        new(
            [],
            [],
            items.ToDictionary(x => x.Id, x => x.Item, StringComparer.OrdinalIgnoreCase),
            users);

    private static CleanupDecision Decision(
        MediaItem item,
        IReadOnlyList<string>? markUnplayedUserIds = null,
        IReadOnlyList<string>? matchedRules = null) =>
        new(
            item,
            ExpiredKind.Played,
            [],
            "test reason",
            new ActivityNotification($"{item.Name} was deleted", "test", item.Path ?? string.Empty),
            markUnplayedUserIds ?? [],
            matchedRules ?? ["test rule"]);

    private static MediaItem Item(string id, MediaItemKind kind, string name) =>
        new(
            id,
            kind,
            name,
            name,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            $"/media/{id}",
            "/media",
            [],
            []);

    private sealed class RecordingMovie : Movie
    {
        public int MarkUnplayedCount { get; private set; }

        public Action? MarkUnplayedCallback { get; init; }

        public override void MarkUnplayed(JellyfinUser user)
        {
            MarkUnplayedCount++;
            MarkUnplayedCallback?.Invoke();
        }
    }
}
