using System.Net;
using System.Text;
using System.Text.Json;

namespace ProxyTests.FakeProviders;

/// <summary>
/// In-memory HTTP handler that returns canned /v1/models (OpenAI shape) or
/// /api/tags (Ollama shape) responses keyed by RequestUri.Host. Every other
/// path returns 404. Used to drive ModelCatalogService.RefreshAvailableModels
/// in unit tests without touching the network.
/// </summary>
internal sealed class FakeProviderHandler : DelegatingHandler
{
    private readonly Dictionary<string, string> _openAiBodies;
    private readonly Dictionary<string, string> _ollamaBodies;
    private int _requestCount;

    public int RequestCount => _requestCount;

    public Task<int> GetRequestCountAsync() => Task.FromResult(_requestCount);

    public FakeProviderHandler(IDictionary<string, string[]> modelsByProvider, IEnumerable<string>? ollamaProviders = null)
    {
        HashSet<string> ollama = new(ollamaProviders ?? [], StringComparer.OrdinalIgnoreCase);
        // Auto-detect Ollama-shaped providers from capabilities (in case caller didn't specify).
        foreach (string key in modelsByProvider.Keys)
        {
            if (!ollama.Contains(key) && ProviderCapabilitiesRegistry.TryGet(key, out ProviderCapabilities caps)
                && caps.ApiFormat == ApiFormat.Ollama)
            {
                ollama.Add(key);
            }
        }
        _openAiBodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _ollamaBodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string[]> kv in modelsByProvider)
        {
            // An "ollama-shaped" provider returns /api/tags; everyone else returns /v1/models.
            if (ollama.Contains(kv.Key))
            {
                _ollamaBodies[kv.Key] = BuildOllamaTagsBody(kv.Value);
            }
            else
            {
                _openAiBodies[kv.Key] = BuildOpenAiModelsBody(kv.Value);
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        string? provider = ResolveProvider(request.RequestUri?.Host);
        if (provider is null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unknown host", Encoding.UTF8, "text/plain")
            });
        }

        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        bool isOllamaProvider = _ollamaBodies.ContainsKey(provider) && !_openAiBodies.ContainsKey(provider);
        bool ollamaPath = path.Contains("/api/tags", StringComparison.OrdinalIgnoreCase);
        bool openAiPath = path.Contains("/v1/models", StringComparison.OrdinalIgnoreCase);

        string body = (isOllamaProvider || ollamaPath) && !openAiPath
            ? _ollamaBodies[provider]
            : _openAiBodies[provider];

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    private string? ResolveProvider(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        // Union: a provider is "known" if it serves either /v1/models (OpenAI shape)
        // or /api/tags (Ollama shape). We previously only matched _openAiBodies, which
        // meant ollama-shaped providers returned 404 and never contributed to discovery.
        foreach (string name in _openAiBodies.Keys)
        {
            if (host.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        foreach (string name in _ollamaBodies.Keys)
        {
            if (host.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        return null;
    }

    private static string BuildOpenAiModelsBody(string[] ids)
    {
        StringBuilder sb = new("{\"object\":\"list\",\"data\":[");
        for (int i = 0; i < ids.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(JsonSerializer.Serialize(ids[i]))
              .Append(",\"object\":\"model\",\"created\":1700000000,\"owned_by\":\"fake\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string BuildOllamaTagsBody(string[] ids)
    {
        StringBuilder sb = new("{\"models\":[");
        for (int i = 0; i < ids.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":").Append(JsonSerializer.Serialize(ids[i]))
              .Append(",\"model\":").Append(JsonSerializer.Serialize(ids[i]))
              .Append(",\"modified_at\":\"2024-01-01T00:00:00Z\",\"size\":0}");
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
