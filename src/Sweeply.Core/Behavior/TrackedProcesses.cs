namespace Sweeply.Core.Behavior;

/// <summary>
/// The processes being recorded: the one chosen (and, when it was already running, the ones it had started), then
/// every process one of them starts, while it runs. A process that ended leaves the set, since Windows reuses
/// process ids. Use from one thread.
/// </summary>
public sealed class TrackedProcesses
{
    private readonly HashSet<int> _running;

    public TrackedProcesses(IEnumerable<int> processIds) => _running = new HashSet<int>(processIds);

    /// <summary>How many have ever been recorded.</summary>
    public int Seen { get; private set; }

    public int Running => _running.Count;

    public bool Contains(int processId) => _running.Contains(processId);

    /// <summary>A process started: recorded too when its parent is. True when it is.</summary>
    public bool Started(int processId, int parentId)
    {
        if (!_running.Contains(parentId) || processId == parentId) return false;
        if (_running.Add(processId)) Seen++;
        return true;
    }

    /// <summary>A process ended. True when it was one of them.</summary>
    public bool Exited(int processId) => _running.Remove(processId);

    /// <summary>
    /// The process and everything it started that is still running, from (process id, parent id) pairs such as a
    /// snapshot of the running processes. A parent that ended long ago and whose id went to a new process could
    /// make an unrelated process look like a child; a child is only taken when it started after its parent.
    /// </summary>
    public static IReadOnlyList<int> WithDescendants(int root, IEnumerable<(int Id, int Parent, DateTime StartedUtc)> processes)
    {
        var list = processes.ToList();
        var started = list.ToDictionary(p => p.Id, p => p.StartedUtc);
        var result = new List<int> { root };
        var seen = new HashSet<int> { root };
        for (int i = 0; i < result.Count; i++)
        {
            int parent = result[i];
            foreach (var p in list)
                if (p.Parent == parent && !seen.Contains(p.Id)
                    && (!started.TryGetValue(parent, out var parentStarted) || p.StartedUtc >= parentStarted))
                {
                    seen.Add(p.Id);
                    result.Add(p.Id);
                }
        }
        return result;
    }
}
