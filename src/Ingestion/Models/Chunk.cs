namespace Ingestion.Models;

public record Chunk(
    string ArticleNumber,
    string ParagraphNumber,
    string ArticleTitle,
    string ChunkText);
