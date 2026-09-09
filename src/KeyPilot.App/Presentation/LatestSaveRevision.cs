namespace KeyPilot.App.Presentation;

/// <summary>
/// Prevents an older asynchronous save completion from replacing a newer UI mutation.
/// </summary>
internal sealed class LatestSaveRevision
{
    private long _revision;

    public long BeginRequest() => Interlocked.Increment(ref _revision);

    public bool IsLatest(long revision) =>
        revision > 0 && revision == Volatile.Read(ref _revision);
}
