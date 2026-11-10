using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Api.Retrieval;
using Api.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Api.Generation;

public class AgentChatService : IGenerationService
{
    private readonly AIAgent _agent;
    private readonly ChatOptions _chatOptions;
    private readonly int _contextWindowTokens;

    private const string SystemPrompt = """
        You are an assistant that answers questions about the UEFA Champions League regulations.
        You have access to a GetSquad tool that returns squad data for a club.
        Call GetSquad ONLY when the question requires actual squad data for a specific club
        (for example, to check a club's compliance with a player eligibility rule).
        Do NOT call GetSquad for pure rules questions that do not reference a specific club's players.

        Rules:
        - Answer ONLY using the regulation excerpts provided and any squad data returned by the tool.
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

    public AgentChatService(
        ChatClient chatClient,
        RosterService rosterService,
        double temperature = 0,
        int maxOutputTokens = 1024,
        int contextWindowTokens = 8192)
    {
        _agent = chatClient.AsAIAgent(
            instructions: SystemPrompt,
            name: "UCLAgent",
            tools: [AIFunctionFactory.Create(rosterService.GetSquad)]);
        _chatOptions = new ChatOptions
        {
            Temperature = (float)temperature,
            MaxOutputTokens = maxOutputTokens,
        };
        _contextWindowTokens = contextWindowTokens;
    }

    public async Task<GenerationResult> GenerateAnswerAsync(
        string question,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken ct = default)
    {
        string userPrompt = BuildUserPrompt(question, chunks);
        WarnIfPromptMayExceedContext(userPrompt);

        try
        {
            var response = await _agent.RunAsync(
                userPrompt,
                options: new ChatClientAgentRunOptions { ChatOptions = _chatOptions },
                cancellationToken: ct);

            bool toolCalled = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Any();

            var (answer, refs, parseFailed) = ParseResponse(response.Text ?? "");

            return new GenerationResult(
                Answer: answer,
                UsedParagraphRefs: refs,
                UsedTool: toolCalled,
                ParseFailed: parseFailed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A tool-call error or a transport failure (e.g. the model doesn't support
            // tool calling) lands here. Report it as a failed question instead of a 500.
            return new GenerationResult(
                Answer: $"[generation error: {ex.Message}]",
                UsedParagraphRefs: [],
                UsedTool: false,
                ParseFailed: true);
        }
    }

    private void WarnIfPromptMayExceedContext(string userPrompt)
    {
        // Rough estimate: ~4 chars/token. Only meaningful as a heads-up for small-context
        // local models; Azure deployments have far more headroom than this ever flags.
        int estimatedTokens = (SystemPrompt.Length + userPrompt.Length) / 4;
        int budget = (int)(_contextWindowTokens * 0.8);
        if (estimatedTokens > budget)
        {
            Console.Error.WriteLine(
                $"WARNING: estimated prompt size ~{estimatedTokens} tokens exceeds 80% of the " +
                $"configured context window ({_contextWindowTokens}). The retrieved paragraphs " +
                "may be silently truncated by the model. Raise Generation:ContextWindowTokens " +
                "and OLLAMA_CONTEXT_LENGTH together.");
        }
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

    private static (string Answer, IReadOnlyList<string> UsedParagraphRefs, bool ParseFailed) ParseResponse(string raw)
    {
        // Strip markdown code fences the model sometimes wraps around JSON.
        string json = raw.Trim();
        if (json.StartsWith("```"))
        {
            int firstNewline = json.IndexOf('\n');
            int lastFence = json.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                json = json[(firstNewline + 1)..lastFence].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string answer = root.GetProperty("answer").GetString() ?? "";
            var refs = root.GetProperty("usedParagraphs")
                .EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
            return (answer, refs, false);
        }
        catch
        {
            return (json, [], true);
        }
    }
}
