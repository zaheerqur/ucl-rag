using Ingestion.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Ingestion.Parsing;

/// <summary>
/// Opens a PDF with PdfPig, extracts lines with position data, and delegates to Chunker.
/// </summary>
public class PdfParser
{
    // Words within this many points vertically are grouped onto the same line.
    private const double LineYTolerance = 3.0;

    private readonly Chunker _chunker = new();

    public IReadOnlyList<Chunk> Parse(string pdfPath)
    {
        var allLines = new List<ParsedLine>();

        using var document = PdfDocument.Open(pdfPath);
        foreach (var page in document.GetPages())
        {
            allLines.AddRange(ExtractLines(page));
        }

        return _chunker.BuildChunks(allLines);
    }

    private static IEnumerable<ParsedLine> ExtractLines(Page page)
    {
        var words = page.GetWords().ToList();
        if (words.Count == 0)
            yield break;

        // Sort top-to-bottom (descending Y), then left-to-right (ascending X).
        words.Sort((a, b) =>
        {
            double yDiff = b.BoundingBox.Bottom - a.BoundingBox.Bottom;
            if (Math.Abs(yDiff) > LineYTolerance)
                return yDiff > 0 ? 1 : -1;
            return a.BoundingBox.Left.CompareTo(b.BoundingBox.Left);
        });

        var lineWords = new List<Word> { words[0] };
        double currentY = words[0].BoundingBox.Bottom;

        for (int i = 1; i < words.Count; i++)
        {
            double wordY = words[i].BoundingBox.Bottom;
            if (Math.Abs(wordY - currentY) <= LineYTolerance)
            {
                lineWords.Add(words[i]);
            }
            else
            {
                yield return ToLine(lineWords);
                lineWords = [words[i]];
                currentY = wordY;
            }
        }
        yield return ToLine(lineWords);
    }

    private static ParsedLine ToLine(List<Word> words)
    {
        string text = string.Join(" ", words.Select(w => w.Text));
        double leftX = words.Min(w => w.BoundingBox.Left);
        return new ParsedLine(text, leftX, words.Count);
    }
}
