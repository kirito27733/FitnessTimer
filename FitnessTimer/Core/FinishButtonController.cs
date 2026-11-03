namespace FitnessTimer.Core;

// Called on the UI thread; Generation may also be captured by the BLE receive thread.
public sealed class FinishButtonController
{
    private readonly IReadOnlyList<LaneTimer> lanes;
    private readonly long debounceTicks;
    private readonly long[] lastAccepted;
    private long generation;
    public FinishButtonController(IReadOnlyList<LaneTimer> lanes, long frequency)
    {
        this.lanes = lanes;
        debounceTicks = frequency / 2; // 500 ms, retained across heat resets.
        lastAccepted = Enumerable.Repeat(long.MinValue, lanes.Count).ToArray();
    }
    public bool Enabled { get; private set; }
    public long Generation => Interlocked.Read(ref generation);
    public void SetEnabled(bool value) { Enabled = value; InvalidatePending(); }
    public void InvalidatePending() => Interlocked.Increment(ref generation);
    public bool TryFinish(int laneNumber, long receivedAt, long capturedGeneration)
    {
        if (!Enabled || capturedGeneration != Generation || laneNumber < 1 || laneNumber > lanes.Count) return false;
        var index = laneNumber - 1;
        if (lastAccepted[index] != long.MinValue && receivedAt - lastAccepted[index] < debounceTicks) return false;
        if (!lanes[index].TryFinishFromButton(receivedAt)) return false;
        lastAccepted[index] = receivedAt;
        return true;
    }
}
