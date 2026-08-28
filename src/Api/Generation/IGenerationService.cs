using Api.Retrieval;

namespace Api.Generation;

public interface IGenerationService
{
    Task<GenerationResult> GenerateAnswerAsync(
        string question,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken ct = default);
}

public record GenerationResult(string Answer, IReadOnlyList<string> UsedParagraphRefs);
