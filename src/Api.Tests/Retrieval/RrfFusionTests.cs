using Api.Retrieval;

namespace Api.Tests.Retrieval;

public class RrfFusionTests
{
    // Build a minimal RetrievedChunk for ranking tests (Score is ignored as input).
    private static RetrievedChunk C(int id) =>
        new(id, id.ToString(), "01", "Title", "Text", 0.0);

    /// <summary>
    /// Hand-computed expected order with k=60.
    ///
    /// Dense:  [A=1, B=2, C=3, D=4]
    /// Sparse: [C=1, A=2, D=3, B=4]
    ///
    /// RRF scores (k=60):
    ///   A: 1/61 + 1/62 = 0.032522
    ///   C: 1/63 + 1/61 = 0.032266
    ///   B: 1/62 + 1/64 = 0.031754
    ///   D: 1/64 + 1/63 = 0.031498
    ///
    /// Expected fused order: A, C, B, D
    /// This differs from both input lists, confirming fusion is working.
    /// </summary>
    [Fact]
    public void FusedOrder_MatchesHandComputedResult()
    {
        int a = 1, b = 2, c = 3, d = 4;

        var dense  = new[] { C(a), C(b), C(c), C(d) };
        var sparse = new[] { C(c), C(a), C(d), C(b) };

        var fused = RrfFusion.Fuse(dense, sparse, rrfK: 60, topK: 4);

        Assert.Equal(4, fused.Count);
        Assert.Equal(a, fused[0].Id); // A ranked 1st
        Assert.Equal(c, fused[1].Id); // C ranked 2nd
        Assert.Equal(b, fused[2].Id); // B ranked 3rd
        Assert.Equal(d, fused[3].Id); // D ranked 4th
    }

    [Fact]
    public void FusedScores_ArePositive()
    {
        var dense  = new[] { C(1), C(2) };
        var sparse = new[] { C(2), C(1) };

        var fused = RrfFusion.Fuse(dense, sparse, rrfK: 60, topK: 2);

        Assert.All(fused, chunk => Assert.True(chunk.Score > 0));
    }

    [Fact]
    public void TopK_LimitsResultCount()
    {
        var list = Enumerable.Range(1, 10).Select(C).ToList();

        var fused = RrfFusion.Fuse(list, list, rrfK: 60, topK: 5);

        Assert.Equal(5, fused.Count);
    }

    [Fact]
    public void DocumentOnlyInOneList_StillAppears()
    {
        // chunk 99 is only in sparse — it should still appear in fused output
        var dense  = new[] { C(1), C(2) };
        var sparse = new[] { C(99), C(1) };

        var fused = RrfFusion.Fuse(dense, sparse, rrfK: 60, topK: 10);

        Assert.Contains(fused, c => c.Id == 99);
    }

    [Fact]
    public void DocumentTopInBothLists_RanksFirst()
    {
        // chunk 7 is top in both lists — it must come first
        var dense  = new[] { C(7), C(1), C(2) };
        var sparse = new[] { C(7), C(3), C(4) };

        var fused = RrfFusion.Fuse(dense, sparse, rrfK: 60, topK: 5);

        Assert.Equal(7, fused[0].Id);
    }
}
