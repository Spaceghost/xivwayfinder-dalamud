namespace XivWayfinder.Core;

/// <summary>Per-update native-query work budget. Time between queries belongs to
/// bounded managed pose/cover preparation, not a fictitious running native query.
/// This does not cache evidence or extend its independent freshness lifetime.</summary>
public sealed class GuideBoardingQueryBudget(long frequency, int maximumQueries = 192, double maximumSeconds = .003)
{
    private long started;
    private bool running, invalid;
    public int Queries { get; private set; }
    public double QuerySeconds { get; private set; }
    public bool Available => !invalid && !running && frequency > 0 && maximumQueries > 0
        && double.IsFinite(maximumSeconds) && maximumSeconds > 0
        && Queries < maximumQueries && QuerySeconds < maximumSeconds;

    public bool TryBegin(long timestamp)
    {
        if (!Available || timestamp < 0) return false;
        started = timestamp; running = true; Queries++;
        return true;
    }

    /// <returns>False if this call exhausted the time budget. Its result may
    /// not approve a pose, even when it was the last ray in the cover.</returns>
    public bool Complete(long timestamp)
    {
        if (!running || timestamp < started) { invalid = true; running = false; return false; }
        running = false;
        QuerySeconds += (timestamp - started) / (double)frequency;
        return double.IsFinite(QuerySeconds) && QuerySeconds < maximumSeconds;
    }
}
