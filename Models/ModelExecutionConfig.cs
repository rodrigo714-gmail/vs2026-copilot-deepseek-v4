public record struct ModelExecutionConfig(
    int? ContextLength = null,
    int? MaxOutputTokens = null,
    bool? SupportsTools = null,
    bool? SupportsVision = null,
    string? Family = null,
    double? Temperature = null,
    double? TopP = null,
    int? MaxTokensPreferred = null,
    string? ReasoningEffort = null,
    int? TimeoutSeconds = null,
    bool OverrideClientParams = false,
    bool? SupportsReasoning = null
);
