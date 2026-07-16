namespace Community_Service.Ids;

/// <summary>
/// Generates positive 64-bit Snowflake IDs:
/// 41 bits timestamp, 10 bits worker ID and 12 bits sequence.
/// </summary>
public sealed class SnowflakeIdGenerator
{
    private const int WorkerIdBits = 10;
    private const int SequenceBits = 12;
    private const int WorkerIdShift = SequenceBits;
    private const int TimestampShift = WorkerIdBits + SequenceBits;
    private const int SequenceMask = (1 << SequenceBits) - 1;
    private const long MaxTimestamp = (1L << 41) - 1;

    // 2025-01-01T00:00:00Z. With 41 timestamp bits IDs remain valid until 2094.
    private const long EpochMilliseconds = 1_735_689_600_000L;

    private readonly object _sync = new();
    private readonly int _workerId;
    private readonly TimeProvider _timeProvider;
    private long _lastTimestamp = -1;
    private int _sequence;

    public const int MaxWorkerId = (1 << WorkerIdBits) - 1;

    public SnowflakeIdGenerator(int workerId, TimeProvider? timeProvider = null)
    {
        if (workerId is < 0 or > MaxWorkerId)
            throw new ArgumentOutOfRangeException(
                nameof(workerId), workerId, $"Worker ID must be between 0 and {MaxWorkerId}.");

        _workerId = workerId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public long NextId()
    {
        lock (_sync)
        {
            var timestamp = CurrentTimestamp();
            if (timestamp < _lastTimestamp)
                throw new InvalidOperationException("System clock moved backwards; Snowflake ID cannot be generated safely.");

            if (timestamp == _lastTimestamp)
            {
                _sequence = (_sequence + 1) & SequenceMask;
                if (_sequence == 0)
                    timestamp = WaitForNextMillisecond(timestamp);
            }
            else
            {
                _sequence = 0;
            }

            if (timestamp > MaxTimestamp)
                throw new InvalidOperationException("Snowflake timestamp range has been exhausted.");

            _lastTimestamp = timestamp;
            return (timestamp << TimestampShift) | ((long)_workerId << WorkerIdShift) | (uint)_sequence;
        }
    }

    private long WaitForNextMillisecond(long timestamp)
    {
        var current = timestamp;
        while (current <= timestamp)
        {
            Thread.Yield();
            current = CurrentTimestamp();
            if (current < timestamp)
                throw new InvalidOperationException("System clock moved backwards; Snowflake ID cannot be generated safely.");
        }

        return current;
    }

    private long CurrentTimestamp()
    {
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() - EpochMilliseconds;
        if (timestamp < 0)
            throw new InvalidOperationException("System time is earlier than the Snowflake epoch.");
        return timestamp;
    }
}
