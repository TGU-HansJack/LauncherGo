using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LauncherGo.Services;

internal sealed class RobotPlayerBindingStore : IDisposable
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public RobotPlayerBindingStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS robot_player_binding_pending (
                qq_user_id INTEGER PRIMARY KEY,
                group_id INTEGER NOT NULL,
                profile_id TEXT NOT NULL,
                player_uid TEXT NOT NULL,
                player_name TEXT NOT NULL COLLATE NOCASE,
                created_at_utc INTEGER NOT NULL,
                expires_at_utc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS robot_player_bindings (
                qq_user_id INTEGER PRIMARY KEY,
                group_id INTEGER NOT NULL,
                profile_id TEXT NOT NULL,
                player_uid TEXT NOT NULL,
                player_name TEXT NOT NULL COLLATE NOCASE,
                bound_at_utc INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_robot_player_binding_uid
                ON robot_player_bindings(profile_id, player_uid)
                WHERE player_uid <> '';
            CREATE UNIQUE INDEX IF NOT EXISTS ux_robot_player_binding_name
                ON robot_player_bindings(profile_id, player_name COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
    }

    public void CreatePending(
        long qqUserId,
        long groupId,
        string profileId,
        string playerUid,
        string playerName,
        DateTimeOffset? now = null)
    {
        var createdAt = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            ThrowIfDisposed();
            DeleteExpiredPendingUnsafe(createdAt);
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO robot_player_binding_pending
                    (qq_user_id, group_id, profile_id, player_uid, player_name, created_at_utc, expires_at_utc)
                VALUES ($qq, $group, $profile, $uid, $name, $created, $expires)
                ON CONFLICT(qq_user_id) DO UPDATE SET
                    group_id = excluded.group_id,
                    profile_id = excluded.profile_id,
                    player_uid = excluded.player_uid,
                    player_name = excluded.player_name,
                    created_at_utc = excluded.created_at_utc,
                    expires_at_utc = excluded.expires_at_utc;
                """;
            command.Parameters.AddWithValue("$qq", qqUserId);
            command.Parameters.AddWithValue("$group", groupId);
            command.Parameters.AddWithValue("$profile", profileId.Trim());
            command.Parameters.AddWithValue("$uid", playerUid.Trim());
            command.Parameters.AddWithValue("$name", playerName.Trim());
            command.Parameters.AddWithValue("$created", createdAt.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$expires", createdAt.Add(PendingLifetime).ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
    }

    public RobotPlayerBinding? TryComplete(
        string profileId,
        string playerUid,
        string playerName,
        string verificationText,
        DateTimeOffset? now = null)
    {
        var verifiedAt = now ?? DateTimeOffset.UtcNow;
        if (!TryParseQqNumber(verificationText, out var qqUserId)) return null;
        lock (_gate)
        {
            ThrowIfDisposed();
            DeleteExpiredPendingUnsafe(verifiedAt);
            using var transaction = _connection.BeginTransaction();
            RobotPendingPlayerBinding? pending;
            using (var select = _connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT qq_user_id, group_id, profile_id, player_uid, player_name, created_at_utc, expires_at_utc
                    FROM robot_player_binding_pending
                    WHERE qq_user_id = $qq AND profile_id = $profile;
                    """;
                select.Parameters.AddWithValue("$qq", qqUserId);
                select.Parameters.AddWithValue("$profile", profileId.Trim());
                using var reader = select.ExecuteReader();
                pending = reader.Read() ? ReadPending(reader) : null;
            }

            if (pending is null || !MatchesPlayer(pending, playerUid, playerName))
            {
                transaction.Rollback();
                return null;
            }

            using (var removeExisting = _connection.CreateCommand())
            {
                removeExisting.Transaction = transaction;
                removeExisting.CommandText = """
                    DELETE FROM robot_player_bindings
                    WHERE qq_user_id = $qq
                       OR (profile_id = $profile AND player_name = $name COLLATE NOCASE)
                       OR (profile_id = $profile AND $uid <> '' AND player_uid = $uid);
                    """;
                removeExisting.Parameters.AddWithValue("$qq", qqUserId);
                removeExisting.Parameters.AddWithValue("$profile", pending.ProfileId);
                removeExisting.Parameters.AddWithValue("$uid", pending.PlayerUid);
                removeExisting.Parameters.AddWithValue("$name", pending.PlayerName);
                removeExisting.ExecuteNonQuery();
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO robot_player_bindings
                        (qq_user_id, group_id, profile_id, player_uid, player_name, bound_at_utc)
                    VALUES ($qq, $group, $profile, $uid, $name, $bound);
                    DELETE FROM robot_player_binding_pending WHERE qq_user_id = $qq;
                    """;
                insert.Parameters.AddWithValue("$qq", qqUserId);
                insert.Parameters.AddWithValue("$group", pending.GroupId);
                insert.Parameters.AddWithValue("$profile", pending.ProfileId);
                insert.Parameters.AddWithValue("$uid", pending.PlayerUid);
                insert.Parameters.AddWithValue("$name", pending.PlayerName);
                insert.Parameters.AddWithValue("$bound", verifiedAt.ToUnixTimeSeconds());
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return new RobotPlayerBinding(
                qqUserId,
                pending.GroupId,
                pending.ProfileId,
                pending.PlayerUid,
                pending.PlayerName,
                verifiedAt);
        }
    }

    public RobotPlayerBinding? GetBinding(long qqUserId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT qq_user_id, group_id, profile_id, player_uid, player_name, bound_at_utc
                FROM robot_player_bindings WHERE qq_user_id = $qq;
                """;
            command.Parameters.AddWithValue("$qq", qqUserId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new RobotPlayerBinding(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)))
                : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    private static bool TryParseQqNumber(string value, out long qqUserId)
    {
        qqUserId = 0;
        var normalized = value.Trim();
        return normalized.Length is >= 5 and <= 20 &&
               normalized.All(char.IsAsciiDigit) &&
               long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out qqUserId) &&
               qqUserId > 0;
    }

    private static bool MatchesPlayer(RobotPendingPlayerBinding pending, string playerUid, string playerName) =>
        (!string.IsNullOrWhiteSpace(pending.PlayerUid) &&
         string.Equals(pending.PlayerUid, playerUid?.Trim(), StringComparison.Ordinal)) ||
        string.Equals(pending.PlayerName, playerName?.Trim(), StringComparison.OrdinalIgnoreCase);

    private void DeleteExpiredPendingUnsafe(DateTimeOffset now)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM robot_player_binding_pending WHERE expires_at_utc < $now;";
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    private static RobotPendingPlayerBinding ReadPending(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed record RobotPendingPlayerBinding(
    long QqUserId,
    long GroupId,
    string ProfileId,
    string PlayerUid,
    string PlayerName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed record RobotPlayerBinding(
    long QqUserId,
    long GroupId,
    string ProfileId,
    string PlayerUid,
    string PlayerName,
    DateTimeOffset BoundAtUtc);
