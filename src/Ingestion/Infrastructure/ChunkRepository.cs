using Ingestion.Models;
using Npgsql;
using Pgvector;

namespace Ingestion.Infrastructure;

/// <summary>
/// Inserts chunks into the database.
/// Upserts on (article_number, paragraph_number) so re-running ingestion is idempotent.
/// </summary>
public class ChunkRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ChunkRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(Chunk chunk, float[] embedding, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO chunks (article_number, paragraph_number, article_title, chunk_text, embedding)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (article_number, paragraph_number)
            DO UPDATE SET
                article_title   = EXCLUDED.article_title,
                chunk_text      = EXCLUDED.chunk_text,
                embedding       = EXCLUDED.embedding
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(chunk.ArticleNumber);
        cmd.Parameters.AddWithValue(chunk.ParagraphNumber);
        cmd.Parameters.AddWithValue(chunk.ArticleTitle);
        cmd.Parameters.AddWithValue(chunk.ChunkText);
        cmd.Parameters.AddWithValue(new Vector(embedding));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM chunks", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> CountNullParagraphAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM chunks WHERE paragraph_number IS NULL OR paragraph_number = ''", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
