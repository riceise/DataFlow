namespace DataFlow.Domain.Time;

// PostgreSQL stores timestamps with microsecond precision while DateTimeOffset keeps 100-nanosecond ticks.
// Normalizing at the domain boundary keeps an in-memory entity equal to its persisted representation.
public static class PersistenceTime
{
    public static DateTimeOffset ToStoragePrecision(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }
}
