namespace PhotoAIFactory.Simulation.Tests.Simulation;

internal sealed class DeterministicTimeProvider : TimeProvider
{
    private readonly object sync = new();
    private DateTimeOffset utcNow;

    public DeterministicTimeProvider(DateTimeOffset initialUtc)
    {
        if (initialUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Simulation time must be UTC.", nameof(initialUtc));
        }

        utcNow = initialUtc;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (sync)
        {
            return utcNow;
        }
    }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Simulation time cannot move backwards.");
        }

        lock (sync)
        {
            utcNow = utcNow.Add(delta);
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Simulation time must be UTC.", nameof(value));
        }

        lock (sync)
        {
            if (value < utcNow)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Simulation time cannot move backwards.");
            }

            utcNow = value;
        }
    }
}
