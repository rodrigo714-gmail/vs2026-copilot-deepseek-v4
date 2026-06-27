namespace ProxyTests;

[Collection("Proxy")]
public class ModelSelectionStoreTests
{
    [Fact]
    public void GetExecutionConfigForModel_DeepSeekV4Pro_HasContextLength()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.ContextLength.HasValue);
        Assert.Equal(1_048_576, config.ContextLength.Value);
    }

    [Fact]
    public void GetExecutionConfigForModel_DeepSeekV4Pro_HasMaxOutputTokens()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.MaxOutputTokens.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_UnknownModel_ReturnsDefaults()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("nonexistent-model-xyz", registry.ModelToProvider);

        Assert.False(config.OverrideClientParams);
    }

    [Fact]
    public void GetProviderModelSelections_DeepSeek_ReturnsEntries()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry[] entries = store.GetProviderModelSelections("deepseek");

        Assert.NotEmpty(entries);
    }

    [Fact]
    public void GetProviderModelSelections_OpenAI_ReturnsEntries()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry[] entries = store.GetProviderModelSelections("openai");

        Assert.NotEmpty(entries);
    }

    [Fact]
    public void FindModelSelectionEntry_DeepSeekV4Pro_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("deepseek-v4-pro", "deepseek");

        Assert.NotNull(entry);
        Assert.Equal("deepseek-v4-pro", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_NonExistent_ReturnsNull()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("nonexistent", "deepseek");

        Assert.Null(entry);
    }

    [Fact]
    public void IsPreferredModel_DeepSeekV4Pro_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("deepseek-v4-pro", "deepseek");

        Assert.True(isPreferred);
    }

    [Fact]
    public void IsPreferredModel_NonExistent_ReturnsFalse()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("nonexistent-model", "deepseek");

        Assert.False(isPreferred);
    }

    [Fact]
    public void GetPreferredModelPriority_DeepSeekV4Pro_ReturnsPriority()
    {
        ModelSelectionStore store = new();

        int priority = store.GetPreferredModelPriority("deepseek-v4-pro", "deepseek");

        Assert.True(priority >= 0);
    }

    [Fact]
    public void ProviderModelSelections_HasAllProviders()
    {
        ModelSelectionStore store = new();

        Assert.True(store.ProviderModelSelections.ContainsKey("deepseek"));
        Assert.True(store.ProviderModelSelections.ContainsKey("openai"));
        Assert.True(store.ProviderModelSelections.ContainsKey("nvidia"));
        Assert.True(store.ProviderModelSelections.ContainsKey("groq"));
        Assert.True(store.ProviderModelSelections.ContainsKey("moonshot"));
        Assert.True(store.ProviderModelSelections.ContainsKey("openrouter"));
        Assert.True(store.ProviderModelSelections.ContainsKey("ollama"));
    }

    [Fact]
    public void ProviderModelSelections_HasCerebrasProvider()
    {
        ModelSelectionStore store = new();

        Assert.True(store.ProviderModelSelections.ContainsKey("cerebras"));
    }

    [Fact]
    public void GetProviderModelSelections_Ollama_MergedFromBothFiles()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry[] entries = store.GetProviderModelSelections("ollama");

        Assert.NotEmpty(entries);
        // Should include entries from both ollama.json and ollamacloud.json
        Assert.True(entries.Length > 5, $"Expected merged entries, got {entries.Length}");
    }

    [Fact]
    public void FindModelSelectionEntry_Ollama_Qwen3Coder480B_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("qwen3-coder:480b", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("qwen3-coder:480b", entry.Value.Match);
        Assert.Equal(4, entry.Value.Priority);
    }

    [Fact]
    public void FindModelSelectionEntry_Ollama_Devstral_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("devstral-2:123b", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("devstral-2:123b", entry.Value.Match);
        Assert.True(entry.Value.Execution.SupportsTools ?? false);
    }

    [Fact]
    public void FindModelSelectionEntry_Ollama_DeepSeekV4Pro_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("deepseek-v4-pro", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("deepseek-v4-pro", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Ollama_KimiK26_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("kimi-k2.6", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("kimi-k2.6", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Ollama_Qwen3CoderNext_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("qwen3-coder-next", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("qwen3-coder-next", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Cerebras_ZaiGlm47_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("zai-glm-4.7", "cerebras");

        Assert.NotNull(entry);
        Assert.Equal("zai-glm-4.7", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Cerebras_GptOss120b_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("gpt-oss-120b", "cerebras");

        Assert.NotNull(entry);
        Assert.Equal("gpt-oss-120b", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Moonshot_KimiK25_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("kimi-k2.5", "moonshot");

        Assert.NotNull(entry);
        Assert.Equal("kimi-k2.5", entry.Value.Match);
    }

    [Fact]
    public void GetExecutionConfigForModel_OllamaCloud_Qwen3Coder480B_HasContextLength128K()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("qwen3-coder:480b", registry.ModelToProvider);

        Assert.True(config.ContextLength.HasValue);
        Assert.Equal(1_000_000, config.ContextLength.Value);
    }

    [Fact]
    public void GetExecutionConfigForModel_OllamaCloud_KimiK26_HasOverrideClientParams()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("kimi-k2.6", registry.ModelToProvider);

        // kimi-k2.6 served via Ollama Cloud inherits the moonshot override_client_params
        // rule and must always pin temperature=1.0.
        Assert.True(config.Temperature.HasValue);
        Assert.Equal(1.0, config.Temperature.Value);
        Assert.True(config.OverrideClientParams);
    }

    [Fact]
    public void GetExecutionConfigForModel_OllamaCloud_Devstral_HasLowTemperature()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("devstral-2:123b", registry.ModelToProvider);

        Assert.True(config.Temperature.HasValue);
        Assert.Equal(0.2, config.Temperature.Value);
    }

    [Fact]
    public void IsPreferredModel_OllamaCloud_Qwen3Coder480B_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("qwen3-coder:480b", "ollama");

        Assert.True(isPreferred);
    }

    [Fact]
    public void IsPreferredModel_OllamaCloud_Devstral_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("devstral-2:123b", "ollama");

        Assert.True(isPreferred);
    }

    [Fact]
    public void IsPreferredModel_Cerebras_ZaiGlm47_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("zai-glm-4.7", "cerebras");

        Assert.True(isPreferred);
    }

    [Fact]
    public void GetPreferredModelPriority_OllamaCloud_KimiK26_ReturnsExpectedPriority()
    {
        ModelSelectionStore store = new();

        int priority = store.GetPreferredModelPriority("kimi-k2.6", "ollama");

        // kimi-k2.6 is priority 7 in ollamacloud.json
        Assert.Equal(7, priority);
    }

    [Fact]
    public void GetExecutionConfigForModel_HasTemperature()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.Temperature.HasValue);
        Assert.True(config.Temperature.Value >= 0);
    }

    [Fact]
    public void GetExecutionConfigForModel_HasMaxTokensPreferred()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.MaxTokensPreferred.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_DeepSeekFlash_HasReasoningEffort()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-flash", registry.ModelToProvider);

        Assert.False(string.IsNullOrWhiteSpace(config.ReasoningEffort));
    }

    [Fact]
    public void FindModelSelectionEntry_Moonshot_KimiK26_HasTemperature10()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("kimi-k2.6", "moonshot");

        Assert.NotNull(entry);
        Assert.Equal(1.0, entry.Value.Execution.Temperature);
    }

    [Fact]
    public void GetProviderModelSelections_Cerebras_ReturnsEntries()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry[] entries = store.GetProviderModelSelections("cerebras");

        Assert.NotEmpty(entries);
    }
}