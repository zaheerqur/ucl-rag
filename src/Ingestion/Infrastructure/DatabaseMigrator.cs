using Npgsql;

namespace Ingestion.Infrastructure;

/// <summary>
/// Runs every SQL file in /db/migrations in filename order.
/// Each file is executed as a single statement batch; re-running is safe because
/// all DDL uses IF NOT EXISTS guards.
/// </summary>
public class DatabaseMigrator
{
    private readonly string _connectionString;
    private readonly string _migrationsDir;

    public DatabaseMigrator(string connectionString, string migrationsDir)
    {
        _connectionString = connectionString;
        _migrationsDir = migrationsDir;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_migrationsDir))
            throw new DirectoryNotFoundException($"Migrations directory not found: {_migrationsDir}");

        var files = Directory.GetFiles(_migrationsDir, "*.sql")
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file, ct);
            // Split on semicolons so each DDL statement runs independently.
            var statements = sql
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            foreach (var statement in statements)
            {
                await using var cmd = new NpgsqlCommand(statement, conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            Console.WriteLine($"  Applied migration: {Path.GetFileName(file)}");
        }
    }
}
