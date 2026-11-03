using System.Threading;

namespace FFTTreasureMaster;

/// <summary>
/// Runs a background thread that re-stamps the armed map's tile marks about every 8 ms.
/// Running water maps animate the terrain render bytes at about 60 fps, which clears the held
/// mark between the 33 ms loop's stamps and flickers. Stamping twice per animation frame keeps
/// the mark set whenever a frame renders.
///
/// TileHolder is stateless and only ever sets bits, so the fast thread and the 33 ms loop
/// holding the same addresses at once is safe. The thread is not started in the ctor, so unit
/// tests can drive <see cref="HoldOnce"/> directly without OS threads.
/// </summary>
internal sealed class FastHold
{
    private readonly TileHolder _holder;
    private readonly int _intervalMs;

    private volatile TreasureMap? _map;
    private volatile bool _stop;
    private Thread? _thread;

    public FastHold(TileHolder holder, int intervalMs)
    {
        _holder = holder;
        _intervalMs = intervalMs;
    }

    /// <summary>
    /// Publishes the map the fast thread should hold.  Pass null to stop holding
    /// (the next HoldOnce / thread iteration becomes a no-op).
    /// </summary>
    public void Publish(TreasureMap? map) => _map = map;

    /// <summary>
    /// Executes one hold pass over the currently published map.
    /// Safe to call from any thread; a null map is a no-op.
    /// Exposed as internal so tests can exercise the hold logic without a thread.
    /// </summary>
    internal void HoldOnce()
    {
        var m = _map;
        if (m != null) _holder.Hold(m);
    }

    /// <summary>
    /// Starts the background thread. A second call while the thread runs does nothing.
    /// </summary>
    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "treasure-fasthold",
        };
        _thread.Start();
    }

    /// <summary>Signals the background thread to stop on its next iteration.</summary>
    public void Stop() => _stop = true;

    private void Run()
    {
        while (!_stop)
        {
            try { HoldOnce(); } catch { }
            Thread.Sleep(_intervalMs);
        }
    }
}
