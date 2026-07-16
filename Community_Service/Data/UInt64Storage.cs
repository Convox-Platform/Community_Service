namespace Community_Service.Data;

/// <summary>
/// PostgreSQL has no unsigned 64-bit integer type. User IDs are stored in BIGINT
/// by preserving their bit pattern and are converted explicitly at the DB boundary.
/// </summary>
public static class UInt64Storage
{
    public static long ToInt64(ulong value) => unchecked((long)value);

    public static ulong ToUInt64(long value) => unchecked((ulong)value);
}
