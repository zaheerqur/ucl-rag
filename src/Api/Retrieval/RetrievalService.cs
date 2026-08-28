using Npgsql;
using OpenAI.Embeddings;
using Pgvector;

namespace Api.Retrieval;

/// <summary>
/// Three independently callable retrieval functions.
/// SearchHybrid combines the other two with hand-written RRF.
/// </summary>
public class RetrievalService
{
    private readonly NpgsqlDataSource _db;
    private readonly EmbeddingClient _embeddings;
    private readonly int _rrfK;

    public RetrievalService(NpgsqlDataSource db, EmbeddingClient embeddings, int rrfK)
    {
        _db = db;
        _embeddings = embeddings;
        _rrfK = rrfK;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchDense(
        string query, int k, CancellationToken ct = default)
    {
        var embedding = await EmbedQuery(query, ct);

        const string sql = """
            SELECT id, article_number, paragraph_number, article_title, chunk_text,
                   1 - (embedding <=> $1) AS score
            FROM chunks
            ORDER BY embedding <=> $1
            LIMIT $2
            """;

        return await QueryChunks(sql, [new Vector(embedding), k], ct);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchSparse(
        string query, int k, CancellationToken ct = default)
    {
        // plainto_tsquery produces a strict AND of all content words, which causes
        // 0-hit results when the question vocabulary doesn't perfectly overlap with
        // the chunk text. Convert to OR so every chunk containing ANY query term is
        // ranked; ts_rank then scores by term frequency, giving true BM25-like behaviour.
        const string sql = """
            WITH q AS (
                SELECT to_tsquery('english',
                    replace(plainto_tsquery('english', $1)::text, ' & ', ' | ')) AS query
            )
            SELECT id, article_number, paragraph_number, article_title, chunk_text,
                   ts_rank(text_search, q.query) AS score
            FROM chunks, q
            WHERE text_search @@ q.query
            ORDER BY score DESC
            LIMIT $2
            """;

        return await QueryChunks(sql, [query, k], ct);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchHybrid(
        string query, int k, CancellationToken ct = default)
    {
        var denseTask = SearchDense(query, k, ct);
        var sparseTask = SearchSparse(query, k, ct);
        await Task.WhenAll(denseTask, sparseTask);

        return RrfFusion.Fuse(denseTask.Result, sparseTask.Result, _rrfK, k);
    }

    private async Task<float[]> EmbedQuery(string query, CancellationToken ct)
    {
        var result = await _embeddings.GenerateEmbeddingAsync(query, cancellationToken: ct);
        return result.Value.ToFloats().ToArray();
    }

    private async Task<IReadOnlyList<RetrievedChunk>> QueryChunks(
        string sql, object[] parameters, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters)
            cmd.Parameters.AddWithValue(p);

        var results = new List<RetrievedChunk>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RetrievedChunk(
                Id: reader.GetInt32(0),
                ArticleNumber: reader.GetString(1),
                ParagraphNumber: reader.GetString(2),
                ArticleTitle: reader.GetString(3),
                ChunkText: reader.GetString(4),
                Score: reader.GetDouble(5)));
        }
        return results;
    }
}
