namespace ProxyTests;

/// <summary>
/// The precedence rule: a variable already present in the real environment beats the `.env` file.
///
/// It used to be the other way round, which inverted the documented order and made deployment
/// surprising — a `.env` inside an image silently beat `docker run -e`, compose `environment:`
/// and Kubernetes env vars, so the container ignored the configuration it was handed.
///
/// These tests mutate process environment variables, so they belong to the "Proxy" collection to
/// avoid racing the fixtures that boot <c>Program.cs</c>.
/// </summary>
[Collection("Proxy")]
public sealed class DotEnvLoaderTests : IDisposable
{
    private const string Key = "AI_PROXY_HUB_DOTENV_TEST_KEY";
    private const string Other = "AI_PROXY_HUB_DOTENV_TEST_OTHER";

    private readonly string? _previousKey = Environment.GetEnvironmentVariable(Key);
    private readonly string? _previousOther = Environment.GetEnvironmentVariable(Other);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Key, _previousKey);
        Environment.SetEnvironmentVariable(Other, _previousOther);
    }

    [Fact]
    public void RealEnvironmentVariable_WinsOverTheFile()
    {
        Environment.SetEnvironmentVariable(Key, "from-environment");

        var result = DotEnvLoader.Apply([$"{Key}=from-dotenv"]);

        Assert.Equal("from-environment", Environment.GetEnvironmentVariable(Key));
        Assert.Contains(Key, result.SkippedBecauseAlreadySet);
        Assert.DoesNotContain(Key, result.Applied);
    }

    [Fact]
    public void FileFillsInWhatTheEnvironmentDoesNotSet()
    {
        Environment.SetEnvironmentVariable(Key, null);

        var result = DotEnvLoader.Apply([$"{Key}=from-dotenv"]);

        Assert.Equal("from-dotenv", Environment.GetEnvironmentVariable(Key));
        Assert.Contains(Key, result.Applied);
        Assert.Empty(result.SkippedBecauseAlreadySet);
    }

    [Fact]
    public void EmptyEnvironmentValue_IsTreatedAsUnset()
    {
        // An exported-but-empty variable means "not configured", so the file should still fill it.
        Environment.SetEnvironmentVariable(Key, "");

        DotEnvLoader.Apply([$"{Key}=from-dotenv"]);

        Assert.Equal("from-dotenv", Environment.GetEnvironmentVariable(Key));
    }

    [Fact]
    public void EmptyValueInTheFile_DoesNotMaskARealVariable()
    {
        // `.env.example` ships commented placeholders like `PROXY_API_KEY=`; an uncommented empty
        // line must not wipe a key supplied by the environment.
        Environment.SetEnvironmentVariable(Key, "from-environment");

        DotEnvLoader.Apply([$"{Key}="]);

        Assert.Equal("from-environment", Environment.GetEnvironmentVariable(Key));
    }

    [Theory]
    [InlineData("# a comment")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-equals-sign")]
    [InlineData("=value-with-no-key")]
    public void MalformedLines_AreIgnored(string line)
    {
        var result = DotEnvLoader.Apply([line]);

        Assert.Empty(result.Applied);
        Assert.Empty(result.SkippedBecauseAlreadySet);
    }

    [Fact]
    public void QuotesAreStripped_AndValuesMayContainEqualsSigns()
    {
        Environment.SetEnvironmentVariable(Key, null);

        DotEnvLoader.Apply([$"{Key}=\"base64==value\""]);

        Assert.Equal("base64==value", Environment.GetEnvironmentVariable(Key));
    }

    [Fact]
    public void MixedFile_AppliesSomeAndSkipsOthers()
    {
        Environment.SetEnvironmentVariable(Key, "from-environment");
        Environment.SetEnvironmentVariable(Other, null);

        var result = DotEnvLoader.Apply(
        [
            "# comment",
            $"{Key}=from-dotenv",
            $"{Other}=from-dotenv",
        ]);

        Assert.Equal("from-environment", Environment.GetEnvironmentVariable(Key));
        Assert.Equal("from-dotenv", Environment.GetEnvironmentVariable(Other));
        Assert.Equal([Other], result.Applied);
        Assert.Equal([Key], result.SkippedBecauseAlreadySet);
    }

    [Fact]
    public void NoFileFound_IsNotAnError()
    {
        var result = DotEnvLoader.Apply([]);

        Assert.Null(result.Path);
        Assert.Empty(result.Applied);
    }
}
