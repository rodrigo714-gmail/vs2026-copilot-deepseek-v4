namespace ProxyTests;

public sealed class PricingCatalogTests
{
    [Fact]
    public void DeepSeekFlash_HasCorrectPrice()
    {
        var p = PricingCatalog.Get("deepseek", "deepseek-v4-flash");
        Assert.NotNull(p);
        Assert.Equal(0.14m, p.Value.InputPerMillion);
        Assert.Equal(0.28m, p.Value.OutputPerMillion);
        Assert.Equal(0.0028m, p.Value.CachedInputPerMillion);
        Assert.Equal("paid", p.Value.Tier);
    }

    [Fact]
    public void GLM47Flash_IsFree()
    {
        var p = PricingCatalog.Get("zai", "glm-4.7-flash");
        Assert.NotNull(p);
        Assert.Equal("free", p.Value.Tier);
        Assert.Equal(0, p.Value.InputPerMillion);
        Assert.Equal(0, p.Value.OutputPerMillion);
    }

    [Fact]
    public void ClaudeFable5_TopTier()
    {
        var p = PricingCatalog.Get("anthropic", "claude-fable-5");
        Assert.NotNull(p);
        Assert.Equal("premium", p.Value.Tier);
        Assert.Equal(1508, p.Value.ArenaElo);
        Assert.Equal(1654, p.Value.ArenaWebDev);
        Assert.Equal(14.00m, p.Value.ArenaAgentWinRate);
    }

    [Fact]
    public void UnknownModel_ReturnsNull()
    {
        var p = PricingCatalog.Get("deepseek", "nonexistent-model-v99");
        Assert.Null(p);
    }

    [Fact]
    public void EstimateCost_ComputesCorrectly()
    {
        var p = PricingCatalog.Get("deepseek", "deepseek-v4-flash");
        Assert.NotNull(p);
        // 5000 input + 1000 output tokens
        decimal cost = p.Value.EstimateCost(5000, 1000);
        decimal expected = (5000m / 1_000_000m) * 0.14m + (1000m / 1_000_000m) * 0.28m;
        Assert.Equal(expected, cost);
    }

    [Fact]
    public void FreeModel_EstimateCost_ReturnsZero()
    {
        var p = PricingCatalog.Get("zai", "glm-4.7-flash");
        Assert.NotNull(p);
        Assert.Equal(0, p.Value.EstimateCost(1_000_000, 500_000));
    }

    [Fact]
    public void NVIDIA_NIM_IsFree()
    {
        var p = PricingCatalog.Get("nvidia", "nvidia/nemotron-3-120b-a12b");
        Assert.NotNull(p);
        Assert.Equal("free", p.Value.Tier);
    }

    [Fact]
    public void ProviderDefault_FallbackWorks()
    {
        var p = PricingCatalog.Get("cerebras", "any-model");
        Assert.NotNull(p);
        Assert.Equal("cerebras", p.Value.Provider);
        Assert.Equal(0.10m, p.Value.InputPerMillion);
    }

    [Fact]
    public void ArenaData_PresentForTopModels()
    {
        var gpt55 = PricingCatalog.Get("openai", "gpt-5.5");
        Assert.NotNull(gpt55);
        Assert.Equal(1481, gpt55.Value.ArenaElo);
        Assert.Equal(8.04m, gpt55.Value.ArenaAgentWinRate);

        var glm52 = PricingCatalog.Get("zai", "glm-5.2");
        Assert.NotNull(glm52);
        Assert.Equal(1481, glm52.Value.ArenaElo);
        Assert.Equal(1593, glm52.Value.ArenaWebDev);
    }

    [Fact]
    public void All_ReturnsAllEntries()
    {
        var all = PricingCatalog.All.ToList();
        Assert.NotEmpty(all);
        Assert.True(all.Count >= 30);
    }
}
