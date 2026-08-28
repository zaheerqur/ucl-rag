using System.Text;
using System.Text.Json;
using Api.Retrieval;
using OpenAI.Chat;

namespace Api.Generation;

public class AzureChatService : IGenerationService
{
    private readonly ChatClient _client;

    private static readonly ChatCompletionOptions JsonOptions = new()
    {
        ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        Temperature = 0,
        MaxOutputTokenCount = 1024,
    };

    private const string SystemPrompt = """
        You are an assistant that answers questions about the UEFA Champions League regulations.

        Rules:
        - Answer ONLY using the regulation excerpts provided. Do not use any other knowledge.
        - If the answer cannot be found in the provided excerpts, use the exact refusal text below.
        - Every factual claim must come from a cited paragraph.

        Refusal text: "This question cannot be answered based on the UEFA Champions League regulations provided."

        Respond with valid JSON in exactly this format:
        {
          "answer": "<your answer or the refusal text>",
          "usedParagraphs": ["31.04", "31.14(a)"]
        }

        usedParagraphs lists the paragraph references (e.g. "31.04") that directly support the answer.
        Return an empty array when refusing.
        """;

    public AzureChatService(ChatClient client)
    {
        _client = client;
    }

    public async Task<GenerationResult> GenerateAnswerAsync(
        string question,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken ct = default)
    {
        string userPrompt = BuildUserPrompt(question, chunks);

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(SystemPrompt),
            ChatMessage.CreateUserMessage(userPrompt),
        };

        var result = await _client.CompleteChatAsync(messages, JsonOptions, ct);
        string raw = result.Value.Content[0].Text;

        return ParseResponse(raw);
    }

    private static string BuildUserPrompt(string question, IReadOnlyList<RetrievedChunk> chunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Regulation excerpts:");
        sb.AppendLine();
        foreach (var chunk in chunks)
        {
            sb.AppendLine($"[{chunk.ParagraphRef}] {chunk.ArticleTitle}");
            sb.AppendLine(chunk.ChunkText);
            sb.AppendLine();
        }
        sb.AppendLine($"Question: {question}");
        return sb.ToString();
    }

    private static GenerationResult ParseResponse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string answer = root.GetProperty("answer").GetString() ?? "";
            var refs = root.GetProperty("usedParagraphs")
                .EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
            return new GenerationResult(answer, refs);
        }
        catch
        {
            // If the model returns malformed JSON, surface the raw text with no citations.
            return new GenerationResult(raw, []);
        }
    }
}
