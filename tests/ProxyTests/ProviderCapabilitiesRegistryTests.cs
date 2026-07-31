using System.Text.Json;

namespace ProxyTests;

/// <summary>
/// Guard tests for the promise in <c>ProviderCapabilitiesRegistry</c>'s doc comment: adding a
/// provider is one registry entry plus one <c>config/model-selection/{provider}.json</c>.
///
/// That promise used to be false — three separate per-provider <c>switch</c> statements
/// (display name ×2, billing) and a fourth per-provider price table had to be patched too, and
/// nothing caught it when they weren't. Two of them had already drifted: neither knew about
/// <c>zai</c>. These tests make the next omission a build failure rather than a silent
/// half-configured provider.
/// </summary>
public sealed class ProviderCapabilitiesRegistryTests
{
    public static TheoryData<string> AllProviders()
    {
        var data = new TheoryData<string>();
        foreach (string name in ProviderCapabilitiesRegistry.KnownProviders)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void EveryProvider_DeclaresItsRequiredFields(string provider)
    {
        ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(provider);

        Assert.False(string.IsNullOrWhiteSpace(caps.ChatPath), $"'{provider}' has no ChatPath.");
        Assert.False(string.IsNullOrWhiteSpace(caps.ModelsPath), $"'{provider}' has no ModelsPath.");
        Assert.False(string.IsNullOrWhiteSpace(caps.DefaultBaseUrl), $"'{provider}' has no DefaultBaseUrl.");
        Assert.False(string.IsNullOrWhiteSpace(caps.EnvPrefix), $"'{provider}' has no EnvPrefix.");
        Assert.False(string.IsNullOrWhiteSpace(caps.DisplayName), $"'{provider}' has no DisplayName — the dashboard would show a bare, capitalised id.");
    }

    /// <summary>
    /// Chat and models paths are appended to the provider's base address as *relative* URIs.
    /// A leading slash would make HttpClient resolve them against the host root and drop any
    /// path segment in the base URL — which is exactly how Z.AI's <c>/api/paas/v4</c> and
    /// Groq's <c>/openai</c> would silently disappear.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProviders))]
    public void EveryProvider_UsesRelativePaths(string provider)
    {
        ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(provider);

        Assert.False(caps.ChatPath.StartsWith('/'), $"'{provider}' ChatPath must be relative, got '{caps.ChatPath}'.");
        Assert.False(caps.ModelsPath.StartsWith('/'), $"'{provider}' ModelsPath must be relative, got '{caps.ModelsPath}'.");
    }

    /// <summary>
    /// Two providers sharing an EnvPrefix would read the same PROVIDER_{prefix}_API_KEY, so
    /// configuring one would silently authenticate the other against the wrong endpoint.
    /// </summary>
    [Fact]
    public void EnvPrefixes_AreUnique()
    {
        var byPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string provider in ProviderCapabilitiesRegistry.KnownProviders)
        {
            string prefix = ProviderCapabilitiesRegistry.Get(provider).EnvPrefix;
            Assert.False(byPrefix.TryGetValue(prefix, out string? other),
                $"Providers '{other}' and '{provider}' both use EnvPrefix '{prefix}'.");
            byPrefix[prefix] = provider;
        }
    }

    [Fact]
    public void DefaultBaseUrls_AreUnique()
    {
        var byUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string provider in ProviderCapabilitiesRegistry.KnownProviders)
        {
            string url = ProviderCapabilitiesRegistry.Get(provider).DefaultBaseUrl;
            Assert.False(byUrl.TryGetValue(url, out string? other),
                $"Providers '{other}' and '{provider}' both default to '{url}'.");
            byUrl[url] = provider;
        }
    }

    [Fact]
    public void DisplayNames_AreUnique()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string provider in ProviderCapabilitiesRegistry.KnownProviders)
        {
            string display = ProviderCapabilitiesRegistry.DisplayName(provider);
            Assert.False(byName.TryGetValue(display, out string? other),
                $"Providers '{other}' and '{provider}' both display as '{display}'.");
            byName[display] = provider;
        }
    }

    [Fact]
    public void DisplayName_FallsBackToCapitalisedName_ForUnknownProvider()
    {
        Assert.Equal("Nosuchprovider", ProviderCapabilitiesRegistry.DisplayName("nosuchprovider"));
    }

    /// <summary>
    /// The filename under config/model-selection/ is cosmetic — the "provider" field inside
    /// binds the file to a provider, and exactly one file may declare a given provider.
    /// A provider with no config file falls back to a hardcoded default model list.
    /// </summary>
    [Fact]
    public void EveryProvider_IsDeclaredByExactlyOneModelSelectionFile()
    {
        string configDir = FindModelSelectionDirectory();
        var declaredBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(configDir, "*.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("provider", out JsonElement p) || p.ValueKind != JsonValueKind.String)
                continue;

            string provider = p.GetString()!;
            if (!declaredBy.TryGetValue(provider, out List<string>? files))
                declaredBy[provider] = files = [];
            files.Add(Path.GetFileName(file));
        }

        foreach (string provider in ProviderCapabilitiesRegistry.KnownProviders)
        {
            Assert.True(declaredBy.TryGetValue(provider, out List<string>? files),
                $"Provider '{provider}' has no config/model-selection file declaring it.");
            Assert.True(files!.Count == 1,
                $"Provider '{provider}' is declared by {files.Count} files: {string.Join(", ", files)}.");
        }

        foreach ((string provider, List<string> files) in declaredBy)
        {
            Assert.True(ProviderCapabilitiesRegistry.IsKnownProvider(provider),
                $"{string.Join(", ", files)} declares provider '{provider}', which is not in ProviderCapabilitiesRegistry.");
        }
    }

    private static string FindModelSelectionDirectory()
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(root);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "config", "model-selection");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate config/model-selection.");
    }
}
