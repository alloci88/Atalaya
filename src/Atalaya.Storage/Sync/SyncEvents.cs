namespace Atalaya.Storage.Sync;

/// <summary>Permanent sync indicator state (§3): green/amber/red in the app status bar.</summary>
public enum SyncHealth
{
    /// <summary>Up to date with the remote.</summary>
    Green,

    /// <summary>Offline or pending pushes queued; working on last pull.</summary>
    Amber,

    /// <summary>Last sync failed (auth, conflict it could not resolve, etc.).</summary>
    Red,
}

/// <summary>A change observed after a pull, so the UI can react live (§3).</summary>
/// <param name="RelativePath">Repo-relative path of the changed file.</param>
/// <param name="Kind">Added / Modified / Deleted.</param>
public sealed record HubChange(string RelativePath, HubChangeKind Kind);

public enum HubChangeKind
{
    Added,
    Modified,
    Deleted,
}

/// <summary>Outcome of a pull: what changed, and any notifications for the user.</summary>
public sealed record PullResult(IReadOnlyList<HubChange> Changes, IReadOnlyList<string> Notifications)
{
    public static PullResult Empty { get; } = new(Array.Empty<HubChange>(), Array.Empty<string>());

    public bool HasChanges => Changes.Count > 0;
}
