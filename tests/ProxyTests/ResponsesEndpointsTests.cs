using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyTests;

/// <summary>
/// Unit + integration tests for the /v1/responses API surface.
/// Tests request translation (Responses → Chat Completions), response translation
/// (Chat Completions → Responses), stub endpoint behaviour, and round-trip consistency.
/// </summary>
[Collection("Proxy")]
public class ResponsesEndpointsTests(ProxyFixture fixture)
{
    private readonly HttpClient _client = fixture.Client;

    // ── Type / reflection helpers ──────────────────────────────────────────

    private static readonly Type ResponsesType = typeof(Program).Assembly
        .GetType("ResponsesEndpoints", throwOnError: true)!;

    private static T CallPrivateStatic<T>(string method, params object[] args) =>
        (T)ResponsesType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;

    // ── ConvertResponsesToChatCompletions ──────────────────────────────────

    [Fact]
    public void ConvertResponsesToChatCompletions_MapsModel()
    {
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"deepseek-v4-pro","input":"hello"}""");

        using JsonDocument d = JsonDocument.Parse(result);
        Assert.Equal("deepseek-v4-pro", d.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void ConvertResponsesToChatCompletions_StringInput_MapsToSingleUserMessage()
    {
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"test","input":"hello world"}""");

        using JsonDocument d = JsonDocument.Parse(result);
        JsonElement messages = d.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        JsonElement first = messages[0];
        Assert.Equal("user", first.GetProperty("role").GetString());
        Assert.Equal("hello world", first.GetProperty("content").GetString());
    }

    [Fact]
    public void ConvertResponsesToChatCompletions_ArrayInput_MapsMessagesWithRoles()
    {
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"test","input":[{"role":"user","content":"hi"},{"role":"assistant","content":"hello!"}]}""");

        using JsonDocument d = JsonDocument.Parse(result);
        JsonElement messages = d.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void ConvertResponsesToChatCompletions_DeveloperRole_MapsToSystem()
    {
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"test","input":[{"role":"developer","content":"you are a bot"}]}""");

        using JsonDocument d = JsonDocument.Parse(result);
        JsonElement messages = d.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public void ConvertResponsesToChatCompletions_PrependsInstructionsAsSystemMessage()
    {
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"test","instructions":"be helpful","input":"hello"}""");

        using JsonDocument d = JsonDocument.Parse(result);
        JsonElement messages = d.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("be helpful", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void ConvertResponsesToChatCompletions_MapsStreamFlag()
    {
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"test","input":"hi","stream":true}""");

        using JsonDocument d = JsonDocument.Parse(result);
        Assert.True(d.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void ConvertResponsesToChatCompletions_MapsMaxOutputTokens()
    {
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"test","input":"hi","max_output_tokens":4096}""");

        using JsonDocument d = JsonDocument.Parse(result);
        Assert.Equal(4096, d.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void ConvertResponsesToChatCompletions_MapsTemperature()
    {
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"test","input":"hi","temperature":0.7}""");

        using JsonDocument d = JsonDocument.Parse(result);
        Assert.Equal(0.7, d.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void ConvertResponsesToChatCompletions_MapsTopP()
    {
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"test","input":"hi","top_p":0.9}""");

        using JsonDocument d = JsonDocument.Parse(result);
        Assert.Equal(0.9, d.RootElement.GetProperty("top_p").GetDouble());
    }

    [Fact]
    public void ConvertResponsesToChatCompletions_ContentTypesRemapped()
    {
        // input_text / output_text → text for Chat Completions.
        string result = CallPrivateStatic<string>("ConvertResponsesToChatCompletions",
            """{"model":"test","input":[{"role":"user","content":[{"type":"input_text","text":"hello"}]}]}""");

        using JsonDocument d = JsonDocument.Parse(result);
        JsonElement contentArr = d.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, contentArr.ValueKind);
        Assert.Equal("text", contentArr[0].GetProperty("type").GetString());
    }

    // ── ConvertChatCompletionsToResponses ──────────────────────────────────

    [Fact]
    public void ConvertChatCompletionsToResponses_ReturnsResponseObject()
    {
        string chatResp = """{"id":"chat-123","object":"chat.completion","model":"deepseek-v4-pro","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""";

        string result = CallPrivateStatic<string>("ConvertChatCompletionsToResponses", chatResp, "deepseek-v4-pro");

        using JsonDocument d = JsonDocument.Parse(result);
        Assert.Equal("response", d.RootElement.GetProperty("object").GetString());
        Assert.Equal("deepseek-v4-pro", d.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void ConvertChatCompletionsToResponses_MapsOutputText()
    {
        string chatResp = """{"id":"chat-1","choices":[{"message":{"role":"assistant","content":"hi there"},"finish_reason":"stop"}]}""";

        string result = CallPrivateStatic<string>("ConvertChatCompletionsToResponses", chatResp, "test");

        using JsonDocument d = JsonDocument.Parse(result);
        JsonElement output = d.RootElement.GetProperty("output");
        Assert.True(output.GetArrayLength() > 0);
        JsonElement content = output[0].GetProperty("content")[0];
        Assert.Equal("output_text", content.GetProperty("type").GetString());
        Assert.Equal("hi there", content.GetProperty("text").GetString());
    }

    [Fact]
    public void ConvertChatCompletionsToResponses_MapsToolCalls()
    {
        string chatResp = """{"id":"chat-tc","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_abc","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"NYC\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":5,"completion_tokens":3,"total_tokens":8}}""";

        string result = CallPrivateStatic<string>("ConvertChatCompletionsToResponses", chatResp, "test");

        using JsonDocument d = JsonDocument.Parse(result);
        JsonElement output = d.RootElement.GetProperty("output");
        Assert.True(output.GetArrayLength() >= 1);
        JsonElement tcItem = output[0];
        Assert.Equal("function_call", tcItem.GetProperty("type").GetString());
        Assert.Equal("get_weather", tcItem.GetProperty("name").GetString());
        Assert.Contains("NYC", tcItem.GetProperty("arguments").GetString());
    }

    [Fact]
    public void ConvertChatCompletionsToResponses_MapsUsage()
    {
        string chatResp = """{"id":"chat-1","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":10,"total_tokens":15}}""";

        string result = CallPrivateStatic<string>("ConvertChatCompletionsToResponses", chatResp, "test");

        using JsonDocument d = JsonDocument.Parse(result);
        JsonElement usage = d.RootElement.GetProperty("usage");
        Assert.Equal(5, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(10, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(15, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void ConvertChatCompletionsToResponses_MapsReasoningTokens()
    {
        string chatResp = """{"id":"chat-1","choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":10,"total_tokens":15,"completion_tokens_details":{"reasoning_tokens":3}}}""";

        string result = CallPrivateStatic<string>("ConvertChatCompletionsToResponses", chatResp, "test");

        using JsonDocument d = JsonDocument.Parse(result);
        JsonElement usage = d.RootElement.GetProperty("usage");
        Assert.Equal(3, usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32());
    }

    // ── Round-trip consistency ────────────────────────────────────────────

    [Fact]
    public void RoundTrip_SimplePrompt_ContentIsPreserved()
    {
        string request = """{"model":"test","input":"what is 2+2?"}""";

        string chatReq = CallPrivateStatic<string>("ConvertResponsesToChatCompletions", request);
        string chatResp = """{"id":"rt-1","object":"chat.completion","model":"test","choices":[{"index":0,"message":{"role":"assistant","content":"4"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":1,"total_tokens":6}}""";

        string responsesResult = CallPrivateStatic<string>("ConvertChatCompletionsToResponses", chatResp, "test");

        using JsonDocument d = JsonDocument.Parse(responsesResult);
        string outputText = d.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Equal("4", outputText);
    }

    [Fact]
    public void RoundTrip_WithInstructions_PreservesSystemContext()
    {
        string request = """{"model":"test","instructions":"you are a calculator","input":"what is 3*4?"}""";

        string chatReq = CallPrivateStatic<string>("ConvertResponsesToChatCompletions", request);

        using JsonDocument d = JsonDocument.Parse(chatReq);
        JsonElement messages = d.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("you are a calculator", messages[0].GetProperty("content").GetString());
    }

    // ── Stub endpoints ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponse_ReturnsCompletedWithWarning()
    {
        HttpResponseMessage r = await _client.GetAsync("/v1/responses/test-id-1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.Equal("response", d.RootElement.GetProperty("object").GetString());
        Assert.Equal("completed", d.RootElement.GetProperty("status").GetString());
        Assert.Equal("stub", d.RootElement.GetProperty("warning").GetString());
    }

    [Fact]
    public async Task DeleteResponse_ReturnsDeleted()
    {
        HttpResponseMessage r = await _client.DeleteAsync("/v1/responses/test-id-2");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.Equal("response.deleted", d.RootElement.GetProperty("object").GetString());
        Assert.True(d.RootElement.GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task GetInputItems_ReturnsEmptyWithWarning()
    {
        HttpResponseMessage r = await _client.GetAsync("/v1/responses/test-id-3/input_items");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, d.RootElement.GetProperty("data").ValueKind);
        Assert.Equal("stub", d.RootElement.GetProperty("warning").GetString());
    }

    [Fact]
    public async Task PostInputTokens_ReturnsZeroWithWarning()
    {
        using StringContent content = new("""{"input_tokens":100}""", Encoding.UTF8, "application/json");
        HttpResponseMessage r = await _client.PostAsync("/v1/responses/test-id-4/input_tokens", content);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.Equal(0, d.RootElement.GetProperty("input_tokens").GetInt32());
        Assert.Equal("stub", d.RootElement.GetProperty("warning").GetString());
    }

    [Fact]
    public async Task PostCancel_ReturnsCancelledWithWarning()
    {
        using StringContent content = new("{}", Encoding.UTF8, "application/json");
        HttpResponseMessage r = await _client.PostAsync("/v1/responses/test-id-5/cancel", content);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.Equal("cancelled", d.RootElement.GetProperty("status").GetString());
        Assert.Equal("stub", d.RootElement.GetProperty("warning").GetString());
    }

    [Fact]
    public async Task PostCompact_ReturnsCompletedWithWarning()
    {
        using StringContent content = new("{}", Encoding.UTF8, "application/json");
        HttpResponseMessage r = await _client.PostAsync("/v1/responses/test-id-6/compact", content);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.Equal("completed", d.RootElement.GetProperty("status").GetString());
        Assert.Equal("stub", d.RootElement.GetProperty("warning").GetString());
    }

    // ── POST /v1/responses (integration through proxy) ────────────────────

    [Fact]
    public async Task PostResponses_NonStreaming_ReturnsResponseObject()
    {
        using StringContent content = new("""{"model":"test-model","input":"say hi"}""",
            Encoding.UTF8, "application/json");

        HttpResponseMessage r = await _client.PostAsync("/v1/responses", content);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.Equal("response", d.RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public async Task PostResponses_NonStreaming_ReturnsContentFromStub()
    {
        using StringContent content = new("""{"model":"test-model","input":"say hi"}""",
            Encoding.UTF8, "application/json");

        HttpResponseMessage r = await _client.PostAsync("/v1/responses", content);
        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);

        JsonElement output = d.RootElement.GetProperty("output");
        Assert.True(output.GetArrayLength() > 0);
    }

    [Fact]
    public async Task PostResponses_Streaming_ReturnsSSE()
    {
        using StringContent content = new("""{"model":"test-model","input":"stream pls","stream":true}""",
            Encoding.UTF8, "application/json");

        HttpResponseMessage r = await _client.PostAsync("/v1/responses", content);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/event-stream", r.Content.Headers.ContentType?.MediaType);

        string body = await r.Content.ReadAsStringAsync();
        Assert.Contains("event: response.created", body);
        Assert.Contains("event: response.in_progress", body);
        Assert.Contains("event: response.output_item.added", body);
    }

    [Fact]
    public async Task PostResponses_UnknownModel_FallsBackToDefault()
    {
        // Proxy resolves unknown models to the default provider, so it should
        // succeed rather than returning 4xx.
        using StringContent content = new("""{"model":"nonexistent-model-xyz","input":"hi"}""",
            Encoding.UTF8, "application/json");

        HttpResponseMessage r = await _client.PostAsync("/v1/responses", content);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.Equal("response", d.RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public void DetectStream_True_WhenStreamFieldPresent()
    {
        bool result = CallPrivateStatic<bool>("DetectStream", """{"model":"test","input":"hi","stream":true}""");
        Assert.True(result);
    }

    [Fact]
    public void DetectStream_False_WhenStreamFieldAbsent()
    {
        bool result = CallPrivateStatic<bool>("DetectStream", """{"model":"test","input":"hi"}""");
        Assert.False(result);
    }

    [Fact]
    public void DetectStream_False_WhenStreamIsFalse()
    {
        bool result = CallPrivateStatic<bool>("DetectStream", """{"model":"test","input":"hi","stream":false}""");
        Assert.False(result);
    }

    [Fact]
    public void DetectStream_False_OnMalformedJson()
    {
        bool result = CallPrivateStatic<bool>("DetectStream", "not valid json");
        Assert.False(result);
    }
}
