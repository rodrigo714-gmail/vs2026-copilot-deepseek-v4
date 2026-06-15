public record struct ProviderInfo(string Name, string ApiKey, string BaseUrl, HttpClient Client, ProviderCapabilities Capabilities);
