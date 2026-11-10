using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;

// ---------------------------------------------------------------------------
// Config
// ---------------------------------------------------------------------------
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets("ucl-rag-api", reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

string apiBaseUrl   = config["Eval:ApiBaseUrl"]   ?? "http://localhost:5000";
string questionsPath = config["Eval:QuestionsPath"] ?? "eval/questions.json";
string resultsDir   = config["Eval:ResultsDir"]   ?? "eval/results";
int    topK         = int.Parse(config["Eval:TopK"] ?? "10");
int    rrfK         = int.Parse(config["Eval:RrfK"] ?? "60");
int    timeoutSeconds = int.Parse(config["Eval:TimeoutSeconds"] ?? "600");
string? modelLabel  = config["Eval:ModelLabel"];

// The judge always runs on Azure, regardless of which provider answered the question,
// so every model in the comparison is graded by the same reference model.
string azureEndpoint = config["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint (needed for the judge model).");
string azureApiKey = config["AzureOpenAI:ApiKey"]
    ?? throw new InvalidOperationException(
        "Missing AzureOpenAI:ApiKey. Set with: dotnet user-secrets set \"AzureOpenAI:ApiKey\" \"<key>\" --project ../Api");
string judgeDeployment = config["Judge:Deployment"] ?? "gpt-4o";

ChatClient judgeClient = new AzureOpenAIClient(new Uri(azureEndpoint), new AzureKeyCredential(azureApiKey))
    .GetChatClient(judgeDeployment);

// Resolve questionsPath relative to working directory or walk up to repo root
questionsPath = ResolveFromRoot(questionsPath) ?? questionsPath;
resultsDir    = ResolveFromRoot(resultsDir)    ?? resultsDir;

// ---------------------------------------------------------------------------
// Load questions
// ---------------------------------------------------------------------------
if (!File.Exists(questionsPath))
{
    Console.Error.WriteLine($"Questions file not found: {questionsPath}");
    Console.Error.WriteLine("Set Eval:QuestionsPath in appsettings.json or pass --Eval:QuestionsPath=<path>");
    return 1;
}

var jsonOpts = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// The file may be either a flat array OR { "_readme":…, "answerable":[…], "unanswerable":[…] }.
List<Question> questions;
{
    var raw = await File.ReadAllTextAsync(questionsPath);
    using var doc = JsonDocument.Parse(raw);
    if (doc.RootElement.ValueKind == JsonValueKind.Array)
    {
        questions = JsonSerializer.Deserialize<List<QuestionDto>>(raw, jsonOpts)!
            .Select(q => q.ToQuestion(q.Answerable ?? true)).ToList();
    }
    else
    {
        var file = JsonSerializer.Deserialize<QuestionsFile>(raw, jsonOpts)!;
        questions = [
            ..(file.Answerable   ?? []).Select(q => q.ToQuestion(true)),
            ..(file.Unanswerable ?? []).Select(q => q.ToQuestion(false)),
        ];
    }
}

Console.WriteLine($"Loaded {questions.Count} questions from {questionsPath}");

// ---------------------------------------------------------------------------
// Run each question against the API
// ---------------------------------------------------------------------------
// Local models are slow and have no fixed upper bound on latency (cold model load,
// CPU-only inference), so the default 100s HttpClient timeout would cut them off mid-answer.
using var http = new HttpClient
{
    BaseAddress = new Uri(apiBaseUrl),
    Timeout = TimeSpan.FromSeconds(timeoutSeconds),
};

var questionResults = new List<QuestionResult>();
string? detectedMode = null;

for (int i = 0; i < questions.Count; i++)
{
    var q = questions[i];
    Console.Write($"  [{i + 1}/{questions.Count}] {q.Id}: {Truncate(q.Text, 55)} ... ");

    AskResponse? apiResponse = null;
    string? errorMessage = null;

    try
    {
        var httpResponse = await http.PostAsJsonAsync("/ask", new { question = q.Text });
        httpResponse.EnsureSuccessStatusCode();
        apiResponse = await httpResponse.Content.ReadFromJsonAsync<AskResponse>(jsonOpts);
        detectedMode ??= apiResponse?.RetrievalMode;
    }
    catch (Exception ex)
    {
        errorMessage = ex.Message;
    }

    var result = await Score(q, apiResponse, errorMessage, judgeClient);
    questionResults.Add(result);

    Console.WriteLine(SummaryIcon(result));
}

Console.WriteLine();

// ---------------------------------------------------------------------------
// Aggregate metrics
// ---------------------------------------------------------------------------
string retrievalMode = detectedMode ?? config["Retrieval:Mode"] ?? "unknown";

var answerable   = questionResults.Where(r => r.Answerable).ToList();
var unanswerable = questionResults.Where(r => !r.Answerable).ToList();
var toolExpected = questionResults.Where(r => r.ExpectsToolCall).ToList();

int hitCount        = answerable.Count(r => r.Hit);
int citedCount      = answerable.Count(r => r.Cited);
int abstainedCount  = unanswerable.Count(r => r.Abstained);
int toolCorrectCount = toolExpected.Count(r => r.ToolCallCorrect == true);
int answerCorrectCount = answerable.Count(r => r.AnswerCorrect == true);
int parseFailureCount  = questionResults.Count(r => r.ParseFailed);

double hitRate        = answerable.Count  > 0 ? (double)hitCount       / answerable.Count  : 0;
double citationAcc    = answerable.Count  > 0 ? (double)citedCount     / answerable.Count  : 0;
double abstentionRate = unanswerable.Count > 0 ? (double)abstainedCount / unanswerable.Count : 0;
double? toolCallAcc   = toolExpected.Count > 0 ? (double)toolCorrectCount / toolExpected.Count : null;
double answerAccuracy = answerable.Count  > 0 ? (double)answerCorrectCount / answerable.Count : 0;

// ---------------------------------------------------------------------------
// Print table
// ---------------------------------------------------------------------------
PrintTable(questionResults, retrievalMode, topK, rrfK);
PrintSummary(answerable.Count, unanswerable.Count, toolExpected.Count,
             hitRate, citationAcc, abstentionRate, toolCallAcc, answerAccuracy, parseFailureCount);
PrintJudgeVerdicts(answerable);

// ---------------------------------------------------------------------------
// Write results file
// ---------------------------------------------------------------------------
Directory.CreateDirectory(resultsDir);
string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
string fileName  = modelLabel is not null
    ? $"{modelLabel}-{DateTime.UtcNow:yyyy-MM-dd}.json"
    : $"{timestamp}_{retrievalMode}.json";
string outPath   = Path.Combine(resultsDir, fileName);

var runResult = new RunResult(
    Timestamp:       DateTime.UtcNow.ToString("o"),
    ModelLabel:      modelLabel,
    RetrievalMode:   retrievalMode,
    TopK:            topK,
    RrfK:            rrfK,
    TotalQuestions:  questions.Count,
    AnswerableCount: answerable.Count,
    UnanswerableCount: unanswerable.Count,
    HitRate:         hitRate,
    CitationAccuracy: citationAcc,
    AbstentionRate:  abstentionRate,
    ToolCallAccuracy: toolCallAcc,
    AnswerAccuracy:  answerAccuracy,
    ParseFailureCount: parseFailureCount,
    Questions:       questionResults);

await File.WriteAllTextAsync(outPath,
    JsonSerializer.Serialize(runResult, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"\nResults written to {outPath}");
return 0;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static async Task<QuestionResult> Score(Question q, AskResponse? resp, string? error, ChatClient judgeClient)
{
    var goldRefs = ParseRefs(q.GoldParagraph);
    bool answerable = q.Answerable && goldRefs.Count > 0;

    // A failed HTTP call counts the same as a response the model produced but that
    // failed to parse: both mean "no usable answer for this question", not a crash.
    bool parseFailed = resp?.ParseFailed ?? (error is not null);

    if (resp is null)
    {
        bool? judgeSkippedCorrect = answerable ? false : null;
        string? judgeSkippedReason = answerable ? $"skipped: no response ({error})" : null;

        return new QuestionResult(
            Id: q.Id,
            Question: q.Text,
            GoldParagraph: q.GoldParagraph,
            Answerable: answerable,
            ExpectsToolCall: q.ExpectsToolCall,
            Hit: false,
            Cited: false,
            Abstained: false,
            UsedTool: false,
            ToolCallCorrect: q.ExpectsToolCall ? false : null,
            Answer: null,
            RetrievedParagraphRefs: [],
            CitedParagraphRefs: [],
            ParseFailed: true,
            AnswerCorrect: judgeSkippedCorrect,
            JudgeReason: judgeSkippedReason,
            Error: error);
    }

    var retrieved = resp.RetrievedParagraphRefs ?? [];
    var cited     = resp.Citations?.Select(c => $"{c.ArticleNumber}.{c.ParagraphNumber}").ToList()
                    ?? [];

    bool hit     = answerable && goldRefs.All(r => retrieved.Contains(r, StringComparer.OrdinalIgnoreCase));
    bool citedOk = answerable && goldRefs.All(r => cited.Contains(r, StringComparer.OrdinalIgnoreCase));

    // Abstention: unanswerable question with empty citations
    bool abstained = !answerable && (resp.Citations == null || resp.Citations.Count == 0);

    bool? toolCorrect = q.ExpectsToolCall ? resp.UsedTool : null;

    bool? answerCorrect = null;
    string? judgeReason = null;
    if (answerable)
    {
        if (parseFailed)
        {
            answerCorrect = false;
            judgeReason = "skipped: response failed to parse into an answer";
        }
        else
        {
            (answerCorrect, judgeReason) = await JudgeAnswer(judgeClient, q.Text, q.GoldAnswer ?? "", resp.Answer);
        }
    }

    return new QuestionResult(
        Id: q.Id,
        Question: q.Text,
        GoldParagraph: q.GoldParagraph,
        Answerable: answerable,
        ExpectsToolCall: q.ExpectsToolCall,
        Hit: hit,
        Cited: citedOk,
        Abstained: abstained,
        UsedTool: resp.UsedTool,
        ToolCallCorrect: toolCorrect,
        Answer: resp.Answer,
        RetrievedParagraphRefs: retrieved.ToList(),
        CitedParagraphRefs: cited,
        ParseFailed: parseFailed,
        AnswerCorrect: answerCorrect,
        JudgeReason: judgeReason,
        Error: error);
}

static async Task<(bool Correct, string Reason)> JudgeAnswer(
    ChatClient judgeClient, string question, string goldAnswer, string candidateAnswer)
{
    const string judgeSystemPrompt = """
        You are grading whether a candidate answer to a question about the UEFA Champions
        League regulations conveys the same factual content as the gold answer.

        Mark it "correct" if the candidate states the same key fact(s) as the gold answer,
        even with different wording or extra correct detail.
        Mark it "incorrect" if it omits the key fact, contradicts the gold answer, states the
        refusal text instead of answering, or is otherwise not supported by the gold answer.

        Respond with valid JSON in exactly this format:
        { "verdict": "correct", "reason": "<one sentence>" }
        """;

    string userPrompt = $"""
        Question: {question}
        Gold answer: {goldAnswer}
        Candidate answer: {candidateAnswer}
        """;

    var options = new ChatCompletionOptions
    {
        ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        Temperature = 0,
        MaxOutputTokenCount = 256,
    };

    try
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(judgeSystemPrompt),
            ChatMessage.CreateUserMessage(userPrompt),
        };
        var result = await judgeClient.CompleteChatAsync(messages, options);
        string raw = result.Value.Content[0].Text;

        using var doc = JsonDocument.Parse(raw);
        string verdict = doc.RootElement.GetProperty("verdict").GetString() ?? "incorrect";
        string reason  = doc.RootElement.GetProperty("reason").GetString() ?? "";
        return (string.Equals(verdict, "correct", StringComparison.OrdinalIgnoreCase), reason);
    }
    catch (Exception ex)
    {
        return (false, $"judge call failed: {ex.Message}");
    }
}

static List<string> ParseRefs(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return [];
    return raw.Split(',').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
}

static void PrintTable(List<QuestionResult> results, string mode, int topK, int rrfK)
{
    Console.WriteLine($"UCL RAG Evaluation  |  Mode: {mode}  |  TopK: {topK}  |  RrfK: {rrfK}");
    Console.WriteLine(new string('=', 85));
    Console.WriteLine($"  {"ID",-6} {"Question",-50} {"Hit",3} {"Cit",3} {"Abs",3} {"Tool",4} {"Ans",3}");
    Console.WriteLine($"  {new string('-', 6)} {new string('-', 50)} {new string('-', 3)} {new string('-', 3)} {new string('-', 3)} {new string('-', 4)} {new string('-', 3)}");

    foreach (var r in results)
    {
        string hit  = !r.Answerable ? " - " : r.Hit    ? " Y " : " N ";
        string cit  = !r.Answerable ? " - " : r.Cited  ? " Y " : " N ";
        string abs  =  r.Answerable ? " - " : r.Abstained ? " Y " : " N ";
        string tool = !r.ExpectsToolCall ? "  - " : r.ToolCallCorrect == true ? "  Y " : "  N ";
        string ans  = !r.Answerable ? " - " : r.AnswerCorrect == true ? " Y " : " N ";
        string parseFlag = r.ParseFailed ? "  [PARSE FAIL]" : "";
        Console.WriteLine($"  {r.Id,-6} {Truncate(r.Question, 50),-50} {hit} {cit} {abs} {tool} {ans}{parseFlag}");
    }

    Console.WriteLine();
}

static void PrintSummary(int answerable, int unanswerable, int toolQuestions,
    double hitRate, double citationAcc, double abstentionRate, double? toolCallAcc,
    double answerAccuracy, int parseFailureCount)
{
    Console.WriteLine("Summary");
    Console.WriteLine($"  Answerable questions  : {answerable}");
    Console.WriteLine($"  Unanswerable questions: {unanswerable}");
    Console.WriteLine($"  Retrieval hit rate    : {hitRate:P1}");
    Console.WriteLine($"  Citation accuracy     : {citationAcc:P1}");
    Console.WriteLine($"  Abstention rate       : {abstentionRate:P1}");
    if (toolCallAcc.HasValue)
        Console.WriteLine($"  Tool call accuracy    : {toolCallAcc:P1}  ({toolQuestions} questions)");
    else
        Console.WriteLine($"  Tool call accuracy    : n/a");
    Console.WriteLine($"  Answer accuracy (judge): {answerAccuracy:P1}  ({answerable} questions)");
    Console.WriteLine($"  Parse/response failures: {parseFailureCount}");
}

static void PrintJudgeVerdicts(List<QuestionResult> answerable)
{
    Console.WriteLine();
    Console.WriteLine("Judge verdicts (answerable questions)");
    Console.WriteLine(new string('=', 85));
    foreach (var r in answerable)
    {
        string verdict = r.AnswerCorrect == true ? "CORRECT" : "INCORRECT";
        Console.WriteLine($"  [{r.Id}] {verdict} — {r.Question}");
        Console.WriteLine($"      gold:      {r.GoldParagraph}");
        Console.WriteLine($"      answer:    {Truncate(r.Answer ?? "", 140)}");
        Console.WriteLine($"      reason:    {r.JudgeReason}");
        Console.WriteLine();
    }
}

static string SummaryIcon(QuestionResult r)
{
    if (r.ParseFailed) return r.Error is not null ? $"PARSE/HTTP FAIL: {r.Error}" : "PARSE FAIL (invalid JSON)";
    if (!r.Answerable) return r.Abstained ? "abstained" : "FAILED to abstain";
    var parts = new List<string>();
    if (r.Hit)   parts.Add("hit");   else parts.Add("MISS");
    if (r.Cited) parts.Add("cited"); else parts.Add("NOT-CITED");
    if (r.ExpectsToolCall) parts.Add(r.ToolCallCorrect == true ? "tool-ok" : "TOOL-MISS");
    parts.Add(r.AnswerCorrect == true ? "answer-correct" : "answer-INCORRECT");
    return string.Join(", ", parts);
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..(max - 1)] + "…";

static string? ResolveFromRoot(string relativePath)
{
    // Walk up from the current working directory (where the user ran dotnet run).
    // This finds eval/questions.json and eval/results at the repo root regardless
    // of where the compiled binary lives.
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        string candidate = Path.Combine(dir.FullName, relativePath);
        if (File.Exists(candidate) || Directory.Exists(candidate))
            return candidate;
        // Also accept a path whose parent directory exists (for dirs not yet created).
        string? parent = Path.GetDirectoryName(candidate);
        if (parent != null && Directory.Exists(parent))
            return candidate;
        dir = dir.Parent;
    }
    return null;
}

// ---------------------------------------------------------------------------
// Records
// ---------------------------------------------------------------------------
record Question(
    string Id,
    [property: JsonPropertyName("question")] string Text,
    string GoldParagraph,
    string? GoldAnswer,
    bool Answerable,
    bool ExpectsToolCall);

record AskResponse(
    string Answer,
    List<Citation>? Citations,
    bool UsedTool,
    string RetrievalMode,
    List<string>? RetrievedParagraphRefs,
    bool ParseFailed = false);

record Citation(
    string ArticleNumber,
    string ParagraphNumber,
    string ArticleTitle,
    string Excerpt);

record QuestionResult(
    string Id,
    string Question,
    string GoldParagraph,
    bool Answerable,
    bool ExpectsToolCall,
    bool Hit,
    bool Cited,
    bool Abstained,
    bool UsedTool,
    bool? ToolCallCorrect,
    string? Answer,
    List<string> RetrievedParagraphRefs,
    List<string> CitedParagraphRefs,
    bool ParseFailed,
    bool? AnswerCorrect,
    string? JudgeReason,
    string? Error);

record RunResult(
    string Timestamp,
    string? ModelLabel,
    string RetrievalMode,
    int TopK,
    int RrfK,
    int TotalQuestions,
    int AnswerableCount,
    int UnanswerableCount,
    double HitRate,
    double CitationAccuracy,
    double AbstentionRate,
    double? ToolCallAccuracy,
    double AnswerAccuracy,
    int ParseFailureCount,
    List<QuestionResult> Questions);

// DTOs for deserializing questions.json (supports both flat array and nested object)
record QuestionsFile(
    List<QuestionDto>? Answerable,
    List<QuestionDto>? Unanswerable);

record QuestionDto(
    string Id,
    [property: JsonPropertyName("question")] string Text,
    string? GoldParagraph,
    string? GoldAnswer,
    bool? Answerable,
    bool? ExpectsToolCall)
{
    public Question ToQuestion(bool answerable) => new(
        Id: Id,
        Text: Text,
        GoldParagraph: GoldParagraph ?? "",
        GoldAnswer: GoldAnswer,
        Answerable: answerable,
        ExpectsToolCall: ExpectsToolCall ?? false);
}
