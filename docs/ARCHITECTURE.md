# System Architecture

Comprehensive architecture documentation describing the proxy design, components, and data flow.

## Table of Contents

- [Overview](#overview)
- [Component Architecture](#component-architecture)
- [Data Flow](#data-flow)
- [Service Dependencies](#service-dependencies)
- [Configuration Management](#configuration-management)
- [Request Lifecycle](#request-lifecycle)
- [Model Resolution & 3-Level Hint Solver](#model-resolution--3-level-hint-solver)
- [Force-Mode Parameter Override](#force-mode-parameter-override)
- [Failing Over](#failing-over)
- [Performance Optimizations](#performance-optimizations)

---

## Overview

The proxy is a high-performance ASP.NET Core minimal API application that bridges GitHub Copilot, Cursor, Continue.dev, Visual Studio BYOM, and Ollama clients to **eight** AI providers:

- DeepSeek
- OpenAI
- NVIDIA NIM
- Groq
- OpenRouter
- Ollama Cloud
- Moonshot / Kimi
- Cerebras

### Design Principles

1. **Multi-Provider Agnostic** — One API surface, N backends
2. **Zero Allocation Streaming** — Pass-through SSE without buffering
3. **Configuration-Driven** — Model defaults, routing, and force-mode flags via JSON
4. **Testability** — All services are unit-testable with in-memory fixtures
5. **Production-Ready** — Connection pooling, HTTP/2, timeout handling
6. **Curated, Not Exhaustive** — 5 enabled models per provider; chosen for coding in VS 2026 via GitHub Copilot

### Technology Stack

- **Runtime:** .NET 10
- **Web Framework:** ASP.NET Core Minimal APIs (`WebApplication.CreateSlimBuilder`)
- **Serialization:** System.Text.Json
- **HTTP Client:** `SocketsHttpHandler` with 256 connections/server + HTTP/2 multiplexing
- **Testing:** xUnit 2.9.3 + `Microsoft.AspNetCore.Mvc.Testing` — **329 tests** in 14 files

---

## Component Architecture

### Application Startup (`Program.cs`)

```csharp
// 1. Create slim builder (minimal middleware overhead)
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

// 2. Load .env (if present) and merge into configuration
DotEnv.LoadIfPresent(builder.Configuration);

// 3. Register core services — all are singletons
builder.Services.AddSingleton<ProviderHttpClientFactory>();      // per-provider HttpClient
builder.Services.AddSingleton<ProviderRegistry>();               // DiscoverProviders + ResolveModel
builder.Services.AddSingleton<ModelSelectionStore>();            // config/model-selection/*.json loader
builder.Services.AddSingleton<ModelCatalogService>();            // live model catalog from /v1/models
builder.Services.AddSingleton<ReasoningCacheService>();          // DeepSeek reasoning_content cache
builder.Services.AddSingleton<RequestTransformer>();             // ApplyExecutionDefaults + force-mode
builder.Services.AddSingleton<OllamaResponseBuilder>();          // OpenAI → Ollama format
builder.Services.AddSingleton<ChatStreamingService>();           // SSE/NDJSON streaming
builder.Services.AddSingleton<ProxyAuthenticationMiddleware>();  // optional Bearer auth

// 4. Background hosted service for health benchmarking
builder.Services.AddHostedService<ProviderBenchmarkService>();

// 5. Map endpoints
app.MapOpenAiEndpoints();   // /v1/models, /v1/chat/completions
app.MapOllamaEndpoints();   // /api/version, /api/tags, /api/show, /api/chat
app.MapHealthEndpoints();   // /health
```

### Core Components

#### 1. `ProviderHttpClientFactory`

**Responsibility:** Create and cache HTTP clients for each provider with proper authentication headers, base URL, and connection pooling.

**Key methods:**

```csharp
public HttpClient GetHttpClient(string providerName)
public IReadOnlyList<ProviderInfo> AllProviders { get; }
```

Each provider's client has:
- **Base URL** set per provider (no `/v1` suffix on stored URL — appended per-call by the endpoint)
- **Authorization** header injected from `PROVIDER_*_API_KEY`
- **SocketsHttpHandler** with 256 connections/server, HTTP/2 enabled
- **Infinite connection lifetime** (OS manages reaper)

#### 2. `ProviderRegistry`

**Responsibility:** Discover providers from env vars and resolve model → ordered candidate list.

**Discovery order** (in `DiscoverProviders()`):
`deepseek, openai, nvidia, openrouter, groq, ollama, moonshot, cerebras`

**Key methods:**

```csharp
public string ResolveModel(string requestedModel);
public IReadOnlyList<string> ResolveCandidates(string resolvedModel);
public IReadOnlyList<ProviderInfo> AllProviders { get; }
public string DefaultModel { get; }
```

**`ResolveModel()` does 3-level `provider/model` hint resolution** (see [dedicated section](#model-resolution--3-level-hint-solver) below). Once the bare upstream id is known, `ResolveCandidates()` returns every provider that offers it, ordered by `(priority asc, provider order asc)`. A qualified id like `kimi-k2.7-code@moonshot` or `kimi-k2.6@moonshot` short-circuits to a single-candidate list (no failover).

**Base URLs (no `/v1` suffix):**

| Provider | Base URL |
|---|---|
| DeepSeek | `https://api.deepseek.com` |
| OpenAI | `https://api.openai.com` |
| NVIDIA NIM | `https://integrate.api.nvidia.com` |
| OpenRouter | `https://openrouter.ai/api/` |
| Groq | `https://api.groq.com/openai` |
| Ollama Cloud | `https://ollama.com` |
| Moonshot/Kimi | `https://api.moonshot.ai` |
| Cerebras | `https://api.cerebras.ai` |

#### 3. `ModelSelectionStore`

**Responsibility:** Load and parse model metadata from `config/model-selection/*.json`. Owns the per-model execution defaults and the `override_client_params` flag.

**Key methods:**

```csharp
public ModelExecutionConfig? FindModelSelectionEntry(string modelId);
public IEnumerable<ModelSelectionEntry> AllEnabled { get; }
public void Reload();  // not exposed — restart only
```

**File structure (8 files, 5 enabled per provider):**

```
config/model-selection/
├── deepseek.json      # 2 enabled (v4-pro, v4-flash; +coder-6.7b disabled)
├── openai.json        # 5 enabled
├── nvidia.json        # 5 enabled
├── groq.json          # 5 enabled
├── openrouter.json    # 6 enabled (qwen3.7-plus, qwen3-coder, nemotron-3-super, nemotron-3-ultra, kimi-k2.7-code, deepseek-v4-pro)
├── moonshot.json      # 5 enabled (kimi-k2.7-code, kimi-k2.6, kimi-k2.5 — all use force-mode)
├── cerebras.json      # 2 enabled
├── ollamacloud.json   # 5 enabled (includes kimi-k2.6 with force-mode inherited from Moonshot rule)
└── ollama.json        # 1 enabled (local Ollama; same provider key as ollamacloud)
```

> `ollamacloud.json` and `ollama.json` both declare `"provider": "ollama"`, so the loader merges them under the `"ollama"` key.

#### 4. `ModelCatalogService`

**Responsibility:** Maintain a live catalog of available models from all providers, fetched at startup.

**Key methods:**

```csharp
public Task RefreshAvailableModelsAsync(CancellationToken ct);
public IReadOnlyList<string> AvailableModels { get; }
public ModelInfo? GetModelInfo(string modelId);
```

**Startup flow:**
1. App starts → `RefreshAvailableModelsAsync()` with a 5s-per-provider timeout
2. For each enabled provider, call its `/models` endpoint
3. Merge results, apply `enabled` filter from `ModelSelectionStore`
4. Resolve cross-provider collisions: when the same upstream id is offered by multiple providers, **lowest `priority` wins; ties go to the earliest-discovered provider** (`deepseek, openai, nvidia, ...`)
5. Cache in memory for the app lifetime; `/v1/models` and `/api/tags` read from this cache

#### 5. `ReasoningCacheService`

**Responsibility:** Cache DeepSeek `reasoning_content` for multi-turn conversations.

**Key methods:**

```csharp
public void CacheReasoning(string conversationId, string thinkingContent);
public string? GetCachedReasoning(string conversationId);
```

**Lifecycle:**
1. DeepSeek response arrives with `message.thinking` populated
2. `ReasoningCacheService` stores the thinking content keyed by a conversation ID derived from message history
3. On the next user message in the same conversation, `RequestTransformer` retrieves and reinjects the cached thinking as a `_reasoning_context` annotation on the assistant message
4. The next DeepSeek call includes the cached reasoning, so it doesn't have to re-derive it

This enables true multi-turn reasoning without paying for it on every turn.

#### 6. `RequestTransformer`

**Responsibility:** Normalize, filter, and inject request parameters per provider; honour `override_client_params` force-mode; reinject cached reasoning.

**Key method:**

```csharp
public string ApplyExecutionDefaults(
    string requestJson,
    string resolvedModel,
    string? providerHint = null);
```

**Parameter filtering matrix** (per-provider support):

| Provider | temperature | top_p | top_k | reasoning_effort | tools | tool_choice |
|---|---|---|---|---|---|---|
| DeepSeek | ✅ | ⚠️ omitted w/ reasoning | ❌ | ✅ | ✅ | ✅ |
| OpenAI | ✅ | ⚠️ omitted w/ reasoning | ❌ | ✅ (o-series) | ✅ | ✅ |
| NVIDIA NIM | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ |
| Groq | ✅ | ⚠️ not recommended w/ temperature | ✅ | ❌ | ❌ | ❌ |
| OpenRouter | ✅ | ✅ | ✅ | ❌ (passthrough) | ✅ | ✅ |
| Ollama Cloud | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ |
| Moonshot/Kimi | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ |
| Cerebras | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ |

**Force-mode semantics** (see [dedicated section](#force-mode-parameter-override)):

```json
"execution": { "temperature": 1.0, "top_p": 0.95, "max_tokens": 4096, "override_client_params": true }
```

When `override_client_params = true`, the four numeric fields (`temperature`, `top_p`, `max_tokens`, `reasoning_effort`) are **overwritten** with the configured values regardless of what the client sent. When `false` or absent, the client wins and the proxy only injects for missing fields.

#### 7. `OllamaResponseBuilder`

**Responsibility:** Convert OpenAI JSON response → Ollama NDJSON format when the client hit `/api/chat`.

**Field mapping:**

| OpenAI | Ollama |
|---|---|
| `choices[0].message.content` | `message.content` |
| `usage.prompt_tokens` | `prompt_eval_count` |
| `usage.completion_tokens` | `eval_count` |
| `choices[0].finish_reason` | `done: true` if `"stop"` |
| `created` | `created_at` |
| `model` | `model` |

**Streaming format conversion:**
- **OpenAI SSE:** `data: {...}\n\n` (one chunk per line)
- **Ollama NDJSON:** one complete JSON object per line, no prefix

The conversion is **on-the-fly** in `ChatStreamingService` — no buffering of full response.

#### 8. `ChatStreamingService`

**Responsibility:** Handle streaming responses with format conversion (if Ollama endpoint) and minimal buffering.

**Streaming pass-through:**
- **OpenAI API:** Server-Sent Events, `data: {...}\n\n`
- **Ollama API:** NDJSON, `{...}\n`
- **Proxy:** Zero-copy pipe through; on-demand format conversion if the target is Ollama

**Key methods:**

```csharp
public async Task StreamChatCompletionAsync(
    HttpContext ctx,
    OpenAiRequest request,
    string resolvedModel,
    string? providerHint,
    CancellationToken ct);
```

#### 9. `ProviderBenchmarkService`

**Responsibility:** Background `IHostedService` that periodically measures provider response times and model availability.

**Key features:**
- Runs on app startup
- Configurable refresh interval
- Logs metrics to console
- Detects provider outages early
- Non-blocking (background hosted service)

#### 10. `ProxyAuthenticationMiddleware`

**Responsibility:** Optional Bearer token auth on all endpoints. Activated only when `PROXY_API_KEY` is set in env or `appsettings.json`.

**Behaviour:**
- If `PROXY_API_KEY` is unset: middleware is a pass-through (no auth)
- If set: every request must include `Authorization: Bearer {PROXY_API_KEY}`; otherwise 401

This is **separate from upstream provider keys** (`PROVIDER_*_API_KEY`). See [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) for the credential separation rule.

---

## Data Flow

### Request Flow: `POST /v1/chat/completions` (Streaming)

```
Client (GitHub Copilot)
    │
    ├─> POST /v1/chat/completions
    │   { "model": "deepseek-v4-pro", "messages": [...], "stream": true }
    │
    ▼
OpenAiEndpoints.cs
    │
    ├─> ProviderRegistry.ResolveModel("deepseek-v4-pro")
    │   Returns: "deepseek-v4-pro" (no prefix to strip)
    │
    ├─> ProviderRegistry.ResolveCandidates("deepseek-v4-pro")
    │   Returns: ["deepseek"] (only DeepSeek offers this id)
    │
    ├─> RequestTransformer.ApplyExecutionDefaults(...)
    │   (Inject temperature, max_tokens, reasoning_effort from deepseek.json)
    │
    ├─> ChatStreamingService.StreamChatCompletionAsync(...)
    │   │
    │   ├─> Forward to https://api.deepseek.com/v1/chat/completions
    │   ├─> Receive SSE stream
    │   ├─> ReasoningCacheService caches thinking content
    │   └─> Stream to client (pass-through)
    │
    ▼
Client (Stream complete)
```

### Request Flow: `POST /api/chat` (Ollama Format, Non-Streaming)

```
Client (Visual Studio BYOM)
    │
    ├─> POST /api/chat
    │   { "model": "kimi-k2.7-code", "messages": [...], "stream": false,
    │     "options": { "temperature": 0.3 } }
    │
    ▼
OllamaEndpoints.cs
    │
    ├─> ProviderRegistry.ResolveModel("kimi-k2.7-code") → "kimi-k2.7-code"
    │
    ├─> ProviderRegistry.ResolveCandidates("kimi-k2.7-code")
    │   Returns: ["moonshot"] (only Moonshot offers it)
    │
    ├─> RequestTransformer.ApplyExecutionDefaults(...)
    │   (Honours override_client_params: true → rewrites temperature: 0.3 → 1.0)
    │
    ├─> Forward to https://api.moonshot.ai/v1/chat/completions
    │   (OpenAI-compatible endpoint)
    │
    ├─> Receive OpenAI response
    │
    ├─> OllamaResponseBuilder.BuildFromOpenAi(...)
    │   (Convert to Ollama NDJSON)
    │
    ▼
Client (Ollama response)
    {
      "model": "kimi-k2.7-code",
      "message": {"role": "assistant", "content": "..."},
      "prompt_eval_count": 42,
      "eval_count": 10,
      "done": true
    }
```

### Request Flow: `POST /v1/chat/completions` with `provider/model` hint

```
Client sends: { "model": "nvidia/qwen3.5-397b-a17b", "messages": [...] }
    │
    ▼
ProviderRegistry.ResolveModel("nvidia/qwen3.5-397b-a17b")
    │
    ├─ Level 1: Verbatim lookup → NOT FOUND
    │             (registry doesn't have the exact "nvidia/qwen3.5-397b-a17b" key)
    │
    ├─ Level 2: Strip prefix "nvidia/" → "qwen3.5-397b-a17b"
    │             Bare lookup → NOT FOUND (NVIDIA prefixes with "qwen/")
    │
    └─ Level 3: Suffix match within hinted provider "nvidia"
                NVIDIA owns "qwen/qwen3.5-397b-a17b" → suffix matches
                Resolved: "qwen/qwen3.5-397b-a17b" (the actual upstream id)
    │
    ▼
ProviderRegistry.ResolveCandidates("qwen/qwen3.5-397b-a17b")
    Returns: ["nvidia"] (only NVIDIA offers this id, so no failover possible)
```

---

## Service Dependencies

### Dependency Injection Graph

```
Program.cs
├─> ProviderHttpClientFactory (Singleton)
│   └─ Creates HttpClient per provider (auth, base URL, connection pool)
│
├─> ProviderRegistry (Singleton)
│   ├─ Requires: ProviderHttpClientFactory
│   └─ Resolves: Model → Provider (3-level hint solver)
│
├─> ModelSelectionStore (Singleton)
│   └─ Loads: config/model-selection/*.json (with override_client_params)
│
├─> ModelCatalogService (Singleton)
│   ├─ Requires: ProviderRegistry, ModelSelectionStore
│   └─ Maintains: Live model catalog from all providers
│
├─> ReasoningCacheService (Singleton)
│   └─ Caches: DeepSeek reasoning_content per conversation
│
├─> RequestTransformer (Singleton)
│   ├─ Requires: ModelCatalogService, ReasoningCacheService
│   └─ Transforms: Requests per provider specs (with force-mode)
│
├─> OllamaResponseBuilder (Singleton)
│   └─ Builds: Ollama responses from OpenAI
│
├─> ChatStreamingService (Singleton)
│   ├─ Requires: RequestTransformer, ProviderRegistry, ProviderHttpClientFactory
│   └─ Handles: SSE/NDJSON streaming + on-the-fly format conversion
│
└─> ProviderBenchmarkService (Hosted Service)
    ├─ Requires: ProviderRegistry, ModelCatalogService
    └─ Monitors: Provider health and metrics
```

### Singleton Rationale

All services are **singletons** (app-scoped):
- **HttpClient reuse:** Enable connection pooling and HTTP/2
- **Model metadata caching:** Avoid reloading JSON on every request
- **Reasoning cache:** Per-conversation state persists across requests
- **Performance:** No allocation overhead from repeated DI lookups

---

## Configuration Management

### Configuration Sources (Priority Order)

1. **Environment Variables** (highest priority)
   - `PROVIDER_DEEPSEEK_API_KEY`, `PROXY_PORT`, `PROVIDER_MOONSHOT_API_KEY`, etc.

2. **.env File**
   - Read by `DotEnv.LoadIfPresent()` in `Program.cs` if present
   - Git-ignored; only `.env.example` is tracked

3. **appsettings.json**
   - Project default configuration

4. **Hardcoded Defaults** (lowest priority)
   - `DefaultModel = "deepseek-v4-pro"`
   - `DefaultPort = 11434`

### JSON Configuration Files

Located in `config/model-selection/`:

```json
{
  "provider": "deepseek",
  "models": [
    {
      "match": "deepseek-v4-pro",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 384000,
        "family": "deepseek",
        "temperature": 0.2,
        "max_tokens": 8192,
        "reasoning_effort": "high",
        "timeout_seconds": 180
      }
    }
  ]
}
```

**Loaded by:** `ModelSelectionStore` on app startup
**Used by:** `RequestTransformer.ApplyExecutionDefaults()` to inject defaults; `ModelCatalogService` to filter the `/v1/models` list
**No hot reload** — restart required after editing JSON

### Curated 5-per-provider cap

Each provider exposes **5 enabled models maximum** (DeepSeek and Cerebras expose 2; Ollama Cloud exposes 5 across `ollamacloud.json` + `ollama.json`). This cap is enforced by `ParameterValidationTests.EnabledModelCount_IsCorrect` (per-provider xUnit `[Theory]`). The curation prioritises coding strength for GitHub Copilot in Visual Studio 2026.

---

## Request Lifecycle

### 1. Request Arrives

```
POST /v1/chat/completions
{
  "model": "deepseek-v4-pro",
  "messages": [{ "role": "user", "content": "hi" }],
  "stream": true
}
```

### 2. Model Resolution

`ProviderRegistry.ResolveModel("deepseek-v4-pro")` runs the 3-level solver:

| Level | Strategy | Hit? |
|---|---|---|
| 1 | Verbatim lookup in registry | ✅ hit → return as-is |

### 3. Candidate Selection

`ProviderRegistry.ResolveCandidates("deepseek-v4-pro")` returns `["deepseek"]` (only DeepSeek offers this id; bare id resolves to a single provider by default).

### 4. Parameter Injection (`RequestTransformer`)

Input request is missing defaults:
```json
{ "model": "deepseek-v4-pro", "messages": [...] }
```

After `ApplyExecutionDefaults()`:
```json
{
  "model": "deepseek-v4-pro",
  "messages": [...],
  "temperature": 0.2,           // ← Injected from deepseek.json
  "max_tokens": 8192,            // ← Injected
  "reasoning_effort": "high"     // ← Injected (and `top_p` is OMITTED because of it)
}
```

### 5. Upstream Call

Forward to `https://api.deepseek.com/v1/chat/completions` via the per-provider `HttpClient` (SocketsHttpHandler, 256-conn pool).

### 6. Response Handling

- If `/v1/chat/completions` (OpenAI): return SSE as-is
- If `/api/chat` (Ollama): convert SSE → NDJSON on-the-fly via `OllamaResponseBuilder`
- If streaming: pass through SSE chunks with minimal buffering (zero-copy)

---

## Model Resolution & 3-Level Hint Solver

`ProviderRegistry.ResolveModel(requestedModel)` is called by the endpoint handlers before any provider selection. It handles the **OpenAI-style `provider/model` request form** used by some clients (e.g. `nvidia/qwen3.5-397b-a17b`).

### Why 3 levels?

NVIDIA exposes many upstream ids with a `family/` prefix that isn't part of the model name the user typed:

- User types: `nvidia/qwen3.5-397b-a17b`
- NVIDIA's actual upstream id: `qwen/qwen3.5-397b-a17b`

A naïve resolver that only tries the verbatim id and the bare name would miss this.

### The 3 levels (tried in order)

1. **Verbatim** — does the full id exist in the registry?
   - `nvidia/openai/gpt-oss-120b` → registered verbatim
2. **Strip prefix** — strip the hinted prefix and look up the bare name
   - `groq/qwen3-32b` → strip `groq/` → look up `qwen3-32b`
3. **Suffix match within hinted provider** — find any upstream id owned by the hinted provider whose **suffix** equals the bare name
   - `nvidia/qwen3.5-397b-a17b` → NVIDIA owns `qwen/qwen3.5-397b-a17b` → suffix match
   - **Constraint:** must not cross providers. A `groq/` hint never resolves to an NVIDIA-owned id.

### What's NOT cross-provider

The 3rd level matches the suffix of an upstream id, but only within the hinted provider's owned set. This is critical: a malicious or mistaken `groq/nemotron-3-super-120b-a12b` will NOT resolve to NVIDIA's Nemotron — it stays on Groq, which would then 404.

### Tests

The full 3-level behaviour is exercised by `tests/ProxyTests/ProviderModelHintTests.cs` (7 tests).

---

## Force-Mode Parameter Override

Some models have hard requirements that contradict what a client might send (the canonical case: Moonshot Kimi K2.7-code, K2.6, and K2.5 reject any request with `temperature ≠ 1.0`).

### Mechanism

The `override_client_params` field on `ModelExecutionConfig` (in `Models/ModelExecutionConfig.cs`) is a boolean. When `true`, `RequestTransformer.ApplyExecutionDefaults()` overwrites client-supplied values for these four fields with the configured value:

- `temperature`
- `top_p`
- `max_tokens`
- `reasoning_effort`

When `false` or absent (the default), the client wins and the proxy only injects for missing fields.

### Real-world case: Moonshot Kimi K2.7-code

`config/model-selection/moonshot.json`:
```json
{
  "match": "kimi-k2.7-code",
  "priority": 1,
  "enabled": true,
  "execution": {
    "temperature": 1.0,
    "top_p": 0.95,
    "max_tokens": 4096,
    "override_client_params": true
  }
}
```

The client sends:
```json
{ "model": "kimi-k2.7-code", "temperature": 0.3, "messages": [...] }
```

The proxy rewrites the body before forwarding to Moonshot:
```json
{ "model": "kimi-k2.7-code", "temperature": 1.0, "max_tokens": 4096, "messages": [...] }
```

### Why is this safe?

Force-mode only fires for the specific `match` patterns in the JSON config. A model without `override_client_params: true` is completely unaffected. The flag is opt-in, per-model, and visible in version-controlled JSON.

### Tests

`tests/ProxyTests/OverrideClientParamsTests.cs` (10 tests) covers:
- Force-mode **overwrites** client values for `temperature`, `top_p`, `max_tokens`
- Default mode (flag absent or `false`) **preserves** client values
- Force-mode is honoured across all 8 providers (not just Moonshot)

---

## Failing Over

### Failover Strategy

The proxy implements **graceful fallback** for non-streaming requests:

1. **Primary candidate** — lowest-priority claimant provider
2. **Secondary** — next candidate in the ordered list (only if more than one provider offers the id)
3. **Tertiary** — ...
4. **Final** — return 502 Bad Gateway if all candidates fail

For a bare id like `kimi-k2.6` offered by both Moonshot and OpenRouter, the failover list is `["moonshot", "openrouter"]`. For a qualified id like `kimi-k2.7-code@moonshot`, the list is `["moonshot"]` only (no failover).

### Error Conditions That Trigger Failover

- ❌ Network timeout
- ❌ HTTP 401 Unauthorized (bad API key)
- ❌ HTTP 429 Too Many Requests (rate limit)
- ❌ HTTP 500 Internal Server Error
- ❌ HTTP 503 Service Unavailable

### Error Conditions That DO NOT Failover

- ❌ 400 Bad Request (invalid parameter) — user error, no point retrying
- ❌ 404 Not Found (model doesn't exist) — doesn't exist elsewhere

### Streaming Failover Limitation

**Streaming requests do NOT failover** because headers are already sent to the client. If the stream fails mid-execution:

```
data: {"choices":[...]}
data: {"choices":[...]}
⚠️ Connection drops
❌ No retry possible (headers already sent)
```

**Workaround:** Client should retry the entire request.

---

## Performance Optimizations

### 1. Connection Pooling

```csharp
// HttpClientFactory uses SocketsHttpHandler
var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 256,  // Pool size
    AutomaticDecompression = DecompressionMethods.All,
    // HTTP/2 is enabled by default
};
```

**Benefits:**
- Connection reuse across requests
- HTTP/2 multiplexing (multiple streams per connection)
- Reduced handshake overhead

### 2. Zero-Copy Streaming

SSE responses are streamed **without buffering**:

```csharp
// ❌ Inefficient: Buffer entire response
byte[] buffer = await response.Content.ReadAsByteArrayAsync();
await ctx.Response.Body.WriteAsync(buffer);

// ✅ Efficient: Stream directly
await response.Content.CopyToAsync(ctx.Response.Body);
```

**Result:** Memory usage is O(chunk_size) not O(total_response_size)

### 3. Slim Builder

```csharp
// Minimal middleware overhead
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);  // No logging, auth, etc.

// Manually add only what we need
app.UseOptionalProxyAuthentication(proxyApiKey);
```

**Benefit:** Reduced CPU per request

### 4. JSON Serialization

Uses only `System.Text.Json` with source generators:

```csharp
[JsonSourceGenerationOptions(...)]
[JsonSerializable(typeof(OpenAiRequest))]
[...]
internal partial class JsonSerializerContext : JsonSerializerContext { }
```

**Benefit:** Zero reflection, compile-time code generation

### 5. Model Metadata In-Memory

```csharp
// Loaded once on startup
await modelCatalog.RefreshAvailableModelsAsync(ct);

// Cached in memory
public IReadOnlyList<string> AvailableModels { get; private set; } = [];
```

**Benefit:** `/v1/models` endpoint responds in <1ms

---

## Related Documentation

- [API.md](API.md) — Endpoint specifications
- [CONFIGURATION.md](CONFIGURATION.md) — Configuration reference
- [TESTING.md](TESTING.md) — Test architecture and running tests
- [AGENTS.md](AGENTS.md) — Quick reference for AI assistants
