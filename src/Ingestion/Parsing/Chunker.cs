using System.Text;
using System.Text.RegularExpressions;
using Ingestion.Models;

namespace Ingestion.Parsing;

/// <summary>
/// Converts a flat sequence of parsed lines into chunks, one per paragraph.
///
/// Paragraph boundaries:
///   (a) Margin style: single-word line whose LeftX &lt; BodyLeftThreshold and whose
///       text matches the para-id pattern.
///   (b) Inline style: first word of a line matches the para-id pattern.
///
/// Article headers reset the current article number. The title is the first
/// non-empty line after the header that has ≥ 2 words and is not a bare number
/// (bare numbers are page labels or section markers, not titles).
///
/// If a flushed chunk exceeds MaxCharsPerChunk, it is split at word boundaries
/// into consecutive sub-chunks whose paragraph numbers get a "-part1", "-part2",
/// … suffix, so every row in the database retains a paragraph reference.
///
/// No PdfPig dependency — fully unit-testable with synthetic ParsedLine input.
/// </summary>
public class Chunker
{
    private static readonly Regex ParaIdPattern =
        new(@"^(\d+)\.(\d+(?:\([a-z]\))?)$", RegexOptions.Compiled);

    private static readonly Regex ArticleHeaderPattern =
        new(@"^[Aa][Rr][Tt][Ii][Cc][Ll][Ee]\s+(\d+)\b", RegexOptions.Compiled);

    private static readonly Regex BareNumberPattern =
        new(@"^\d+$", RegexOptions.Compiled);

    // TOC entries use underscores or dots as page-number leaders — skip these as titles.
    private static readonly Regex TocLeaderPattern =
        new(@"[_.]{4,}", RegexOptions.Compiled);

    // Trailing page number in TOC lines: "Player lists 32" → strip " 32".
    private static readonly Regex TrailingPageNumber =
        new(@"\s+\d+\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Words whose LeftX is at or below this value (PDF points) are in the left margin.
    /// The body column on UEFA PDFs starts around 72 pt; 60 pt avoids false positives.
    /// </summary>
    public const double BodyLeftThreshold = 60.0;

    /// <summary>
    /// Maximum characters per chunk before splitting. The annex content (club names, codes,
    /// numbers) tokenizes at ~3 chars/token, so we target 5 000 tokens with a 15 000-char
    /// ceiling — comfortably below the text-embedding-3-small limit of 8 192 tokens.
    /// </summary>
    public const int MaxCharsPerChunk = 15_000;

    public IReadOnlyList<Chunk> BuildChunks(IEnumerable<ParsedLine> lines)
    {
        var result = new List<Chunk>();

        string currentArticleNumber = "";
        string currentArticleTitle = "";
        string? currentParaNum = null;
        var currentText = new StringBuilder();
        bool expectingTitle = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
                continue;

            // --- Article header ---
            var articleMatch = ArticleHeaderPattern.Match(line.Text);
            if (articleMatch.Success)
            {
                FlushChunk(result, currentArticleNumber, currentArticleTitle,
                    currentParaNum, currentText);
                currentArticleNumber = articleMatch.Groups[1].Value;

                // The title often appears on the same line: "Article 31 Player lists"
                // Strip any trailing page number (TOC style: "Player lists 32").
                string inline = line.Text[articleMatch.Length..].Trim();
                inline = TrailingPageNumber.Replace(inline, "").Trim();
                currentArticleTitle = TocLeaderPattern.IsMatch(inline) ? "" : inline;

                currentParaNum = null;
                currentText.Clear();
                // Only search next-line title if we didn't get one inline.
                expectingTitle = string.IsNullOrWhiteSpace(currentArticleTitle);
                continue;
            }

            // --- Article title ---
            // Must be the first qualifying line after "Article NN": ≥ 2 words, not a bare number,
            // not itself an article header or paragraph id.
            if (expectingTitle)
            {
                // A valid title is any non-empty text that is not a bare number, not another
                // article header, not a para-id start, and not a TOC leader line (dots/underscores).
                bool isValidTitle = !BareNumberPattern.IsMatch(line.Text.Trim())
                    && !ArticleHeaderPattern.IsMatch(line.Text)
                    && !IsParaId(FirstWord(line.Text))
                    && !TocLeaderPattern.IsMatch(line.Text);

                if (isValidTitle)
                {
                    currentArticleTitle = line.Text.Trim();
                    expectingTitle = false;
                    continue;
                }

                // Not a valid title — keep looking unless this is a paragraph start.
                bool isParagraphStart =
                    (line.WordCount == 1 && line.LeftX < BodyLeftThreshold && IsParaId(line.Text.Trim()))
                    || IsParaId(FirstWord(line.Text));

                if (isParagraphStart)
                    expectingTitle = false;
                // Fall through to paragraph-detection below.
            }

            // --- Margin-style paragraph number ---
            if (line.WordCount == 1
                && line.LeftX < BodyLeftThreshold
                && IsParaId(line.Text.Trim()))
            {
                var (articleNum, paraNum) = SplitParaId(line.Text.Trim());
                if (articleNum == currentArticleNumber)
                {
                    FlushChunk(result, currentArticleNumber, currentArticleTitle,
                        currentParaNum, currentText);
                    currentParaNum = paraNum;
                    currentText.Clear();
                    continue;
                }
            }

            // --- Inline-style paragraph number ---
            string firstWord = FirstWord(line.Text);
            if (IsParaId(firstWord))
            {
                var (articleNum, paraNum) = SplitParaId(firstWord);
                if (articleNum == currentArticleNumber)
                {
                    FlushChunk(result, currentArticleNumber, currentArticleTitle,
                        currentParaNum, currentText);
                    currentParaNum = paraNum;
                    currentText.Clear();
                    string rest = line.Text[firstWord.Length..].TrimStart();
                    if (!string.IsNullOrWhiteSpace(rest))
                        currentText.Append(rest);
                    continue;
                }
            }

            // --- Body text accumulation ---
            if (currentParaNum != null)
            {
                if (currentText.Length > 0)
                    currentText.Append(' ');
                currentText.Append(line.Text.Trim());
            }
        }

        FlushChunk(result, currentArticleNumber, currentArticleTitle,
            currentParaNum, currentText);

        return result;
    }

    private static bool IsParaId(string word) => ParaIdPattern.IsMatch(word);

    private static (string articleNum, string paraNum) SplitParaId(string word)
    {
        var m = ParaIdPattern.Match(word);
        return (m.Groups[1].Value, m.Groups[2].Value);
    }

    private static string FirstWord(string text)
    {
        int i = text.IndexOf(' ');
        return i < 0 ? text.Trim() : text[..i].Trim();
    }

    /// <summary>
    /// Adds the accumulated paragraph to the result list. If the text exceeds
    /// MaxCharsPerChunk, splits at word boundaries and appends "-part1", "-part2", …
    /// to the paragraph number so every row retains a paragraph reference.
    /// </summary>
    private static void FlushChunk(
        List<Chunk> list,
        string articleNumber,
        string articleTitle,
        string? paraNum,
        StringBuilder text)
    {
        if (paraNum == null)
            return;

        string chunkText = text.ToString().Trim();
        if (string.IsNullOrWhiteSpace(chunkText))
            return;

        if (chunkText.Length <= MaxCharsPerChunk)
        {
            list.Add(new Chunk(articleNumber, paraNum, articleTitle, chunkText));
            return;
        }

        // Split into parts at word boundaries.
        var parts = SplitAtWordBoundary(chunkText, MaxCharsPerChunk);
        for (int i = 0; i < parts.Count; i++)
        {
            string partNum = $"{paraNum}-part{i + 1}";
            list.Add(new Chunk(articleNumber, partNum, articleTitle, parts[i]));
        }
    }

    private static List<string> SplitAtWordBoundary(string text, int maxChars)
    {
        var parts = new List<string>();
        int start = 0;

        while (start < text.Length)
        {
            if (start + maxChars >= text.Length)
            {
                parts.Add(text[start..].Trim());
                break;
            }

            // Find the last space at or before the limit.
            int end = start + maxChars;
            int split = text.LastIndexOf(' ', end, end - start);
            if (split <= start)
                split = end; // No space found; hard-cut.

            parts.Add(text[start..split].Trim());
            start = split + 1;
        }

        return parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }
}
