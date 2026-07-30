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
    bool? SupportsReasoning = null,
    // OpenAI's GPT-5.x and o-series reject "max_tokens" and require
    // "max_completion_tokens" instead. Set via execution.uses_max_completion_tokens.
    bool UsesMaxCompletionTokens = false,
    // Those same models also reject any temperature/top_p other than the default.
    // null = unspecified (treated as supported). Set via execution.supports_temperature.
    bool? SupportsTemperature = null
);
