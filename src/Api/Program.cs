using Api.Generation;
using Api.Models;
using Api.Retrieval;
using Azure;
using Azure.AI.OpenAI;
using Npgsql;

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

// --- Services ---
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
var dataSource = dataSourceBuilder.Build();

var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
var embeddingClient = azureClient.GetEmbeddingClient(embeddingDeployment);
var chatClient = azureClient.GetChatClient(chatDeployment);

var retrieval = new RetrievalService(dataSource, embeddingClient, rrfK);
IGenerationService generation = new AzureChatService(chatClient);

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

    return Results.Ok(new AskResponse(
        Answer: generated.Answer,
        Citations: citations,
        UsedTool: false,
        RetrievalMode: retrievalMode));
});

app.Run();
