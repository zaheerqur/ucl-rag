namespace Ingestion.Parsing;

/// <summary>
/// A line of text extracted from a PDF page, with its position data.
/// Decoupled from PdfPig types so the chunking logic is unit-testable.
/// </summary>
public record ParsedLine(
    string Text,
    double LeftX,
    int WordCount);
