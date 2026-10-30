using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaCleaner.Compatibility;
using MediaCleaner.Core;
using MediaCleaner.LeavingSoon;
using CoreExpiredKind = MediaCleaner.Core.ExpiredKind;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Adapters;

internal sealed class JellyfinMutationAdapter(
    ILogger<JellyfinMutationAdapter> logger,
    ILibraryManager libraryManager,
    IActivityManager activityManager,
    LeavingSoonCoordinator? leavingSoonCoordinator = null,
    ArrSyncClient? arrSyncClient = null) : IMediaMutationAdapter
{
    public async Task ExecuteAsync(CleanupPlan plan, CleanupCatalog catalog, CancellationToken cancellationToken)
    {
        var plannedDeletionIds = plan.Deletions
            .Select(operation => operation.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var successfullyDeletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var decisionsByItemId = GetDecisionsForItemIds(plan.Decisions, plannedDeletionIds)
            .ToDictionary(decision => decision.Item.Id, StringComparer.OrdinalIgnoreCase);
        // Cascaded episodes without a decision of their own inherit their season's/series' decision (mark-as-unplayed).
        var markUnplayedByItemId = new Dictionary<string, CleanupDecision>(decisionsByItemId, StringComparer.OrdinalIgnoreCase);
        foreach (var decision in decisionsByItemId.Values)
        {
            foreach (var episodeId in decision.Item.EpisodeIds ?? [])
            {
                markUnplayedByItemId.TryAdd(episodeId, decision);
            }
        }

        var itemsById = catalog.Items.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var failedEpisodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var operation in plan.Deletions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!catalog.ItemsById.TryGetValue(operation.ItemId, out var item))
                {
                    logger.LogWarning("Skipping deletion for unknown item id {ItemId}", operation.ItemId);
                    continue;
                }

                // Never let a recursive folder delete remove a file Jellyfin reported as not deleted.
                if (operation.Kind != MediaItemKind.Episode
                    && itemsById.GetValueOrDefault(operation.ItemId)?.EpisodeIds?.FirstOrDefault(failedEpisodeIds.Contains) is { } failedEpisode)
                {
                    logger.LogWarning("Skipping deletion of {Name} because episode {EpisodeId} could not be deleted", operation.Name, failedEpisode);
                    continue;
                }

                var deleted = false;
                void Delete()
                {
                    // Jellyfin persists user data from MarkUnplayed, so it must run immediately
                    // before deletion while the same safety revision is guarded.
                    if (markUnplayedByItemId.TryGetValue(operation.ItemId, out var decision))
                    {
                        MarkUnplayed(decision, item, catalog);
                    }

                    try
                    {
                        JellyfinCompatibility.DeleteItem(libraryManager, item, new DeleteOptions { DeleteFileLocation = true });
                        deleted = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error deleting item: {Name}", operation.Name);
                    }
                }

                if (plan.SafetyRevision is { } revision && leavingSoonCoordinator is not null)
                {
                    leavingSoonCoordinator.ExecuteDeletionIfRevisionCurrent(revision, Delete);
                }
                else
                {
                    Delete();
                }

                if (deleted)
                {
                    successfullyDeletedIds.Add(operation.ItemId);
                }
                else if (operation.Kind == MediaItemKind.Episode)
                {
                    failedEpisodeIds.Add(operation.ItemId);
                }
            }
        }
        finally
        {
            // Sync even when the loop was cancelled, so already-deleted items are not re-downloaded by Sonarr.
            if (successfullyDeletedIds.Count > 0 && arrSyncClient is not null && Plugin.Instance?.Configuration is { } config)
            {
                var deletedItems = successfullyDeletedIds
                    .Select(id => catalog.ItemsById.GetValueOrDefault(id))
                    .OfType<MediaBrowser.Controller.Entities.BaseItem>()
                    .ToList();
                await arrSyncClient.SyncDeletedAsync(deletedItems, catalog, config, CancellationToken.None);
            }
        }

        foreach (var decision in GetDecisionsForItemIds(plan.Decisions, successfullyDeletedIds))
        {
            LogSuccessfulDeletion(decision);
            var notificationDecision = WithNotificationOverview(decision);
            await CreateNotification(notificationDecision, cancellationToken);
        }

        if (successfullyDeletedIds.Count > 0 && leavingSoonCoordinator is not null)
        {
            leavingSoonCoordinator.MarkDeleted(successfullyDeletedIds);
        }
    }

    internal static IEnumerable<CleanupDecision> GetDecisionsForItemIds(
        IEnumerable<CleanupDecision> decisions,
        ISet<string> itemIds) =>
        decisions.Where(decision => itemIds.Contains(decision.Item.Id));

    private void LogSuccessfulDeletion(CleanupDecision decision)
    {
        if (decision.Kind is CoreExpiredKind.Played or CoreExpiredKind.InProgress)
        {
            var users = string.Join(", ", decision.Playback.Select(x => $"{(x.UserName ?? x.UserId)} ({x.LastPlayedDate?.ToLocalTime()})"));
            logger.LogInformation("({Type}) \"{Name}\" was deleted because {Reason}: {Users}",
                decision.Item.Kind, decision.Item.FullName, decision.Kind == CoreExpiredKind.Played ? "expired for" : "started but not finished by", users);
            return;
        }

        if (decision.Kind == CoreExpiredKind.NotPlayed)
        {
            logger.LogInformation("({Type}) \"{Name}\" was deleted because no one played it since {DateCreated}",
                decision.Item.Kind, decision.Item.FullName, decision.Item.DateCreated.ToLocalTime());
            return;
        }

        logger.LogInformation("({Type}) \"{Name}\" was deleted because it was added at {DateCreated}",
            decision.Item.Kind, decision.Item.FullName, decision.Item.DateCreated.ToLocalTime());
    }

    private async Task CreateNotification(CleanupDecision decision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await activityManager.CreateAsync(
            JellyfinCompatibility.CreateActivityLog(
                decision.Notification.Title,
                "MediaCleaner",
                Guid.Empty,
                decision.Notification.ShortOverview,
                decision.Notification.Overview));
    }

    internal static CleanupDecision WithNotificationOverview(CleanupDecision decision)
    {
        return decision with
        {
            Notification = decision.Notification with
            {
                Overview = BuildNotificationOverview(decision),
            },
        };
    }

    internal static string BuildNotificationOverview(CleanupDecision decision)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Path:");
        builder.AppendLine(string.IsNullOrWhiteSpace(decision.Item.Path) ? "(no path)" : decision.Item.Path);
        builder.AppendLine();
        builder.AppendLine("Decision:");
        builder.Append("Reason: ");
        builder.AppendLine(decision.Reason);
        builder.Append("Matched rules: ");
        if (decision.MatchedRules.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            builder.AppendLine(string.Join(", ", decision.MatchedRules));
        }

        builder.AppendLine("Result: successfully deleted");

        return builder.ToString().TrimEnd();
    }

    private static void MarkUnplayed(CleanupDecision decision, BaseItem item, CleanupCatalog catalog)
    {
        if (decision.MarkUnplayedUserIds.Count == 0)
        {
            return;
        }

        foreach (var userId in decision.MarkUnplayedUserIds)
        {
            if (catalog.UsersById.TryGetValue(userId, out var user))
            {
                JellyfinCompatibility.MarkUnplayed(item, user);
            }
        }
    }
}
