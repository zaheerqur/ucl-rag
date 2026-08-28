using Ingestion.Parsing;

namespace Api.Tests.Parsing;

public class ChunkerTests
{
    private readonly Chunker _chunker = new();

    // Helper: margin-style paragraph-number line (single word, left of body column).
    private static ParsedLine MarginLine(string paraId) =>
        new(paraId, Chunker.BodyLeftThreshold - 1, 1);

    // Helper: body-text line at the body column.
    private static ParsedLine BodyLine(string text) =>
        new(text, Chunker.BodyLeftThreshold + 10, text.Split(' ').Length);

    // Helper: inline-style paragraph-number line (para-id is the first word).
    private static ParsedLine InlineLine(string paraId, string text) =>
        new($"{paraId} {text}", Chunker.BodyLeftThreshold + 10, text.Split(' ').Length + 1);

    // Helper: "Article NN" header line — 2 words, at body column.
    private static ParsedLine ArticleLine(string n) =>
        new($"Article {n}", Chunker.BodyLeftThreshold + 10, 2);

    // Helper: multi-word title line.
    private static ParsedLine TitleLine(string title) =>
        new(title, Chunker.BodyLeftThreshold + 10, title.Split(' ').Length);

    // Helper: bare number line (page label — should NOT be treated as a title).
    private static ParsedLine BareNumberLine(string n) =>
        new(n, Chunker.BodyLeftThreshold + 10, 1);

    // ------------------------------------------------------------------
    // Basic margin-style parsing
    // ------------------------------------------------------------------

    [Fact]
    public void MarginStyle_BasicParagraph_ProducesChunk()
    {
        var lines = new[]
        {
            ArticleLine("31"),
            TitleLine("Locally Trained Players"),
            MarginLine("31.04"),
            BodyLine("Each club must include a minimum of eight locally trained players."),
        };

        var chunks = _chunker.BuildChunks(lines);

        Assert.Single(chunks);
        Assert.Equal("31", chunks[0].ArticleNumber);
        Assert.Equal("04", chunks[0].ParagraphNumber);
        Assert.Contains("locally trained players", chunks[0].ChunkText, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Paragraph number preserved across a simulated page boundary
    // ------------------------------------------------------------------

    [Fact]
    public void MarginStyle_ParagraphTextSpansPageBoundary_SingleChunk()
    {
        var lines = new[]
        {
            ArticleLine("31"),
            TitleLine("Locally Trained Players"),
            MarginLine("31.04"),
            BodyLine("Each club must include a minimum"),   // page 1
            BodyLine("of eight locally trained players."), // page 2 continuation
            MarginLine("31.05"),
            BodyLine("A locally trained player is defined as follows."),
        };

        var chunks = _chunker.BuildChunks(lines);

        Assert.Equal(2, chunks.Count);

        var chunk04 = chunks.Single(c => c.ParagraphNumber == "04");
        Assert.Contains("minimum", chunk04.ChunkText);
        Assert.Contains("locally trained players", chunk04.ChunkText, StringComparison.OrdinalIgnoreCase);

        var chunk05 = chunks.Single(c => c.ParagraphNumber == "05");
        Assert.Contains("defined", chunk05.ChunkText, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Inline-style parsing
    // ------------------------------------------------------------------

    [Fact]
    public void InlineStyle_BasicParagraph_ProducesChunk()
    {
        var lines = new[]
        {
            ArticleLine("40"),
            TitleLine("Financial Fair Play"),
            InlineLine("40.02", "Each club shall break even over the assessment period."),
        };

        var chunks = _chunker.BuildChunks(lines);

        Assert.Single(chunks);
        Assert.Equal("40", chunks[0].ArticleNumber);
        Assert.Equal("02", chunks[0].ParagraphNumber);
        Assert.Contains("break even", chunks[0].ChunkText);
    }

    // ------------------------------------------------------------------
    // Lettered sub-items produce distinguishable chunks with their full id
    // ------------------------------------------------------------------

    [Fact]
    public void LetteredSubItem_IsStoredWithFullIdentifier()
    {
        var lines = new[]
        {
            ArticleLine("31"),
            TitleLine("Locally Trained Players"),
            MarginLine("31.14"),
            BodyLine("The squad list shall include:"),
            MarginLine("31.14(a)"),
            BodyLine("the names of all A-list players;"),
            MarginLine("31.14(b)"),
            BodyLine("the names of all B-list players;"),
        };

        var chunks = _chunker.BuildChunks(lines);

        Assert.Equal(3, chunks.Count);
        Assert.Contains(chunks, c => c.ParagraphNumber == "14");
        Assert.Contains(chunks, c => c.ParagraphNumber == "14(a)");
        Assert.Contains(chunks, c => c.ParagraphNumber == "14(b)");
    }

    // ------------------------------------------------------------------
    // Multiple articles produce correctly attributed chunks
    // ------------------------------------------------------------------

    [Fact]
    public void MultipleArticles_ChunksAttributedToCorrectArticle()
    {
        var lines = new[]
        {
            ArticleLine("6"),
            TitleLine("Participation"),
            MarginLine("6.01"),
            BodyLine("Only clubs admitted by UEFA may participate."),
            ArticleLine("31"),
            TitleLine("Locally Trained Players"),
            MarginLine("31.04"),
            BodyLine("Eight locally trained players required."),
        };

        var chunks = _chunker.BuildChunks(lines);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("6", chunks[0].ArticleNumber);
        Assert.Equal("31", chunks[1].ArticleNumber);
    }

    // ------------------------------------------------------------------
    // Article title captured correctly
    // ------------------------------------------------------------------

    [Fact]
    public void ArticleTitle_CapturedFromLineAfterHeader()
    {
        var lines = new[]
        {
            ArticleLine("18"),
            TitleLine("Registration of Players"),
            MarginLine("18.01"),
            BodyLine("Every player must be registered."),
        };

        var chunks = _chunker.BuildChunks(lines);

        Assert.Single(chunks);
        Assert.Equal("Registration of Players", chunks[0].ArticleTitle);
    }

    // ------------------------------------------------------------------
    // Bare-number lines (page labels) are NOT captured as article titles
    // ------------------------------------------------------------------

    [Fact]
    public void BareNumberLine_AfterArticleHeader_IsSkippedAsTitle()
    {
        var lines = new[]
        {
            ArticleLine("5"),
            BareNumberLine("2"),            // page number — not a title
            TitleLine("Eligibility of Clubs"),
            MarginLine("5.01"),
            BodyLine("Only eligible clubs may enter."),
        };

        var chunks = _chunker.BuildChunks(lines);

        Assert.Single(chunks);
        Assert.Equal("Eligibility of Clubs", chunks[0].ArticleTitle);
    }

    // ------------------------------------------------------------------
    // Oversized chunks are split with a paragraph reference on every part
    // ------------------------------------------------------------------

    [Fact]
    public void OversizedChunk_IsSplitIntoPartsWithParagraphReference()
    {
        // Build a text that is 1.5× MaxCharsPerChunk.
        string longText = string.Join(" ", Enumerable.Repeat("word", Chunker.MaxCharsPerChunk / 5 + 1));

        var lines = new List<ParsedLine>
        {
            ArticleLine("96"),
            TitleLine("Administrative Provisions"),
            MarginLine("96.01"),
        };
        // Feed the long text as multiple body lines.
        foreach (var segment in longText.Chunk(200).Select(c => new string(c)))
            lines.Add(BodyLine(segment));

        var chunks = _chunker.BuildChunks(lines);

        // Must have produced ≥ 2 chunks, all with paragraph references.
        Assert.True(chunks.Count >= 2, $"Expected ≥ 2 chunks, got {chunks.Count}");
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.ParagraphNumber)));
        Assert.All(chunks, c => Assert.Equal("96", c.ArticleNumber));
        Assert.Contains(chunks, c => c.ParagraphNumber.StartsWith("01-part"));
    }

    // ------------------------------------------------------------------
    // Zero null/empty paragraph numbers across all chunks
    // ------------------------------------------------------------------

    [Fact]
    public void AllChunks_HaveNonEmptyParagraphNumber()
    {
        var lines = new[]
        {
            ArticleLine("6"),
            TitleLine("Participation"),
            MarginLine("6.01"),
            BodyLine("Text for 6.01."),
            InlineLine("6.02", "Text for 6.02."),
        };

        var chunks = _chunker.BuildChunks(lines);

        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.ParagraphNumber)));
    }
}
