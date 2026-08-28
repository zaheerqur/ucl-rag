namespace Api.Retrieval;

public record RetrievedChunk(
    int Id,
    string ArticleNumber,
    string ParagraphNumber,
    string ArticleTitle,
    string ChunkText,
    double Score)
{
    /// <summary>Combined reference used for citation matching: "31.14(a)".</summary>
    public string ParagraphRef => $"{ArticleNumber}.{ParagraphNumber}";
}
