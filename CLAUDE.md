# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

The project is **AI Proxy Hub** (GitHub: `rodrigo714-gmail/ai-proxy-hub`); the assembly, csproj and
solution are all named `ai-proxy-hub`. Older names (`vs2026-copilot-deepseek-v4`,
`deepseek-copilot-proxy`) are retired — do not reintroduce them.

## Branching rule (hard constraint)

- **Never** touch `main`. It is the protected release branch.
- All work happens on `develop`. Feature branches branch off `develop` and merge back into `develop`.
- Conventional Commits are required for every merge into `develop`.

## Build & Test

```bash
# Build
dotnet build

# Run all tests (370 tests, xUnit + WebApplicationFactory, fully offline)
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

# Smoke-test every provider end-to-end, exactly as VS 2026 BYOM calls it
./scripts/test-all-providers.ps1            # non-streaming
./scripts/test-all-providers.ps1 -Stream    # streaming (what VS 2026 actually uses)
```

**Port 11434 is the real Ollama daemon's port.** If Ollama is installed and running, the proxy
cannot bind and will fail to start. Stop Ollama or set `PROXY_PORT` to something else.

Tests live in `tests/ProxyTests/`. The project targets **.NET 10.0** and uses `WebApplication.CreateSlimBuilder()`.

## What This Is

A high-performance ASP.NET Core **minimal API proxy** that bridges GitHub Copilot, Cursor, Continue.dev, Visual Studio BYOM, and Ollama clients to multiple AI providers through two API surfaces:

| API Surface | URL Prefix | Used By |
|---|---|---|
| OpenAI-compatible | `/v1/*` | Copilot, Cursor, Continue.dev, OpenAI SDKs |
| Ollama-compatible | `/api/*` | VS 2026 BYOM, native Ollama clients |

**Supported providers (11):** DeepSeek, OpenAI, Google, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, Cerebras, Z.AI, ZenMux.

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

Model metadata lives in `config/model-selection/{provider}.json` (11 files: `deepseek`, `openai`, `google`, `nvidia`, `groq`, `openrouter`, `moonshot`, `cerebras`, `zai`, `ollama`, `zenmux`). The filename is cosmetic — the `"provider"` field inside is what binds the file to a provider, and **exactly one file may declare a given provider**. Each file maps model names to execution defaults:

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
- **Adding a new provider:** add one entry to `ProviderCapabilitiesRegistry` + create its JSON. Nothing else — discovery, routing and filtering all read from that registry.
- Models with `"enabled": false` are excluded from `/v1/models` and `/api/tags`
- **Matching is by substring, longest match first.** `"gpt-5.4"` is a substring of `"gpt-5.4-mini"`, so specificity — not priority — decides which entry a model gets. Priority only breaks ties between equally specific entries.
- `execution.override_client_params` (bool, default `false`) — force-mode: overwrite the client's `temperature` / `top_p` / `max_tokens` / `reasoning_effort` with the configured value (Moonshot Kimi K2.x mandates `temperature=1.0`)
- `execution.uses_max_completion_tokens` (bool, default `false`) — send the token budget as `max_completion_tokens` and never `max_tokens`. Required by OpenAI GPT-5.x and the o-series, which answer HTTP 400 otherwise.
- `execution.supports_temperature` (bool, default `true`) — when `false`, strip `temperature` and `top_p` entirely. Same OpenAI models.

### Curated model cap

Each provider exposes **up to ~10 enabled models**, curated for coding strength in GitHub Copilot
inside Visual Studio 2026. Rosters were verified against each provider's live `/v1/models` (or
`/api/tags`) on **2026-07-30** — every enabled entry answered a real request.

Retired or unentitled models are kept in the JSON with `"enabled": false` plus a `_comment`
explaining why, so nobody re-adds them.

**Verify before editing a roster.** `./scripts/test-all-providers.ps1` is the check: a model that
`/v1/models` lists can still return 404 (not entitled), 410 (end of life) or 402 (needs a paid
plan). NVIDIA's `moonshotai/kimi-k2.6` and Ollama Cloud's `kimi-k3` are both examples.

### Model naming across the two API surfaces

`/api/tags` publishes each model twice over:

- `name` — `"GROQ - gpt-oss-120b:latest"`, the human-readable label Visual Studio shows.
- `model` — `"openai/gpt-oss-120b@groq:latest"`, the id the client sends back.

`ProviderRegistry.ResolveModel` must therefore cope with `<upstream>@<provider>:latest`, with the
`"PROVIDER - model"` display form, and with the OpenAI-style `provider/model` form.

Ollama upstream ids legitimately contain a colon (`gpt-oss:120b`, `mistral-large-3:675b`), so the
`:latest` tag is stripped from the **end**, never from the first colon.

An `@provider` (or `PROVIDER - `) hint that the named provider cannot satisfy falls back to the
default model rather than resolving across providers — answering an explicit "OLLAMA - x" pick
from NVIDIA is worse than an honest fallback.

For the OpenAI-style `provider/model` form, `ResolveModel` tries three strategies in order:

1. **Verbatim** — the full id exists in the registry (e.g. `openai/gpt-oss-120b` is a registered key).
2. **Strip prefix** — strip the provider prefix and look up the bare name (e.g. `groq/qwen3.6-27b` → `qwen3.6-27b`).
3. **Suffix match within hinted provider** — find any upstream id owned by the hinted provider whose suffix equals the bare name. Must NOT cross providers — a `groq/` hint never resolves to an NVIDIA-owned id.

The corresponding test files are `tests/ProxyTests/ProviderModelHintTests.cs` and
`tests/ProxyTests/RoutingDiagnosticTests.cs`.

### Streaming format conversion (critical for VS 2026)

The two surfaces speak different stream formats, and each converts on the fly:

| Endpoint | Emits | Converts from |
|---|---|---|
| `/api/chat` | Ollama NDJSON, one object per line, terminated by `"done": true` | upstream OpenAI SSE (`ChatStreamingService.StreamOllamaAndCache`) |
| `/v1/chat/completions` | OpenAI SSE, terminated by `data: [DONE]` | upstream Ollama NDJSON (`OpenAiEndpoints.HandleOllamaCloudChatCompletion`) |

`/api/chat` must **never** emit `data:` frames. An Ollama client silently discards them, so the
model appears to answer nothing at all — which is exactly how this broke for 10 of the 11
providers before. `EndpointTests.ApiChat_Streaming_LastLineHasDoneTrue` guards it.

Reasoning models (DeepSeek, Nemotron, GLM) can spend their whole budget in `reasoning_content`
and return empty `content`. Both directions fall back to the reasoning text so the client never
sees a blank reply.

### Parameter Filtering Rules (RequestTransformer)

`RequestTransformer.ApplyExecutionDefaults()` strips unsupported parameters per provider before forwarding, and injects defaults for missing fields:

- `top_k` → removed for DeepSeek, OpenAI, Moonshot/Kimi; kept for NVIDIA, Groq, OpenRouter
- `reasoning_effort` → only DeepSeek and OpenAI o-series; removed for NVIDIA, Groq, Moonshot/Kimi
- `top_p` → omitted when `reasoning_effort` is set (DeepSeek API rule: "don't combine sampling parameters with reasoning")
- `tools`/`tool_choice` → kept for DeepSeek, OpenAI, NVIDIA, OpenRouter, Moonshot, Cerebras; **removed for Groq** (Groq's chat API has tool quirks)
- `function_call` → removed for all (deprecated)
- `override_client_params=true` → force-overwrite the client value with the configured one for `temperature`, `top_p`, `max_tokens`, `reasoning_effort`
- `uses_max_completion_tokens=true` → rewrite `max_tokens` to `max_completion_tokens` (OpenAI GPT-5.x / o-series)
- `supports_temperature=false` → drop `temperature` and `top_p` entirely (same models)

### Provider-specific quirks

**Moonshot Kimi K2.x** rejects any request with `temperature ≠ 1.0`. Handled with
`"override_client_params": true` in `moonshot.json`; `RequestTransformer` overwrites the client's
`temperature` (and `top_p`, `max_tokens`, `reasoning_effort` when configured) before forwarding.
`OverrideClientParamsTests.cs` exercises it end-to-end.

**OpenAI GPT-5.x / o-series** answers `400 Unsupported parameter: 'max_tokens'` and rejects an
explicit temperature. Handled with `uses_max_completion_tokens` + `supports_temperature: false`
in `openai.json`. `gpt-5.5-pro` is Responses-API only (`404 This is not a chat model`) and stays
disabled for Ollama/BYOM clients.

**Z.AI** puts its OpenAI-compatible API under `https://api.z.ai/api/paas/v4` with a *relative*
chat path (`chat/completions`, no `v1/` prefix). `ProviderHttpClientFactory` appends a trailing
slash to every base URL, without which `HttpClient` would resolve the relative path against the
last path segment and silently drop `/v4`.

### Upstream error handling

`UpstreamErrorMiddleware` maps transport failures to responses a client can act on:
`HttpRequestException` → **502 UPSTREAM_UNREACHABLE**, timeout → **504 UPSTREAM_TIMEOUT**, both
with a JSON body naming the provider and model. Without it, an unreachable host or an expired
`timeout_seconds` surfaced as an empty HTTP 500 and Visual Studio just said the model failed.

An upstream 200 whose body isn't a parseable OpenAI completion returns **502** with the upstream
body attached, rather than throwing.

### Configuration Sources (priority order)

1. System environment variables
2. `.env` file (loaded by `Program.cs` if present)
3. `appsettings.json`
4. Hardcoded defaults (port 11434, model `deepseek-v4-pro`)

### Testing Architecture

Tests use `WebApplicationFactory<Program>` with an **in-process stub provider** (no real API calls). The stub simulates OpenAI-compatible endpoints on a random port. Key patterns:

- `ProxyFixture` provides `HttpClient` wired to the in-process proxy
- Tests that construct a `ProviderRegistry` or otherwise touch process env vars MUST be in `[Collection("Proxy")]` — `ProxyFixture` boots `Program.cs`, which loads the developer's real `.env` into the process, so anything running in parallel with it races
- Those tests must also use `ProviderEnvScope`, which clears every `PROVIDER_*` variable derived from `ProviderCapabilitiesRegistry` and restores them on dispose. Never hand-write the list: a forgotten provider picks up a real API key from `.env` and quietly changes collision resolution
- **370 tests** across 15 test files covering endpoints, parameter validation, model selection, transformers, auth, reasoning cache, Ollama response building, JSON defaults, HTTP client factory, provider registry, `override_client_params` semantics, `provider/model` hint resolution, and Ollama NDJSON streaming

## Credential Separation

Per `.github/copilot-instructions.md`: **Never confuse Ollama Cloud API keys with local proxy API keys.** Cloud provider keys are managed via `.env` variables (`PROVIDER_OLLAMACLOUD_API_KEY`, `PROVIDER_DEEPSEEK_API_KEY`, `PROVIDER_MOONSHOT_API_KEY`, `PROVIDER_CEREBRAS_API_KEY`, etc.). The optional `PROXY_API_KEY` controls access to the proxy itself and is unrelated.

`.env` is in `.gitignore` and is **never** committed. Only `.env.example` is tracked.

## Key Files Reference

| File | Purpose |
|---|---|
| `Program.cs` | Entry point, DI registration, endpoint mapping, env-var discovery |
| `Services/ProviderRegistry.cs` | Model → provider resolution; tag/`@provider`/`provider/model` hint resolvers; `ResolveCandidates` for failover lists |
| `Services/RequestTransformer.cs` | Parameter filtering + default injection; `override_client_params` force-mode; `max_completion_tokens` rewrite |
| `Services/ModelCatalogService.cs` | Live model catalog from all providers; cross-provider collision resolution |
| `Services/ModelSelectionStore.cs` | JSON config loader; longest-match-first entry lookup; merges files declaring the same provider |
| `Services/ChatStreamingService.cs` | Streaming; OpenAI SSE → Ollama NDJSON conversion incl. tool-call reassembly |
| `Services/ProviderCapabilitiesRegistry.cs` | Single source of truth for provider base URLs, paths, env prefixes and parameter support |
| `Services/ProviderHttpClientFactory.cs` | HttpClient creation with auth headers and base-URL normalisation |
| `Models/ModelExecutionConfig.cs` | Per-model execution flags parsed from `execution` |
| `Models/ProviderInfo.cs` | record struct `(Name, ApiKey, BaseUrl, Client, Capabilities)` |
| `Infrastructure/ProxyAuthenticationMiddleware.cs` | Optional bearer token auth |
| `Infrastructure/UpstreamErrorMiddleware.cs` | Transport failures → 502/504 JSON instead of empty 500 |
| `config/model-selection/` | Per-provider model JSON configs (11 files) |
| `scripts/test-all-providers.ps1` | Live end-to-end smoke test of every published model |

Further detail is available in `docs/ARCHITECTURE.md`, `docs/AGENTS.md`, `docs/API.md`, `docs/CONFIGURATION.md`, `docs/TESTING.md`, and `docs/DEPLOYMENT.md`.
