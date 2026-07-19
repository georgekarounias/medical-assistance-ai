using System.Buffers.Binary;
using Npgsql;

namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>
/// A Postgres session-level advisory lock, held until the handle is disposed.
///
/// Two things in this service need one instance to act while the others stand
/// back — applying migrations, and running an ingestion — and both need the
/// claim to survive the claimant being killed. An advisory lock belongs to the
/// database session, so a process that dies releases everything it held the
/// moment its connection drops. That is what makes it safe here where a lease
/// column would need a heartbeat, a timeout, and an answer to what happens when
/// the timeout is wrong.
/// </summary>
public sealed class PostgresAdvisoryLock : IAsyncDisposable
{
    /// <summary>
    /// Identifies the schema-migration lock. Any fixed value works — it only has
    /// to be the same one in every instance.
    ///
    /// This is the single-key space; per-ingestion locks use the two-key space,
    /// and Postgres keeps the two spaces separate, so no ingestion can ever
    /// collide with the migration lock however its key happens to fold.
    /// </summary>
    public const long SchemaMigrationKey = 6_941_233_071_002;

    private readonly NpgsqlConnection _connection;
    private readonly string _unlockSql;
    private readonly object[] _keys;

    private PostgresAdvisoryLock(NpgsqlConnection connection, string unlockSql, object[] keys)
    {
        _connection = connection;
        _unlockSql = unlockSql;
        _keys = keys;
    }

    /// <summary>
    /// Waits for the single-key lock and returns the handle that releases it.
    /// Blocks for as long as the holder takes: an instance arriving mid-migration
    /// should wait for the schema to be ready, not give up and serve against half
    /// of it.
    /// </summary>
    public static async Task<PostgresAdvisoryLock> AcquireAsync(
        NpgsqlConnection connection, long key, CancellationToken ct = default)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection);
        command.Parameters.AddWithValue(key);
        await command.ExecuteNonQueryAsync(ct);
        return new PostgresAdvisoryLock(connection, "SELECT pg_advisory_unlock($1)", [key]);
    }

    /// <summary>
    /// Takes the two-key lock if it is free, and returns null if someone else
    /// holds it. Never waits: the caller is asking whether this work is already
    /// someone's, and the answer "yes" means put it down, not queue behind them.
    /// </summary>
    public static async Task<PostgresAdvisoryLock?> TryAcquireAsync(
        NpgsqlConnection connection, (int High, int Low) key, CancellationToken ct = default)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1, $2)", connection);
        command.Parameters.AddWithValue(key.High);
        command.Parameters.AddWithValue(key.Low);

        var acquired = (bool)(await command.ExecuteScalarAsync(ct))!;
        return acquired
            ? new PostgresAdvisoryLock(connection, "SELECT pg_advisory_unlock($1, $2)", [key.High, key.Low])
            : null;
    }

    /// <summary>
    /// Folds an id into the two-key space, losslessly enough that two different
    /// ingestions colliding is not a practical concern — which matters, because
    /// a collision would look exactly like "someone else is running this" and the
    /// loser would be put down rather than processed.
    /// </summary>
    public static (int High, int Low) KeyFor(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var folded = BinaryPrimitives.ReadInt64LittleEndian(bytes)
                     ^ BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        return ((int)(folded >> 32), unchecked((int)folded));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await using var command = new NpgsqlCommand(_unlockSql, _connection);
        foreach (var key in _keys)
            command.Parameters.AddWithValue(key);
        await command.ExecuteNonQueryAsync();
    }
}
