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

    public AgentChatService(ChatClient chatClient, RosterService rosterService)
    {
        _agent = chatClient.AsAIAgent(
            instructions: SystemPrompt,
            name: "UCLAgent",
            tools: [AIFunctionFactory.Create(rosterService.GetSquad)]);
    }

    public async Task<GenerationResult> GenerateAnswerAsync(
        string question,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken ct = default)
    {
        string userPrompt = BuildUserPrompt(question, chunks);
        var response = await _agent.RunAsync(userPrompt, cancellationToken: ct);

        bool toolCalled = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Any();

        return new GenerationResult(
            Answer: ParseResponse(response.Text ?? "").Answer,
            UsedParagraphRefs: ParseResponse(response.Text ?? "").UsedParagraphRefs,
            UsedTool: toolCalled);
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

    private static (string Answer, IReadOnlyList<string> UsedParagraphRefs) ParseResponse(string raw)
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
            return (answer, refs);
        }
        catch
        {
            return (json, []);
        }
    }
}
