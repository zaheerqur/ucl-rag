using Azure;
using Azure.AI.OpenAI;
using OpenAI.Embeddings;

namespace Ingestion.Infrastructure;

public interface IEmbeddingService
{
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default);
}

public class AzureEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _client;

    public AzureEmbeddingService(string endpoint, string apiKey, string deploymentName)
    {
        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _client = azureClient.GetEmbeddingClient(deploymentName);
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var result = await _client.GenerateEmbeddingAsync(text, cancellationToken: ct);
        return result.Value.ToFloats().ToArray();
    }
}
