namespace Api.Models;

public record AskResponse(
    string Answer,
    IReadOnlyList<Citation> Citations,
    bool UsedTool,
    string RetrievalMode,
    IReadOnlyList<string> RetrievedParagraphRefs);
