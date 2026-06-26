public record struct ModelSelectionEntry(string Match, int Priority, bool Enabled, ModelExecutionConfig Execution, string? Upstream = null);
