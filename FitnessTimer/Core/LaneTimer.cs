using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FitnessTimer.Core;

public enum LaneState { Ready, Running, Paused, Finished }

public sealed class LaneTimer : INotifyPropertyChanged
{
    private readonly Func<long> clock;
    private readonly long frequency;
    private long startedAt;
    private long accumulated;
    private string participant = "";
    public LaneTimer(int number, Func<long>? clock = null, long frequency = 0)
    {
        Number = number;
        this.clock = clock ?? Stopwatch.GetTimestamp;
        this.frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
    }
    public int Number { get; }
    public string Participant { get => participant; set { participant = value; Changed(); } }
    public LaneState State { get; private set; }
    public int FiveCount { get; private set; }
    public int FifteenCount { get; private set; }
    public int PenaltySeconds => FiveCount * 5 + FifteenCount * 15;
    public TimeSpan Elapsed => TimeSpan.FromSeconds((accumulated + (State == LaneState.Running ? clock() - startedAt : 0)) / (double)frequency);
    public string BaseDisplay => Format(Elapsed);
    public string TotalDisplay => Format(Elapsed + TimeSpan.FromSeconds(PenaltySeconds));
    public string Status => State switch { LaneState.Ready => "待开始", LaneState.Running => "计时中", LaneState.Paused => "已暂停", _ => "已完成" };
    public string PauseLabel => State == LaneState.Paused ? "继续" : "暂停";
    public bool CanPause => State is LaneState.Running or LaneState.Paused;
    public bool CanFinish => CanPause;
    public void Start(long? timestamp = null)
    {
        if (State is not (LaneState.Ready or LaneState.Paused)) return;
        startedAt = timestamp ?? clock(); State = LaneState.Running; Refresh();
    }
    public void Pause(long? timestamp = null)
    {
        if (State != LaneState.Running) return;
        accumulated += (timestamp ?? clock()) - startedAt; State = LaneState.Paused; Refresh();
    }
    public void Finish(long? timestamp = null)
    {
        if (!CanFinish) return;
        if (State == LaneState.Running) accumulated += (timestamp ?? clock()) - startedAt;
        State = LaneState.Finished; Refresh();
    }
    public bool TryFinishFromButton(long receivedAt)
    {
        // Reject packets queued before start/resume and prevent the dispatcher delay being counted.
        if (State != LaneState.Running || receivedAt < startedAt || receivedAt > clock()) return false;
        Finish(receivedAt);
        return true;
    }
    public void AdjustPenalty(int seconds, int delta)
    {
        if (delta is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(delta));
        if (seconds == 5) FiveCount = Math.Max(0, FiveCount + delta);
        else if (seconds == 15) FifteenCount = Math.Max(0, FifteenCount + delta);
        else throw new ArgumentOutOfRangeException(nameof(seconds));
        Refresh();
    }
    public void Reset()
    {
        accumulated = 0; startedAt = 0; FiveCount = 0; FifteenCount = 0; State = LaneState.Ready; Refresh();
    }
    public static string Format(TimeSpan time) => $"{(long)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
    public void Refresh() => Changed(string.Empty);
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
