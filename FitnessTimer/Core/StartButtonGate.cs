namespace FitnessTimer.Core;

// Timestamp and generation checks keep setup presses and old queued events from starting a heat.
public sealed class StartButtonGate(long frequency)
{
    private bool armed;
    private long armedAt, generation, lastAccepted=long.MinValue;
    public long Generation=>Interlocked.Read(ref generation);
    public void SetArmed(bool value,long now)
    {
        if(armed==value)return;
        armed=value;armedAt=now;Interlocked.Increment(ref generation);
    }
    public bool TryAccept(long receivedAt,long capturedGeneration,long now)
    {
        if(!armed||capturedGeneration!=Generation||receivedAt<armedAt||receivedAt>now)return false;
        if(lastAccepted!=long.MinValue&&receivedAt-lastAccepted<frequency/2)return false;
        lastAccepted=receivedAt;SetArmed(false,now);return true;
    }
}
