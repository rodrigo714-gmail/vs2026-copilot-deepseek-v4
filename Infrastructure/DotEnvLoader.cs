/// <summary>
/// Loads a <c>.env</c> file into the process environment, filling in only what is missing.
///
/// The precedence — real environment variables beat the file — is the documented one and the one
/// every deployment expects. It used to be inverted: the loader overwrote unconditionally, so a
/// <c>.env</c> baked into an image silently beat <c>docker run -e</c>, compose <c>environment:</c>
/// and Kubernetes env vars, and the container quietly ignored the configuration it was given.
/// </summary>
internal static class DotEnvLoader
{
    /// <summary>Result of a load, so the caller can report what it chose to ignore.</summary>
    /// <param name="Path">The file that was read, or null when none was found.</param>
    /// <param name="Applied">Keys taken from the file.</param>
    /// <param name="SkippedBecauseAlreadySet">
    /// Keys present in the file but already set in the environment. Reported rather than silently
    /// dropped — an ignored line in <c>.env</c> is the kind of thing that costs an afternoon.
    /// </param>
    internal readonly record struct LoadResult(string? Path, IReadOnlyList<string> Applied, IReadOnlyList<string> SkippedBecauseAlreadySet);

    /// <summary>
    /// Finds a <c>.env</c> next to the binary, then in the working directory, and applies it.
    /// </summary>
    internal static LoadResult LoadFromDefaultLocations()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, ".env");
        if (!File.Exists(path))
            path = System.IO.Path.Combine(Directory.GetCurrentDirectory(), ".env");

        return File.Exists(path)
            ? Apply(File.ReadAllLines(path), path)
            : new LoadResult(null, [], []);
    }

    /// <summary>
    /// Applies parsed lines to the process environment. Exposed for tests so the precedence rule
    /// can be verified without writing a file next to the test binary.
    /// </summary>
    internal static LoadResult Apply(IEnumerable<string> lines, string? path = null)
    {
        List<string> applied = [];
        List<string> skipped = [];

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            int eq = trimmed.IndexOf('=');
            if (eq < 1)
                continue;

            string key = trimmed[..eq].Trim();
            string value = trimmed[(eq + 1)..].Trim().Trim('"');
            if (string.IsNullOrEmpty(key))
                continue;

            // An empty value in .env means "not configured" and must not mask a real variable.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                skipped.Add(key);
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
            applied.Add(key);
        }

        return new LoadResult(path, applied, skipped);
    }
}
