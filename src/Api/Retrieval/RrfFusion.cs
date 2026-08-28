namespace Api.Retrieval;

/// <summary>
/// Hand-written Reciprocal Rank Fusion.
///
/// score(d) = sum_i( 1 / (RrfK + rank_i(d)) )
///
/// where rank_i(d) is the 1-based position of chunk d in list i.
/// Documents absent from a list contribute 0 from that list.
/// RrfK is a named constant supplied by the caller (from configuration).
/// </summary>
public static class RrfFusion
{
    public static IReadOnlyList<RetrievedChunk> Fuse(
        IReadOnlyList<RetrievedChunk> denseResults,
        IReadOnlyList<RetrievedChunk> sparseResults,
        int rrfK,
        int topK)
    {
        var scores = new Dictionary<int, double>();
        var lookup = new Dictionary<int, RetrievedChunk>();

        AddRanks(denseResults, rrfK, scores, lookup);
        AddRanks(sparseResults, rrfK, scores, lookup);

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => lookup[kv.Key] with { Score = kv.Value })
            .ToList();
    }

    private static void AddRanks(
        IReadOnlyList<RetrievedChunk> list,
        int rrfK,
        Dictionary<int, double> scores,
        Dictionary<int, RetrievedChunk> lookup)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var chunk = list[i];
            int rank = i + 1;
            scores[chunk.Id] = scores.GetValueOrDefault(chunk.Id) + 1.0 / (rrfK + rank);
            lookup.TryAdd(chunk.Id, chunk);
        }
    }
}
