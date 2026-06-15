using System.Text.Json;

namespace ProxyTests;

/// <summary>
/// Tests for the <c>override_client_params</c> flag on <see cref="ModelExecutionConfig"/>.
/// When true, the proxy overwrites client-supplied temperature / top_p / max_tokens /
/// reasoning_effort with the configured value (e.g. Kimi K2.x mandates temperature=1.0).
/// When false (or absent), the proxy only injects defaults for missing fields.
/// </summary>
public class OverrideClientParamsTests
{
    // ── RequestTransformer behavior ───────────────────────────────────────

    [Fact]
    public void ApplyExecutionDefaults_OverrideClientParamsTrue_OverwritesClientTemperature()
    {
        // kimi-k2.6 in moonshot.json has override_client_params=true and temperature=1.0
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"temperature":0.7}""";

        string result = sut.ApplyExecutionDefaults(raw, "kimi-k2.6", ProviderCapabilitiesRegistry.Get("moonshot"));

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal(1.0, doc.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void ApplyExecutionDefaults_OverrideClientParamsTrue_OverwritesClientMaxTokens()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"max_tokens":32000}""";

        string result = sut.ApplyExecutionDefaults(raw, "kimi-k2.6", ProviderCapabilitiesRegistry.Get("moonshot"));

        using JsonDocument doc = JsonDocument.Parse(result);
        // kimi-k2.6's configured max_tokens is 4096
        Assert.Equal(4096, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void ApplyExecutionDefaults_OverrideClientParamsTrue_OverwritesClientTopP()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"top_p":0.5}""";

        string result = sut.ApplyExecutionDefaults(raw, "kimi-k2.6", ProviderCapabilitiesRegistry.Get("moonshot"));

        using JsonDocument doc = JsonDocument.Parse(result);
        // kimi-k2.6's configured top_p is 0.95
        Assert.Equal(0.95, doc.RootElement.GetProperty("top_p").GetDouble());
    }

    [Fact]
    public void ApplyExecutionDefaults_OverrideClientParamsFalse_KeepsClientValue()
    {
        // moonshot-v1-128k has override_client_params absent (defaults to false) — the proxy
        // must NOT overwrite a client-supplied temperature.
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"temperature":0.7,"max_tokens":1234}""";

        string result = sut.ApplyExecutionDefaults(raw, "moonshot-v1-128k", ProviderCapabilitiesRegistry.Get("moonshot"));

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal(0.7, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(1234, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void ApplyExecutionDefaults_OverrideClientParamsFalse_InjectsMissingDefaults()
    {
        // Same model (moonshot-v1-128k) but with no temperature/max_tokens in the body — the
        // proxy must inject the configured defaults since override mode is off.
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[]}""";

        string result = sut.ApplyExecutionDefaults(raw, "moonshot-v1-128k", ProviderCapabilitiesRegistry.Get("moonshot"));

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal(0.3, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(4096, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void ApplyExecutionDefaults_OverrideClientParamsTrue_AppliesToAllForcedFields()
    {
        // Single test that exercises temperature, top_p, max_tokens, and reasoning_effort
        // (kimi-k2.6 has reasoning_effort absent in JSON, so reasoning_effort is NOT forced —
        //  but the model is still a "native reasoner" by the provider/model heuristic).
        RequestTransformer sut = CreateTransformer();
        string raw = """
            {
              "messages": [],
              "temperature": 0.2,
              "top_p": 0.1,
              "max_tokens": 99999
            }
            """;

        string result = sut.ApplyExecutionDefaults(raw, "kimi-k2.6", ProviderCapabilitiesRegistry.Get("moonshot"));

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal(1.0, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, doc.RootElement.GetProperty("top_p").GetDouble());
        Assert.Equal(4096, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    // ── ModelExecutionConfig defaults & parsing ───────────────────────────

    [Fact]
    public void ModelExecutionConfig_OverrideClientParams_DefaultsToFalse()
    {
        ModelExecutionConfig cfg = new();

        Assert.False(cfg.OverrideClientParams);
    }

    [Fact]
    public void ModelExecutionConfig_OverrideClientParams_TrueIsParsed()
    {
        // Mirrors LoadProviderModelSelections() parsing of moonshot.json's kimi-k2.6 entry.
        string json = """
            {
              "provider": "moonshot",
              "models": [{
                "match": "kimi-k2.6",
                "priority": 1,
                "enabled": true,
                "execution": {
                  "temperature": 1.0,
                  "override_client_params": true
                }
              }]
            }
            """;

        ModelSelectionEntry[] entries = ParseSelections(json);
        ModelSelectionEntry entry = Assert.Single(entries);
        Assert.True(entry.Execution.OverrideClientParams);
        Assert.Equal(1.0, entry.Execution.Temperature);
    }

    [Fact]
    public void ModelExecutionConfig_OverrideClientParams_FalseIsParsed()
    {
        string json = """
            {
              "provider": "moonshot",
              "models": [{
                "match": "moonshot-v1-128k",
                "priority": 1,
                "enabled": true,
                "execution": {
                  "temperature": 0.3,
                  "override_client_params": false
                }
              }]
            }
            """;

        ModelSelectionEntry[] entries = ParseSelections(json);
        ModelSelectionEntry entry = Assert.Single(entries);
        Assert.False(entry.Execution.OverrideClientParams);
    }

    [Fact]
    public void ModelExecutionConfig_OverrideClientParams_AbsentIsParsedAsFalse()
    {
        // Default behaviour: omitting the field must yield false (not true, not null).
        string json = """
            {
              "provider": "moonshot",
              "models": [{
                "match": "moonshot-v1-128k",
                "priority": 1,
                "enabled": true,
                "execution": {
                  "temperature": 0.3
                }
              }]
            }
            """;

        ModelSelectionEntry[] entries = ParseSelections(json);
        ModelSelectionEntry entry = Assert.Single(entries);
        Assert.False(entry.Execution.OverrideClientParams);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static RequestTransformer CreateTransformer()
    {
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "unit-test-key");
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_BASE_URL", "http://127.0.0.1:12345");

        ProviderHttpClientFactory httpClientFactory = new();
        ProviderRegistry providerRegistry = new(httpClientFactory);
        ModelSelectionStore modelSelectionStore = new();
        ModelCatalogService modelCatalog = new(providerRegistry, modelSelectionStore);
        ReasoningCacheService cache = new();
        return new RequestTransformer(modelCatalog, cache);
    }

    private static ModelSelectionEntry[] ParseSelections(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        List<ModelSelectionEntry> entries = [];
        foreach (JsonElement item in root.GetProperty("models").EnumerateArray())
        {
            string match = item.GetProperty("match").GetString()!;
            JsonElement execE = item.GetProperty("execution");
            bool ov = execE.TryGetProperty("override_client_params", out JsonElement ovE)
                && ovE.ValueKind is JsonValueKind.True or JsonValueKind.False
                && ovE.GetBoolean();
            ModelExecutionConfig cfg = new(
                Temperature: execE.TryGetProperty("temperature", out JsonElement t) ? t.GetDouble() : null,
                OverrideClientParams: ov
            );
            entries.Add(new ModelSelectionEntry(match, 1, true, cfg));
        }
        return [.. entries];
    }
}
