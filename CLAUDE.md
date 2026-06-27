# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Branching rule (hard constraint)

- **Never** touch `main`. It is the protected release branch.
- All work happens on `develop`. Feature branches branch off `develop` and merge back into `develop`.
- Conventional Commits are required for every merge into `develop`.

## Build & Test

```bash
# Build
dotnet build

# Run all tests (329 tests, xUnit + WebApplicationFactory)
dotnet test

# Run specific test suite
dotnet test --filter "FullyQualifiedName~ParameterValidationTests"
dotnet test --filter "FullyQualifiedName~EndpointTests"
dotnet test --filter "FullyQualifiedName~ModelSelectionStoreTests"
dotnet test --filter "FullyQualifiedName~OverrideClientParamsTests"
dotnet test --filter "FullyQualifiedName~ProviderModelHintTests"

# Run single test by method name
dotnet test --filter TestMethodName=MySpecificTest

# Verbose output
dotnet test --verbosity detailed

# Run the proxy locally (port 11434 default)
dotnet run
```

Tests live in `tests/ProxyTests/`. The project targets **.NET 10.0** and uses `WebApplication.CreateSlimBuilder()`.

## What This Is

A high-performance ASP.NET Core **minimal API proxy** that bridges GitHub Copilot, Cursor, Continue.dev, Visual Studio BYOM, and Ollama clients to multiple AI providers through two API surfaces:

| API Surface | URL Prefix | Used By |
|---|---|---|
| OpenAI-compatible | `/v1/*` | Copilot, Cursor, Continue.dev, OpenAI SDKs |
| Ollama-compatible | `/api/*` | VS 2026 BYOM, native Ollama clients |

**Supported providers (9):** DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, Cerebras, ZenMux.

**Primary use case:** GitHub Copilot inside Visual Studio 2026 producing code completions and code chat. All curated model configs are optimised for this workload.

## Architecture

### Service Registration (all Singletons)

Every service is registered as a **singleton** in `Program.cs`. The entire DI graph:

```
ProviderHttpClientFactory  →  Creates/caches per-provider HttpClient with auth headers
ProviderRegistry           →  Resolves model name → ordered list of provider candidates;
                              ResolveModel() does 3-level "provider/model" hint resolution
ModelSelectionStore        →  Loads/parses config/model-selection/*.json (incl. override_client_params)
ModelCatalogService        →  Fetches live model catalogs from all providers on startup;
                              resolves cross-provider collisions by (priority asc, provider order asc)
ReasoningCacheService      →  Caches DeepSeek reasoning_content for multi-turn conversations
RequestTransformer         →  Injects defaults + filters unsupported params per provider;
                              honours override_client_params=true force-mode
OllamaResponseBuilder      →  Converts OpenAI JSON response → Ollama NDJSON format
ChatStreamingService       →  Handles SSE streaming + on-the-fly format conversion
ProviderBenchmarkService   →  Background HostedService monitoring provider health
```

### Endpoint Structure

- `Endpoints/OpenAiEndpoints.cs` — Maps `/v1/models`, `/v1/chat/completions`
- `Endpoints/OllamaEndpoints.cs` — Maps `/api/version`, `/api/tags`, `/api/show`, `/api/chat`
- `Endpoints/HealthEndpoints.cs` — Maps `/health`
- `Middleware/` — Empty (auth middleware lives in `Infrastructure/ProxyAuthenticationMiddleware.cs`)

### Request Lifecycle

1. **Request arrives** → endpoint handler parses model name
2. **Model validated** → `ModelCatalogService.AvailableModels` (populated at startup)
3. **Defaults injected** → `RequestTransformer.ApplyExecutionDefaults()` reads config from `ModelSelectionStore` and injects temperature, max_tokens, reasoning_effort, etc. for the requested model
4. **Provider resolved** → `ProviderRegistry.ResolveCandidates(model)` returns ordered list of providers to try
5. **Forward to upstream** → via `ChatStreamingService` (streaming) or direct HTTP (non-streaming)
6. **Response converted** → if Ollama endpoint, `OllamaResponseBuilder` maps OpenAI → Ollama format
7. **Failover** → non-streaming requests retry next candidate on failure; streaming does NOT failover (headers already sent)

### Model Configuration

Model metadata lives in `config/model-selection/{provider}.json` (9 files: `deepseek`, `openai`, `nvidia`, `groq`, `openrouter`, `moonshot`, `cerebras`, `ollamacloud`, `zenmux`). Each file maps model names to execution defaults:

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
        "temperature": 0.2,
        "max_tokens": 8192,
        "reasoning_effort": "high",
        "timeout_seconds": 180
      }
    }
  ]
}
```

- **Adding a new model:** edit the JSON for its provider + restart (no hot reload)
- **Adding a new provider:** create JSON + add provider to `ProviderRegistry.DiscoverProviders` + add HttpClient factory in `ProviderHttpClientFactory.cs`
- Models with `"enabled": false` are excluded from `/v1/models` and `/api/tags`
- The `execution.override_client_params` flag (bool, default `false`) controls force-mode: when `true`, the proxy overwrites client-supplied `temperature` / `top_p` / `max_tokens` / `reasoning_effort` with the configured value (used by Moonshot Kimi K2.x which mandates `temperature=1.0`)

### Curated model cap

Each provider exposes **up to 9 enabled models** (DeepSeek and Cerebras expose 2, Ollama Cloud exposes 9). Curated picks prioritise coding strength for GitHub Copilot in Visual Studio 2026:

| Provider | Top picks |
|----------|-----------|
| DeepSeek | deepseek-v4-pro, deepseek-v4-flash |
| OpenAI | gpt-5, gpt-5-mini, gpt-4.1, gpt-4o, gpt-oss-120b |
| NVIDIA NIM | qwen3-coder-480b, moonshotai/kimi-k2.6, nemotron-3-super-120b, openai/gpt-oss-120b, qwen3.5-397b |
| Groq | llama-3.3-70b-versatile, qwen3-32b, llama-4-scout-17b, gpt-oss-120b, gpt-oss-20b |
| OpenRouter | qwen3-coder, nemotron-3-super, nemotron-3-ultra, kimi-k2.6, deepseek-v4-pro, qwen3.7-plus |
| Moonshot | kimi-k2.6, kimi-k2.5, moonshot-v1-{128k,auto,32k} |
| Cerebras | zai-glm-4.7, gpt-oss-120b |
| Ollama Cloud | kimi2.7-code, glm-5.2, minimax-m3, qwen3-coder:480b, qwen3-coder-next, devstral-2:123b, kimi-k2.6, deepseek-v4-pro, mistral-medium-3.5 |
| ZenMux | glm-5.2-free 🆓, glm-5.2, kimi-k2.7-code-free 🆓, kimi-k2.7-code, qwen3.7-plus, qwen3.7-max, gemini-3.5-flash, gpt-5.5-pro, gpt-5.5, qwen3.6-plus, deepseek-v4-pro, deepseek-v4-flash, grok-4.3 |

### 3-level `provider/model` hint resolution

`ProviderRegistry.ResolveModel(requestedModel)` tries three strategies in order to handle the OpenAI-style `provider/model` request form:

1. **Verbatim** — the full id exists in the registry (e.g. `openai/gpt-oss-120b` is a registered key).
2. **Strip prefix** — strip the provider prefix and look up the bare name (e.g. `groq/qwen3-32b` → `qwen3-32b`).
3. **Suffix match within hinted provider** — find any upstream id owned by the hinted provider whose suffix equals the bare name (e.g. `nvidia/qwen3.5-397b-a17b` matches NVIDIA's `qwen/qwen3.5-397b-a17b` upstream id). Must NOT cross providers — a `groq/` hint never resolves to an NVIDIA-owned id.

The corresponding test file is `tests/ProxyTests/ProviderModelHintTests.cs`.

### Parameter Filtering Rules (RequestTransformer)

`RequestTransformer.ApplyExecutionDefaults()` strips unsupported parameters per provider before forwarding, and injects defaults for missing fields:

- `top_k` → removed for DeepSeek, OpenAI, Moonshot/Kimi; kept for NVIDIA, Groq, OpenRouter
- `reasoning_effort` → only DeepSeek and OpenAI o-series; removed for NVIDIA, Groq, Moonshot/Kimi
- `top_p` → omitted when `reasoning_effort` is set (DeepSeek API rule: "don't combine sampling parameters with reasoning")
- `tools`/`tool_choice` → kept for DeepSeek, OpenAI, NVIDIA, OpenRouter, Moonshot, Cerebras; **removed for Groq** (Groq's chat API has tool quirks)
- `function_call` → removed for all (deprecated)
- `override_client_params=true` → force-overwrite the client value with the configured one for `temperature`, `top_p`, `max_tokens`, `reasoning_effort`

### Moonshot Kimi K2.x quirk

The Kimi K2.5 and K2.6 models reject any request with `temperature ≠ 1.0`. The proxy handles this by setting `"override_client_params": true` in `moonshot.json` for those two entries. `RequestTransformer` then overwrites the client's `temperature` value (and `top_p`, `max_tokens`, `reasoning_effort` if they have configured values) before forwarding.

The `OverrideClientParamsTests.cs` test file exercises this end-to-end: `ApplyExecutionDefaults_OverrideClientParamsTrue_OverwritesClientTemperature` sends `{"temperature": 0.7}` and verifies the upstream body has `temperature: 1.0`.

### Configuration Sources (priority order)

1. System environment variables
2. `.env` file (loaded by `Program.cs` if present)
3. `appsettings.json`
4. Hardcoded defaults (port 11434, model `deepseek-v4-pro`)

### Testing Architecture

Tests use `WebApplicationFactory<Program>` with an **in-process stub provider** (no real API calls). The stub simulates OpenAI-compatible endpoints on a random port. Key patterns:

- `ProxyFixture` provides `HttpClient` wired to the in-process proxy
- Tests that mutate process env vars MUST share the `[Collection("Proxy")]` fixture (no parallel races)
- **336 tests** across 14 test files covering endpoints, parameter validation, model selection, transformers, auth, reasoning cache, Ollama response building, JSON defaults, HTTP client factory, provider registry, **override_client_params semantics**, and **3-level `provider/model` hint resolution**

## Credential Separation

Per `.github/copilot-instructions.md`: **Never confuse Ollama Cloud API keys with local proxy API keys.** Cloud provider keys are managed via `.env` variables (`PROVIDER_OLLAMACLOUD_API_KEY`, `PROVIDER_DEEPSEEK_API_KEY`, `PROVIDER_MOONSHOT_API_KEY`, `PROVIDER_CEREBRAS_API_KEY`, etc.). The optional `PROXY_API_KEY` controls access to the proxy itself and is unrelated.

`.env` is in `.gitignore` and is **never** committed. Only `.env.example` is tracked.

## Key Files Reference

| File | Purpose |
|---|---|
| `Program.cs` | Entry point, DI registration, endpoint mapping, env-var discovery |
| `Services/ProviderRegistry.cs` | Model → provider resolution; 3-level `provider/model` hint resolver; `ResolveCandidates` for failover lists |
| `Services/RequestTransformer.cs` | Parameter filtering + default injection; `override_client_params` force-mode |
| `Services/ModelCatalogService.cs` | Live model catalog from all providers; cross-provider collision resolution |
| `Services/ModelSelectionStore.cs` | JSON config loader for model defaults; parses `override_client_params` |
| `Services/ChatStreamingService.cs` | SSE/NDJSON streaming handler |
| `Services/ProviderHttpClientFactory.cs` | HttpClient creation with auth headers |
| `Models/ModelExecutionConfig.cs` | record struct with `OverrideClientParams` field |
| `Models/ProviderInfo.cs` | record struct `(Name, ApiKey, BaseUrl, Client)` |
| `Infrastructure/ProxyAuthenticationMiddleware.cs` | Optional bearer token auth |
| `config/model-selection/` | Per-provider model JSON configs (9 files) |

Further detail is available in `docs/ARCHITECTURE.md`, `docs/AGENTS.md`, `docs/API.md`, `docs/CONFIGURATION.md`, `docs/TESTING.md`, and `docs/DEPLOYMENT.md`.
