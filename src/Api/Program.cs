using System.ClientModel;
using Api.Generation;
using Api.Models;
using Api.Retrieval;
using Api.Tools;
using Azure;
using Azure.AI.OpenAI;
using Npgsql;
using OpenAI;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true);

// --- Configuration ---
string connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Postgres");

string endpoint = builder.Configuration["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint");

string apiKey = builder.Configuration["AzureOpenAI:ApiKey"]
    ?? throw new InvalidOperationException(
        "Missing AzureOpenAI:ApiKey. Set with: dotnet user-secrets set \"AzureOpenAI:ApiKey\" \"<key>\"");

string embeddingDeployment = builder.Configuration["AzureOpenAI:EmbeddingDeployment"]
    ?? throw new InvalidOperationException("Missing AzureOpenAI:EmbeddingDeployment");

string chatDeployment = builder.Configuration["AzureOpenAI:ChatDeployment"]
    ?? throw new InvalidOperationException("Missing AzureOpenAI:ChatDeployment");

string retrievalMode = builder.Configuration["Retrieval:Mode"] ?? "hybrid";
int topK = int.Parse(builder.Configuration["Retrieval:TopK"] ?? "10");
int rrfK = int.Parse(builder.Configuration["Retrieval:RrfK"] ?? "60");

string generationProvider = builder.Configuration["Generation:Provider"] ?? "AzureOpenAI";
string generationModel = builder.Configuration["Generation:Model"] ?? chatDeployment;
double generationTemperature = double.Parse(builder.Configuration["Generation:Temperature"] ?? "0");
int generationMaxOutputTokens = int.Parse(builder.Configuration["Generation:MaxOutputTokens"] ?? "1024");
int generationContextWindowTokens = int.Parse(builder.Configuration["Generation:ContextWindowTokens"] ?? "8192");
string ollamaEndpoint = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434/v1";

// Roster data path: walk up from binary until we find data/rosters.json
string rostersPath = FindFile("data/rosters.json")
    ?? throw new InvalidOperationException("Cannot locate data/rosters.json");

// --- Services ---
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
var dataSource = dataSourceBuilder.Build();

var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
var embeddingClient = azureClient.GetEmbeddingClient(embeddingDeployment);

// Embeddings and retrieval always stay on Azure. Only the answer model's ChatClient
// construction branches on Generation:Provider — AgentChatService and the RAG pipeline
// are unaware of which provider they're talking to.
var chatClient = generationProvider switch
{
    "AzureOpenAI" => azureClient.GetChatClient(generationModel),
    "Ollama" => new OpenAIClient(
            new ApiKeyCredential("ollama"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(ollamaEndpoint),
                NetworkTimeout = TimeSpan.FromMinutes(5),
            })
        .GetChatClient(generationModel),
    _ => throw new InvalidOperationException(
        $"Unknown Generation:Provider '{generationProvider}'. Use 'AzureOpenAI' or 'Ollama'.")
};

var retrieval = new RetrievalService(dataSource, embeddingClient, rrfK);
var rosterService = new RosterService(rostersPath);
IGenerationService generation = new AgentChatService(
    chatClient,
    rosterService,
    generationTemperature,
    generationMaxOutputTokens,
    generationContextWindowTokens);

// --- Endpoints ---
var app = builder.Build();

app.MapPost("/ask", async (AskRequest request, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "question must not be empty" });

    IReadOnlyList<RetrievedChunk> chunks = retrievalMode switch
    {
        "dense"  => await retrieval.SearchDense(request.Question, topK, ct),
        "sparse" => await retrieval.SearchSparse(request.Question, topK, ct),
        _        => await retrieval.SearchHybrid(request.Question, topK, ct),
    };

    var generated = await generation.GenerateAnswerAsync(request.Question, chunks, ct);

    // Build citations: look up each ref the model claimed to use in the retrieved set.
    var chunkByRef = chunks.ToDictionary(c => c.ParagraphRef, StringComparer.OrdinalIgnoreCase);
    var citations = generated.UsedParagraphRefs
        .Where(chunkByRef.ContainsKey)
        .Select(r =>
        {
            var c = chunkByRef[r];
            return new Citation(
                c.ArticleNumber,
                c.ParagraphNumber,
                c.ArticleTitle,
                c.ChunkText[..Math.Min(400, c.ChunkText.Length)]);
        })
        .ToList();

    var retrievedRefs = chunks.Select(c => c.ParagraphRef).ToList();

    return Results.Ok(new AskResponse(
        Answer: generated.Answer,
        Citations: citations,
        UsedTool: generated.UsedTool,
        RetrievalMode: retrievalMode,
        RetrievedParagraphRefs: retrievedRefs,
        ParseFailed: generated.ParseFailed));
});

app.Run();

static string? FindFile(string relativePath)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        string candidate = Path.Combine(dir.FullName, relativePath);
        if (File.Exists(candidate))
            return candidate;
        dir = dir.Parent;
    }
    return null;
}
