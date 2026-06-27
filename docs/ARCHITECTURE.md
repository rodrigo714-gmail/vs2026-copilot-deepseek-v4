# System Architecture

Comprehensive architecture documentation describing the proxy design, components, and data flow for **9 AI providers**: DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, Cerebras, and ZenMux.

## Table of Contents

- [Overview](#overview)
- [Component Architecture](#component-architecture)
- [Data Flow](#data-flow)
- [Service Dependencies](#service-dependencies)
- [Configuration Management](#configuration-management)
- [Request Lifecycle](#request-lifecycle)
- [Model Resolution & 3-Level Hint Solver](#model-resolution--3-level-hint-solver)
- [Qualified Model Aliases (model@provider)](#qualified-model-aliases-modelprovider)
- [Diagnostic Response Headers](#diagnostic-response-headers)
- [Image Passthrough Support](#image-passthrough-support)
- [Force-Mode Parameter Override](#force-mode-parameter-override)
- [Failing Over](#failing-over)
- [Performance Optimizations](#performance-optimizations)

---

## Overview

The proxy is a high-performance ASP.NET Core minimal API application that bridges GitHub Copilot, Cursor, Continue.dev, Visual Studio BYOM, and Ollama clients to **nine** AI providers:

- DeepSeek
- OpenAI
- NVIDIA NIM
- Groq
- OpenRouter
- Ollama Cloud
- Moonshot / Kimi
- Cerebras
- ZenMux

### Design Principles

1. **Multi-Provider Agnostic** — One API surface, N backends
2. **Zero Allocation Streaming** — Pass-through SSE without buffering
3. **Configuration-Driven** — Model defaults, routing, and force-mode flags via JSON
4. **Testability** — All services are unit-testable with in-memory fixtures
5. **Production-Ready** — Connection pooling, HTTP/2, timeout handling
6. **Curated, Not Exhaustive** — Up to 15 enabled models per provider; chosen for coding in VS 2026 via GitHub Copilot

### Technology Stack

- **Runtime:** .NET 10
- **Web Framework:** ASP.NET Core Minimal APIs (`WebApplication.CreateSlimBuilder`)
- **Serialization:** System.Text.Json
- **HTTP Client:** `SocketsHttpHandler` with 256 connections/server + HTTP/2 multiplexing
- **Testing:** xUnit 2.9.3 + `Microsoft.AspNetCore.Mvc.Testing` — **336 tests** in 15 test files

---

## Component Architecture

### Application Startup (`Program.cs`)

```csharp
// 1. Create slim builder
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

// 2. Register core services — all are singletons
builder.Services.AddSingleton<ProviderHttpClientFactory>();
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ModelSelectionStore>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<ReasoningCacheService>();
builder.Services.AddSingleton<RequestTransformer>();
builder.Services.AddSingleton<OllamaResponseBuilder>();
builder.Services.AddSingleton<ChatStreamingService>();

// 3. Background hosted service
builder.Services.AddHostedService<ProviderBenchmarkService>();

// 4. Map endpoints
app.MapOpenAiEndpoints();   // /v1/models, /v1/chat/completions
app.MapOllamaEndpoints();   // /api/version, /api/tags, /api/show, /api/chat
app.MapHealthEndpoints();   // /health
```

### Core Components

#### 1. `ProviderHttpClientFactory`

Creates and caches HTTP clients for each provider with auth headers, base URL, and connection pooling (256 connections/server, HTTP/2).

#### 2. `ProviderRegistry`

**Discovery order:** `deepseek, openai, nvidia, openrouter, groq, ollama, moonshot, cerebras, zenmux`

**Base URLs:**

| Provider | Base URL |
|----------|----------|
| DeepSeek | `https://api.deepseek.com` |
| OpenAI | `https://api.openai.com` |
| NVIDIA NIM | `https://integrate.api.nvidia.com` |
| OpenRouter | `https://openrouter.ai/api/` |
| Groq | `https://api.groq.com/openai` |
| Ollama Cloud | `https://ollama.com` |
| Moonshot/Kimi | `https://api.moonshot.ai` |
| Cerebras | `https://api.cerebras.ai` |
| ZenMux | `https://zenmux.ai/api` |

#### 3. `ModelSelectionStore`

Loads and parses model metadata from `config/model-selection/*.json` (10 files: deepseek, openai, nvidia, groq, openrouter, moonshot, cerebras, ollamacloud, ollama, zenmux).

#### 4. `ModelCatalogService`

Maintains a live catalog of available models from all providers, fetched at startup. Resolves cross-provider collisions by `(priority asc, provider order asc)`.

#### 5. `ReasoningCacheService`

Caches DeepSeek `reasoning_content` for multi-turn conversations.

#### 6. `RequestTransformer`

Normalizes, filters, and injects request parameters per provider. Honours `override_client_params` force-mode.

**Parameter filtering matrix:**

| Provider | temperature | top_p | top_k | reasoning_effort | tools |
|----------|:-----------:|:-----:|:-----:|:-----------------:|:-----:|
| DeepSeek | ✅ | ⚠️ omitted | ❌ | ✅ | ✅ |
| OpenAI | ✅ | ⚠️ omitted | ❌ | ✅ | ✅ |
| NVIDIA NIM | ✅ | ✅ | ✅ | ❌ | ✅ |
| Groq | ✅ | ✅ | ✅ | ❌ | ❌ |
| OpenRouter | ✅ | ✅ | ✅ | ❌ | ✅ |
| Ollama Cloud | ✅ | ✅ | ✅ | ❌ | ✅ |
| Moonshot/Kimi | ✅ | ✅ | ❌ | ❌ | ✅ |
| Cerebras | ✅ | ✅ | ✅ | ❌ | ✅ |
| ZenMux | ✅ | ✅ | ❌ | ❌ | ✅ |

#### 7. `OllamaResponseBuilder`

Converts OpenAI JSON response → Ollama NDJSON format. Also handles image format conversion (OpenAI multi-part → Ollama `images` array).

#### 8. `ChatStreamingService`

Handles streaming responses with format conversion (SSE ↔ NDJSON) and zero-copy passthrough.

---

## Data Flow

### Request Flow: `POST /v1/chat/completions` (Streaming)

```
Client (GitHub Copilot)
    ├─> POST /v1/chat/completions
    │   { "model": "deepseek-v4-pro", "messages": [...], "stream": true }
    ▼
OpenAiEndpoints.cs
    ├─> ProviderRegistry.ResolveModel("deepseek-v4-pro")
    ├─> ProviderRegistry.ResolveCandidates("deepseek-v4-pro")
    ├─> RequestTransformer.ApplyExecutionDefaults(...)
    ├─> ChatStreamingService.StreamChatCompletionAsync(...)
    │   ├─> Forward to upstream API
    │   ├─> Receive SSE stream
    │   ├─> ReasoningCacheService caches thinking content
    │   └─> Stream to client (pass-through, + diagnostic headers)
    ▼
Client (Stream complete)
```

### Request Flow: `POST /api/chat` with provider-qualified alias

```
Client (Visual Studio BYOM)
    ├─> POST /api/chat
    │   { "model": "z-ai/glm-5.2-free@zenmux:latest", "messages": [...], "stream": false }
    ▼
OllamaEndpoints.cs
    ├─> ProviderRegistry.ResolveModel("z-ai/glm-5.2-free@zenmux:latest")
    │   → StripTagSuffix: "z-ai/glm-5.2-free@zenmux"
    │   → Contains '@' → exact match: "z-ai/glm-5.2-free@zenmux" → provider: "zenmux"
    ├─> RequestTransformer.ApplyExecutionDefaults(...)
    ├─> Convert Ollama → OpenAI format (including image conversion)
    ├─> Forward to https://zenmux.ai/api/v1/chat/completions
    ├─> Receive OpenAI response
    ├─> OllamaResponseBuilder: Convert to Ollama NDJSON
    └─> Response + X-Proxy-* diagnostic headers
```

---

## Qualified Model Aliases (model@provider)

The `/api/tags` endpoint emits `model` fields in `model@provider:latest` format. This ensures that when a client sends this model name back, the proxy routes it to the **exact provider** rather than falling back to the default (DeepSeek).

**Resolution chain:**
1. Client sends: `{"model": "deepseek-v4-pro@ollama:latest"}`
2. `StripTagSuffix(":latest")` → `deepseek-v4-pro@ollama`
3. `_modelToProvider.ContainsKey("deepseek-v4-pro@ollama")` → ✅
4. Returns `deepseek-v4-pro@ollama`, which maps to the **ollama** provider

**For bare model names** (no `@provider`), the proxy returns the lowest-priority claimant provider. To pin a provider, use the qualified `model@provider` form.

---

## Diagnostic Response Headers

Both endpoints include response headers for debugging:

| Header | Endpoints | Description |
|--------|-----------|-------------|
| `X-Proxy-Requested-Model` | Both | Model name as sent by client |
| `X-Proxy-Resolved-Model` | Both | Internal resolved model id |
| `X-Proxy-Upstream-Model` | Both | Model sent to upstream API |
| `X-Proxy-Provider` | Both | Provider that handled the request |
| `X-Proxy-Candidate-Count` | `/v1/*` | Number of failover candidates |
| `X-Proxy-Primary-Provider` | `/v1/*` | Primary candidate provider |
| `X-Proxy-Primary-Upstream` | `/v1/*` | Primary upstream model |

---

## Image Passthrough Support

The proxy supports vision models that accept image inputs. Image conversion happens automatically:

**OpenAI multi-part format** (input from SDKs):
```json
{"role": "user", "content": [{"type": "text", "text": "..."}, {"type": "image_url", "image_url": {"url": "data:image/..."}}]}
```

**Ollama images format** (input from Ollama clients):
```json
{"role": "user", "content": "...", "images": ["data:image/..."]}
```

**Conversion logic in `BuildOllamaChatRequest` (OpenAiEndpoints.cs):**
- Detects `content` as array (multi-part format)
- Extracts `text` parts → concatenates into `content` string
- Extracts `image_url` parts → writes as `images` array (Ollama format)
- Converts bare base64 to `data:image/png;base64,` prefix when needed

---

## Force-Mode Parameter Override

The `override_client_params` field on `ModelExecutionConfig` is a boolean. When `true`, `RequestTransformer.ApplyExecutionDefaults()` overwrites client-supplied values for `temperature`, `top_p`, `max_tokens`, and `reasoning_effort` with the configured value.

Currently enabled for:
- Moonshot `kimi-k2.7-code`, `kimi-k2.6`, `kimi-k2.5` (mandates `temperature=1.0`)
- Ollama Cloud `kimi2.7-code`, `kimi-k2.6`
- ZenMux `kimi-k2.7-code-free`, `kimi-k2.6`

---

## Failing Over

Non-streaming requests try the next candidate in priority order if the primary fails. Streaming requests do **not** failover (headers already sent).

---

## Performance Optimizations

- **Connection pooling:** 256 per provider, HTTP/2 enabled
- **Zero-copy streaming:** SSE/NDJSON passthrough without buffering
- **Slim builder:** Minimal middleware overhead
- **JSON:** System.Text.Json source-generated (no reflection)
- **Model metadata:** Loaded once on startup, cached in RAM

---

## Related Documentation

- [API.md](API.md) — Endpoint specifications
- [CONFIGURATION.md](CONFIGURATION.md) — Configuration reference
- [TESTING.md](TESTING.md) — Test architecture and running tests
- [AGENTS.md](AGENTS.md) — Quick reference for AI assistants