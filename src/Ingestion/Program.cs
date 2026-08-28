using Ingestion.Infrastructure;
using Ingestion.Parsing;
using Microsoft.Extensions.Configuration;
using Npgsql;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .Build();

var connectionString = config.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Postgres");

var endpoint = config["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException("Missing AzureOpenAI:Endpoint");

var embeddingDeployment = config["AzureOpenAI:EmbeddingDeployment"]
    ?? throw new InvalidOperationException("Missing AzureOpenAI:EmbeddingDeployment");

var apiKey = config["AzureOpenAI:ApiKey"]
    ?? throw new InvalidOperationException(
        "Missing AzureOpenAI:ApiKey — set it with:\n" +
        "  cd src/Ingestion && dotnet user-secrets set \"AzureOpenAI:ApiKey\" \"<your-key>\"");

var pdfPath = config["Ingestion:PdfPath"]
    ?? throw new InvalidOperationException("Missing Ingestion:PdfPath");

// Resolve relative PDF path upward from the binary location.
if (!Path.IsPathRooted(pdfPath))
    pdfPath = ResolveFromBinary(pdfPath) ?? pdfPath;

if (!File.Exists(pdfPath))
    throw new FileNotFoundException($"PDF not found at resolved path: {pdfPath}");

// --- Run migrations ---
Console.WriteLine("Running database migrations...");
string migrationsDir = ResolveDir("db/migrations")
    ?? throw new DirectoryNotFoundException("Cannot locate db/migrations from binary location");

var migrator = new DatabaseMigrator(connectionString, migrationsDir);
await migrator.MigrateAsync();
Console.WriteLine("Migrations complete.");

// --- Build Npgsql data source with pgvector support ---
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.UseVector();
await using var dataSource = dataSourceBuilder.Build();

// --- Parse PDF ---
Console.WriteLine($"Parsing PDF: {pdfPath}");
var parser = new PdfParser();
var chunks = parser.Parse(pdfPath);
Console.WriteLine($"Parsed {chunks.Count} paragraphs.");

var badChunks = chunks.Where(c => string.IsNullOrWhiteSpace(c.ParagraphNumber)).ToList();
if (badChunks.Count > 0)
    Console.Error.WriteLine($"WARNING: {badChunks.Count} chunks have an empty paragraph number.");

// --- Embed and upsert ---
var embeddingService = new AzureEmbeddingService(endpoint, apiKey, embeddingDeployment);
var repository = new ChunkRepository(dataSource);

int upserted = 0;
foreach (var chunk in chunks)
{
    var embedding = await embeddingService.GetEmbeddingAsync(chunk.ChunkText);
    await repository.UpsertAsync(chunk, embedding);
    upserted++;
    if (upserted % 25 == 0)
        Console.WriteLine($"  Upserted {upserted}/{chunks.Count} chunks...");
}

long total = await repository.CountAsync();
long nullPara = await repository.CountNullParagraphAsync();
Console.WriteLine($"Done. Total chunks in DB: {total}. Rows with null paragraph_number: {nullPara}.");

// --- Helpers ---
static string? ResolveFromBinary(string relativePath)
{
    string dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8; i++)
    {
        string candidate = Path.GetFullPath(Path.Combine(dir, relativePath));
        if (File.Exists(candidate))
            return candidate;
        string? parent = Path.GetDirectoryName(dir);
        if (parent == null || parent == dir) break;
        dir = parent;
    }
    return null;
}

static string? ResolveDir(string relativePath)
{
    string dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8; i++)
    {
        string candidate = Path.GetFullPath(Path.Combine(dir, relativePath));
        if (Directory.Exists(candidate))
            return candidate;
        string? parent = Path.GetDirectoryName(dir);
        if (parent == null || parent == dir) break;
        dir = parent;
    }
    return null;
}
