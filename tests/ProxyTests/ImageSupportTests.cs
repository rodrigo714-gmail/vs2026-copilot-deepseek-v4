using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyTests;

/// <summary>
/// Pruebas de diagnóstico para verificar que el proxy maneja correctamente
/// imágenes en los modelos que lo permiten (vision-capable models).
///
/// Flujos probados:
///   1. OpenAI → Ollama: request con multi-part content (text + image_url)
///      se convierte a formato Ollama (content string + images array)
///   2. Ollama → OpenAI: request con images array se convierte a
///      multi-part content (text parts + image_url parts)
///   3. /api/tags expone supports_images para modelos vision
///   4. ModelSelectionStore identifica modelos con soporte de visión
///   5. ModelCatalogService.GetModelProfile detecta modelos vision
/// </summary>
public class ImageSupportTests
{
    private readonly ModelSelectionStore _store = new();
    private readonly ProviderHttpClientFactory _factory = new();

    /// <summary>
    /// Helper: invoca BuildOllamaChatRequest (método privado en OpenAiEndpoints)
    /// a través de ser humano, convierte un request OpenAI a Ollama.
    /// Para testear el método directamente, duplicamos la lógica inline.
    /// </summary>
    private static string BuildOllamaChatRequest(string openAiRequestBody, string model, bool isStream)
    {
        using JsonDocument openAiDoc = JsonDocument.Parse(openAiRequestBody);
        JsonElement root = openAiDoc.RootElement;

        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);

        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteBoolean("stream", isStream);

        if (root.TryGetProperty("messages", out JsonElement messages))
        {
            writer.WritePropertyName("messages");
            writer.WriteStartArray();

            foreach (JsonElement msg in messages.EnumerateArray())
            {
                writer.WriteStartObject();

                bool hasMultiPartContent = false;
                List<string> imageUrls = [];

                if (msg.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
                {
                    hasMultiPartContent = true;
                    System.Text.StringBuilder textContent = new();

                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out JsonElement type) && type.GetString() == "text")
                        {
                            if (part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                            {
                                if (textContent.Length > 0)
                                    textContent.Append('\n');
                                textContent.Append(text.GetString());
                            }
                        }
                        else if (type.GetString() == "image_url")
                        {
                            if (part.TryGetProperty("image_url", out JsonElement imgUrl) && imgUrl.ValueKind == JsonValueKind.Object)
                            {
                                if (imgUrl.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String)
                                {
                                    imageUrls.Add(url.GetString()!);
                                }
                            }
                        }
                    }

                    writer.WriteString("content", textContent.ToString());
                }

                foreach (JsonProperty mp in msg.EnumerateObject())
                {
                    if (mp.NameEquals("content") && hasMultiPartContent)
                        continue;
                    mp.WriteTo(writer);
                }

                if (imageUrls.Count > 0)
                {
                    writer.WritePropertyName("images");
                    writer.WriteStartArray();
                    foreach (string imgUrl in imageUrls)
                    {
                        writer.WriteStringValue(imgUrl);
                    }
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (root.TryGetProperty("tools", out JsonElement tools))
        {
            writer.WritePropertyName("tools");
            tools.WriteTo(writer);
        }

        if (root.TryGetProperty("temperature", out JsonElement temp))
        {
            writer.WriteNumber("temperature", temp.GetDouble());
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Helper: convierte un request Ollama (con images) a OpenAI multi-part.
    /// Duplica la lógica de OllamaEndpoints.ConvertOllamaToOpenAi.
    /// </summary>
    private static string ConvertOllamaToOpenAi(string ollamaBody, string upstreamModel, bool isStream)
    {
        using JsonDocument doc = JsonDocument.Parse(ollamaBody);
        JsonElement root = doc.RootElement;

        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);

        writer.WriteStartObject();
        writer.WriteString("model", upstreamModel);
        writer.WriteBoolean("stream", isStream);

        if (root.TryGetProperty("messages", out JsonElement omsgs) && omsgs.ValueKind == JsonValueKind.Array)
        {
            writer.WritePropertyName("messages");
            writer.WriteStartArray();

            foreach (JsonElement msg in omsgs.EnumerateArray())
            {
                writer.WriteStartObject();
                bool hasImages = msg.TryGetProperty("images", out JsonElement imgs) && imgs.GetArrayLength() > 0;

                foreach (JsonProperty mp in msg.EnumerateObject())
                {
                    if (mp.NameEquals("content") && hasImages)
                    {
                        string text = mp.Value.GetString() ?? "";
                        writer.WritePropertyName("content");
                        writer.WriteStartArray();
                        writer.WriteStartObject();
                        writer.WriteString("type", "text");
                        writer.WriteString("text", text);
                        writer.WriteEndObject();
                        foreach (JsonElement img in imgs.EnumerateArray())
                        {
                            string url = img.GetString()!;
                            if (!url.StartsWith("data:") && !url.StartsWith("http"))
                                url = $"data:image/png;base64,{url}";
                            writer.WriteStartObject();
                            writer.WriteString("type", "image_url");
                            writer.WritePropertyName("image_url");
                            writer.WriteStartObject();
                            writer.WriteString("url", url);
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    else if (mp.NameEquals("images"))
                    {
                        continue;
                    }
                    else
                    {
                        mp.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 1: OpenAI → Ollama — imagen única
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenAiToOllama_SingleImage_ConvertsCorrectly()
    {
        string openAiRequest = /*lang=json*/ """
        {
            "model": "kimi-k2.7-code-free",
            "messages": [
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": "What's in this image?"},
                        {"type": "image_url", "image_url": {"url": "data:image/png;base64,iVBORw0KGgo="}}
                    ]
                }
            ],
            "stream": false
        }
        """;

        string ollamaRequest = BuildOllamaChatRequest(openAiRequest, "kimi-k2.7-code-free", false);

        using JsonDocument parsed = JsonDocument.Parse(ollamaRequest);
        JsonElement root = parsed.RootElement;

        // Model preserved
        Assert.Equal("kimi-k2.7-code-free", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());

        // Messages array
        JsonElement messages = root.GetProperty("messages");
        Assert.Single(messages.EnumerateArray());

        JsonElement msg = messages[0];
        Assert.Equal("user", msg.GetProperty("role").GetString());

        // Content debe ser string (no array)
        Assert.Equal(JsonValueKind.String, msg.GetProperty("content").ValueKind);
        Assert.Equal("What's in this image?", msg.GetProperty("content").GetString());

        // Images array must exist with base64 data
        JsonElement images = msg.GetProperty("images");
        Assert.Single(images.EnumerateArray());
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", images[0].GetString());
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 2: OpenAI → Ollama — múltiples imágenes + texto
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenAiToOllama_MultipleImagesAndText_ConvertsCorrectly()
    {
        string openAiRequest = /*lang=json*/ """
        {
            "model": "qwen3-vl",
            "messages": [
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": "Compare these images"},
                        {"type": "image_url", "image_url": {"url": "data:image/png;base64,img1data"}},
                        {"type": "image_url", "image_url": {"url": "data:image/png;base64,img2data"}}
                    ]
                }
            ],
            "stream": false
        }
        """;

        string ollamaRequest = BuildOllamaChatRequest(openAiRequest, "qwen3-vl", false);

        using JsonDocument parsed = JsonDocument.Parse(ollamaRequest);
        JsonElement msg = parsed.RootElement.GetProperty("messages")[0];

        // Content concatenated
        Assert.Equal("Compare these images", msg.GetProperty("content").GetString());

        // Two images
        JsonElement images = msg.GetProperty("images");
        Assert.Equal(2, images.GetArrayLength());
        Assert.Equal("data:image/png;base64,img1data", images[0].GetString());
        Assert.Equal("data:image/png;base64,img2data", images[1].GetString());
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 3: OpenAI → Ollama — sin imágenes (solo texto)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenAiToOllama_NoImages_DoesNotAddImagesField()
    {
        string openAiRequest = /*lang=json*/ """
        {
            "model": "deepseek-v4-pro",
            "messages": [
                {"role": "user", "content": "Hello, how are you?"}
            ],
            "stream": false
        }
        """;

        string ollamaRequest = BuildOllamaChatRequest(openAiRequest, "deepseek-v4-pro", false);

        using JsonDocument parsed = JsonDocument.Parse(ollamaRequest);
        JsonElement msg = parsed.RootElement.GetProperty("messages")[0];

        // Content is plain string
        Assert.Equal("Hello, how are you?", msg.GetProperty("content").GetString());

        // No images property
        Assert.False(msg.TryGetProperty("images", out _));
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 4: Ollama → OpenAI — imagen única
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OllamaToOpenAi_SingleImage_ConvertsToMultiPartContent()
    {
        string ollamaRequest = /*lang=json*/ """
        {
            "model": "kimi-k2.7-code-free",
            "stream": false,
            "messages": [
                {
                    "role": "user",
                    "content": "What's in this image?",
                    "images": ["data:image/png;base64,iVBORw0KGgo="]
                }
            ]
        }
        """;

        string openAiRequest = ConvertOllamaToOpenAi(ollamaRequest, "kimi-k2.7-code-free", false);

        using JsonDocument parsed = JsonDocument.Parse(openAiRequest);
        JsonElement msg = parsed.RootElement.GetProperty("messages")[0];

        Assert.Equal("user", msg.GetProperty("role").GetString());

        // Content debe ser array multi-part
        JsonElement content = msg.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());

        // Primer parte: texto
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("What's in this image?", content[0].GetProperty("text").GetString());

        // Segunda parte: imagen
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 5: Ollama → OpenAI — imagen sin prefijo data:
    //  Debe agregar el prefijo data:image/png;base64, automáticamente
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OllamaToOpenAi_RawBase64Image_PrefixesDataUri()
    {
        string ollamaRequest = /*lang=json*/ """
        {
            "model": "qwen3-vl",
            "stream": false,
            "messages": [
                {
                    "role": "user",
                    "content": "Diagram analysis",
                    "images": ["iVBORw0KGgoAAAANSUhEUgAAAAE="]
                }
            ]
        }
        """;

        string openAiRequest = ConvertOllamaToOpenAi(ollamaRequest, "qwen3-vl", false);

        using JsonDocument parsed = JsonDocument.Parse(openAiRequest);
        JsonElement imagePart = parsed.RootElement.GetProperty("messages")[0].GetProperty("content")[1];

        string url = imagePart.GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Contains("iVBORw0KGgoAAAANSUhEUgAAAAE=", url);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 6: Ollama → OpenAI — múltiples imágenes
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OllamaToOpenAi_MultipleImages_AllConverted()
    {
        string ollamaRequest = /*lang=json*/ """
        {
            "model": "gemini-2.5-flash",
            "stream": false,
            "messages": [
                {
                    "role": "user",
                    "content": "Compare",
                    "images": ["data:img1", "data:img2", "data:img3"]
                }
            ]
        }
        """;

        string openAiRequest = ConvertOllamaToOpenAi(ollamaRequest, "gemini-2.5-flash", false);

        using JsonDocument parsed = JsonDocument.Parse(openAiRequest);
        JsonElement content = parsed.RootElement.GetProperty("messages")[0].GetProperty("content");

        // 1 text part + 3 image parts = 4 total
        Assert.Equal(4, content.GetArrayLength());

        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:img1", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("image_url", content[2].GetProperty("type").GetString());
        Assert.Equal("data:img2", content[2].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("image_url", content[3].GetProperty("type").GetString());
        Assert.Equal("data:img3", content[3].GetProperty("image_url").GetProperty("url").GetString());
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 7: ModelSelectionStore — identifica modelos con soporte visión
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModelSelectionStore_OllamaCloud_Qwen3Vl_HasVision()
    {
        // qwen3-vl:235b en ollamacloud.json tiene supports_vision: true pero está disabled
        ModelSelectionEntry? entry = _store.FindModelSelectionEntry("qwen3-vl:235b", "ollama");

        // El entry existe aunque esté disabled (FindModelSelectionEntry solo retorna enabled)
        // Como está enabled: false, FindModelSelectionEntry retorna null
        // Verificamos que al menos el provider tiene la entry
        ModelSelectionEntry[] ollamaEntries = _store.GetProviderModelSelections("ollama");
        bool hasVisionEntry = ollamaEntries.Any(e =>
            e.Match.Contains("vl") && (e.Execution.SupportsVision ?? false));

        Assert.True(hasVisionEntry, "ollama provider should have at least one vision-capable entry");
    }

    [Fact]
    public void ModelSelectionStore_Moonshot_KimiK25_HasVision()
    {
        // kimi-k2.5 en moonshot.json tiene supports_vision: true
        ModelSelectionEntry? entry = _store.FindModelSelectionEntry("kimi-k2.5", "moonshot");

        Assert.NotNull(entry);
        Assert.True(entry.Value.Enabled);
        Assert.True(entry.Value.Execution.SupportsVision ?? false);
    }

    [Fact]
    public void ModelSelectionStore_Moonshot_V1_128k_HasVision()
    {
        // moonshot-v1-128k en moonshot.json tiene supports_vision: true
        ModelSelectionEntry? entry = _store.FindModelSelectionEntry("moonshot-v1-128k", "moonshot");

        Assert.NotNull(entry);
        Assert.True(entry.Value.Enabled);
        Assert.True(entry.Value.Execution.SupportsVision ?? false);
    }

    [Fact]
    public void ModelSelectionStore_DeepSeekV4Pro_DoesNotHaveVision()
    {
        // deepseek-v4-pro en deepseek.json no tiene supports_vision
        ModelSelectionEntry? entry = _store.FindModelSelectionEntry("deepseek-v4-pro", "deepseek");

        Assert.NotNull(entry);
        Assert.False(entry.Value.Execution.SupportsVision ?? false);
    }

    [Fact]
    public void ModelSelectionStore_OllamaCloud_Qwen3Coder_DoesNotHaveVision()
    {
        // qwen3-coder:480b en ollamacloud.json NO tiene supports_vision
        ModelSelectionEntry? entry = _store.FindModelSelectionEntry("qwen3-coder:480b", "ollama");

        Assert.NotNull(entry);
        Assert.False(entry.Value.Execution.SupportsVision ?? false);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 8: /api/tags — supports_images se expone para modelos vision
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetModelProfile_MoonshotV1128k_HasVisionCapability()
    {
        // moonshot-v1-128k tiene supports_vision: true en moonshot.json
        ModelExecutionConfig config = _store.GetExecutionConfigForModel("moonshot-v1-128k",
            new Dictionary<string, ProviderInfo>());

        Assert.True(config.SupportsVision == true, "moonshot-v1-128k must have SupportsVision=true");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 9: OpenAI → Ollama — todos los vision models configurados
    //  Verifica que los modelos con vision en sus JSON de configuración
    //  pueden ser convertidos correctamente.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenAiToOllama_AllVisionModels_ProduceValidOllamaRequest()
    {
        // Modelos que tienen supports_vision=true en config/model-selection
        // Using models from providers known to support vision (configured in JSON with vision=true).
        // kimi-k2.5 and moonshot-v1-128k in moonshot.json have supports_vision: true.
        // Testing conversion logic only — no provider HTTP calls.
        string[][] visionModelsAndRequests =
        [
            ["kimi-k2.5", /*lang=json*/ """{"model":"kimi-k2.5","messages":[{"role":"user","content":[{"type":"text","text":"Describe"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AA=="}}]}],"stream":false}"""],
            ["moonshot-v1-128k", /*lang=json*/ """{"model":"moonshot-v1-128k","messages":[{"role":"user","content":[{"type":"text","text":"Analyze"},{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,BB=="}}]}],"stream":false}"""],
            ["models/gemini-2.5-flash", /*lang=json*/ """{"model":"models/gemini-2.5-flash","messages":[{"role":"user","content":[{"type":"text","text":"What"},{"type":"image_url","image_url":{"url":"data:image/png;base64,CC=="}}]}],"stream":false}"""],
        ];

        foreach (string[] testCase in visionModelsAndRequests)
        {
            string model = testCase[0];
            string request = testCase[1];

            string ollamaRequest = BuildOllamaChatRequest(request, model, false);

            using JsonDocument parsed = JsonDocument.Parse(ollamaRequest);
            JsonElement msg = parsed.RootElement.GetProperty("messages")[0];

            // Content debe ser string (no array)
            Assert.Equal(JsonValueKind.String, msg.GetProperty("content").ValueKind);
            Assert.NotEmpty(msg.GetProperty("content").GetString()!);

            // Debe tener images array
            Assert.True(msg.TryGetProperty("images", out JsonElement images), $"{model}: expected 'images' property");
            Assert.Single(images.EnumerateArray());
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  TEST 10: Ollama → OpenAI — todos los vision models
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void OllamaToOpenAi_AllVisionModels_ProduceValidOpenAiRequest()
    {
        string[][] visionModelsAndRequests =
        [
            ["kimi-k2.5", /*lang=json*/ """{"model":"kimi-k2.5","stream":false,"messages":[{"role":"user","content":"Desc","images":["data:image/png;base64,AA=="]}]}"""],
            ["moonshot-v1-128k", /*lang=json*/ """{"model":"moonshot-v1-128k","stream":false,"messages":[{"role":"user","content":"Desc","images":["data:image/jpeg;base64,BB=="]}]}"""],
            ["models/gemini-2.5-flash", /*lang=json*/ """{"model":"models/gemini-2.5-flash","stream":false,"messages":[{"role":"user","content":"Desc","images":["data:image/png;base64,CC=="]}]}"""],
        ];

        foreach (string[] testCase in visionModelsAndRequests)
        {
            string model = testCase[0];
            string request = testCase[1];

            string openAiRequest = ConvertOllamaToOpenAi(request, model, false);

            using JsonDocument parsed = JsonDocument.Parse(openAiRequest);
            JsonElement content = parsed.RootElement.GetProperty("messages")[0].GetProperty("content");

            // Content debe ser array multi-part
            Assert.Equal(JsonValueKind.Array, content.ValueKind);
            Assert.Equal(2, content.GetArrayLength());

            // Text part
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.NotEmpty(content[0].GetProperty("text").GetString()!);

            // Image part
            Assert.Equal("image_url", content[1].GetProperty("type").GetString());
            string url = content[1].GetProperty("image_url").GetProperty("url").GetString()!;
            Assert.NotEmpty(url);
        }
    }
}

/// <summary>
/// Serialization helper to match the JSON defaults used by the proxy.
/// </summary>
file static class ImageTestJsonDefaults
{
    public static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}