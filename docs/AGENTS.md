# AGENTS.md - AI Assistant Quick Reference

Optimized documentation for GitHub Copilot, Claude, and other AI code assistants working with this codebase.

## Project Essence

**Multi-Provider AI Proxy** — Single HTTP gateway to DeepSeek, OpenAI, Google, NVIDIA, Groq, OpenRouter, Ollama Cloud, **Moonshot/Kimi**, **Cerebras**, **Z.AI**, **ZenMux**, and the free-tier trio **Mistral**, **SiliconFlow**, **Cloudflare Workers AI** — 14 in total.

- **Dual API Support:** OpenAI-compatible (`/v1/*`) + Ollama-compatible (`/api/*`)
- **Smart Routing:** Model names auto-map to providers with intelligent fallback (3-level `provider/model` hint resolution). `/api/tags` emits both a pinned alias (`model@provider:latest`, one exact provider) and, for models served by two or more providers, an unpinned one (`model@auto:latest`) that walks the whole candidate list.
- **Parameter Filtering:** Adapt requests for each provider's unique capabilities
- **Override Mode:** `override_client_params: true` force-overrides client values for models with hard requirements (e.g. Moonshot Kimi K2.x mandates `temperature=1.0`)
- **Diagnostic Response Headers:** Every response includes `X-Proxy-Requested-Model`, `X-Proxy-Resolved-Model`, `X-Proxy-Provider` for debugging routing
- **🖼️ Vision & Image Passthrough:** Multi-part image content is converted between OpenAI and Ollama formats automatically
- **Zero-Copy Streaming:** SSE pass-through with minimal allocations
- **Reasoning Cache:** DeepSeek multi-turn thinking content reuse
- **Quota-Aware Failover:** Every chat path walks an ordered candidate list, streaming included. `UpstreamFailureClassifier` splits a transient 429 from an exhausted daily/monthly budget; `ProviderHealthService` stands the provider down for a matching length and demotes it in the routing order (never removes it).
- **Free-Tier Budgets:** `config/free-tier/catalog.json` + `/api/free-tier/summary` report allowance, spend and remaining budget. Usage persists in `data/usage-rollup.json` so a monthly quota survives a restart.
- **Production Ready:** HTTP/2, connection pooling, **585-test** suite, zero NuGet dependencies

**Primary use case:** GitHub Copilot inside Visual Studio 2026 producing code completions and code chat. All curated models are selected for coding strength.

---

## Quick Navigation

### For Implementation Tasks
- **Adding a new endpoint?** → See `Endpoints/` directory and `ARCHITECTURE.md` → Request Lifecycle
- **Fixing parameter issues?** → Edit `config/model-selection/*.json` or `Services/RequestTransformer.cs`
- **Adding provider support?** → Add entry in `ProviderCapabilitiesRegistry.cs` + new `config/model-selection/{provider}.json` (no other code changes needed)
- **Debugging streaming?** → `Services/ChatStreamingService.cs` + `Endpoints/OpenAiEndpoints.cs`
- **Checking routing?** → Check `X-Proxy-*` response headers on any chat completion

### For Understanding
- **How does request routing work?** → `ARCHITECTURE.md` → Provider Resolution + Retry Loop
- **What parameters does each model support?** → `CONFIGURATION.md` → Parameter Mapping table
- **How do tests work?** → `TESTING.md` → Test Architecture section

### For Deployment
- **Docker setup?** → `Dockerfile` + `docker-compose.yml`
- **Environment variables?** → `CONFIGURATION.md` → Environment Setup
- **Health checks?** → `GET /health` (maps to `Endpoints/HealthEndpoints.cs`)

---

## Core Services (One-Liner Summaries)

| Service | Purpose | File |
|---------|---------|------|
| `ProviderHttpClientFactory` | Creates HTTP clients per provider with auth | `Services/ProviderHttpClientFactory.cs` |
| `ProviderRegistry` | Resolves model → provider + lists available providers; `ResolveModel` does 3-level `provider/model` hint resolution | `Services/ProviderRegistry.cs` |
| `ModelSelectionStore` | Loads JSON configs from `config/model-selection/`; parses `override_client_params` | `Services/ModelSelectionStore.cs` |
| `ModelCatalogService` | Fetches live model list from all providers on startup; resolves cross-provider collisions by `(priority asc, provider order asc)` | `Services/ModelCatalogService.cs` |
| `ReasoningCacheService` | Stores/retrieves DeepSeek thinking for multi-turn | `Services/ReasoningCacheService.cs` |
| `RequestTransformer` | Injects defaults + filters params per provider; honours `override_client_params` force-mode | `Services/RequestTransformer.cs` |
| `OllamaResponseBuilder` | Converts OpenAI response → Ollama format | `Services/OllamaResponseBuilder.cs` |
| `ChatStreamingService` | Handles SSE streaming + format conversion | `Services/ChatStreamingService.cs` |
| `ProviderBenchmarkService` | Background service monitoring provider health | `Services/ProviderBenchmarkService.cs` |

---

## Endpoints at a Glance

### OpenAI Format (`/v1/*`)
```
GET  /v1/models                    → List models (OpenAI format; returns bare + 'upstream@provider' aliases)
POST /v1/chat/completions          → Chat completion (streaming or non-streaming)
                                     Response includes X-Proxy-* diagnostic headers
GET  /health                       → Health check + provider summary
```

### Ollama Format (`/api/*`)
```
GET  /api/version                  → Proxy version
GET  /api/tags                     → List models (Ollama format; model field uses model@provider:latest)
GET  /api/show?model=X             → Model info (GET variant)
POST /api/show                     → Model info (POST variant)
POST /api/chat                     → Chat completion (Ollama format; NDJSON streaming)
                                     Response includes X-Proxy-* diagnostic headers
```

### Diagnostic Headers

| Header | Endpoint | Description |
|--------|----------|-------------|
| `X-Proxy-Requested-Model` | Both | What the client sent |
| `X-Proxy-Resolved-Model` | Both | Internal resolved model id |
| `X-Proxy-Upstream-Model` | Both | Model sent to upstream API |
| `X-Proxy-Provider` | Both | Provider that handled the request |
| `X-Proxy-Candidate-Count` | Both | How many providers could have served this model |
| `X-Proxy-Candidate-Index` | Both | Position of the provider that answered; non-zero means it failed over |
| `X-Proxy-Attempts` | Both | How many candidates were actually tried |
| `X-Proxy-Primary-Provider` | `/v1/*` | Primary candidate provider |
| `X-Proxy-Primary-Upstream` | `/v1/*` | Primary upstream model |

---

## Curated Model Roster (2026-06-16)

Each provider exposes enabled models optimised for **GitHub Copilot inside Visual Studio 2026**: coding-first picks with deep context windows, strong tool support, and 1M-token reasoning where available.

| Provider | Top picks | Notes |
|----------|-----------|-------|
| **DeepSeek** | `deepseek-v4-pro`, `deepseek-v4-flash` | 2 enabled |
| **OpenAI** | `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, `o4-mini` | 4 enabled; GPT-5.x/o-series use `max_completion_tokens` and reject explicit temperature |
| **Google** | `gemini-3.5-flash`, `gemini-3.1-pro-preview`, `gemini-3-pro-preview`, `gemini-3-flash-preview`, `gemini-3.1-flash-lite`, `gemini-2.5-pro`, `gemini-2.5-flash`, `gemini-2.5-flash-lite` | 8 enabled; free-tier daily quota (429s recover at midnight PT) |
| **NVIDIA NIM** | `nvidia/nemotron-3-super-120b-a12b`, `z-ai/glm-5.2`, `deepseek-ai/deepseek-v4-pro`, `openai/gpt-oss-120b`, `nvidia/nemotron-3-ultra-550b-a55b`, `minimaxai/minimax-m3`, `nvidia/llama-3.3-nemotron-super-49b-v1.5`, `meta/llama-3.3-70b-instruct` | 8 enabled; free tier queues some models past their timeout |
| **Groq** | `qwen/qwen3.6-27b`, `openai/gpt-oss-120b`, `openai/gpt-oss-20b`, `llama-3.3-70b-versatile`, `llama-3.1-8b-instant`, `groq/compound`, `groq/compound-mini` | 7 enabled; compound models run server-side tools only (`supports_tools=false`) |
| **OpenRouter** | `anthropic/claude-sonnet-4.6`, `openai/gpt-5.4`, `google/gemini-3.5-flash`, `deepseek/deepseek-v4-pro`, `qwen/qwen3.7-plus`, `qwen/qwen3-coder`, `moonshotai/kimi-k2.7-code`, `moonshotai/kimi-k2.6`, `x-ai/grok-4.3`, `nvidia/nemotron-3-super-120b-a12b` | 10 enabled |
| **Moonshot/Kimi** | `kimi-k2.7-code`, `kimi-k2.7-code-highspeed`, `kimi-k2.6`, `moonshot-v1-128k`, `moonshot-v1-auto`, `moonshot-v1-32k` | 6 enabled; K2.x have `override_client_params=true` (forces `temperature=1.0`) |
| **Cerebras** | `zai-glm-4.7`, `gpt-oss-120b` | 2 enabled |
| **Z.AI** | `glm-5.2`, `glm-5.1`, `glm-4.7`, `glm-4.7-flash`, `glm-4.7-flashx` | 5 enabled; only `glm-4.7-flash` is free — the rest need account balance |
| **Ollama Cloud** | `kimi-k2.7-code`, `glm-5.2`, `deepseek-v4-pro`, `minimax-m3`, `nemotron-3-ultra`, `nemotron-3-super`, `glm-5.1`, `deepseek-v4-flash`, `gpt-oss:120b` | 9 enabled |
| **ZenMux** | *(whole roster disabled 2026-07-31: every model answered HTTP 402 `reject_no_credit` — top up at zenmux.ai and re-enable in `zenmux.json`)* | 0 enabled |

---

## Key Workflows

### Adding a New Model

1. **Update config file:** `config/model-selection/{provider}.json`
   ```json
   {
     "match": "new-model-name",
     "priority": 99,
     "enabled": true,
     "execution": {
       "context_length": 128000,
       "max_output_tokens": 8000,
       "temperature": 0.7,
       "max_tokens": 4096,
       "timeout_seconds": 120
     }
   }
   ```
   For models with hard requirements (e.g. `temperature=1.0` is non-negotiable), set
   `"override_client_params": true` in the `execution` block — see the Moonshot and Ollama Cloud Kimi K2.x entries.

2. **Update provider routing:** If new provider, add entry to `ProviderCapabilitiesRegistry.cs` + create `config/model-selection/{provider}.json` (no other code changes needed — routing reads from registry).
3. **Restart proxy** (configuration is not reloaded on-the-fly)
4. **Test:** `dotnet test --filter "FullyQualifiedName~ModelSelectionStoreTests"`

### Debugging a Routing Issue

Check the diagnostic headers on any response:
```bash
curl -s -D - -X POST http://localhost:11434/api/chat \
  -H "Content-Type: application/json" \
  -d '{"model":"glm-5.2","messages":[{"role":"user","content":"hi"}],"stream":false}' \
  | head -20
```
Look for `X-Proxy-Requested-Model`, `X-Proxy-Resolved-Model`, `X-Proxy-Provider`.

### Debugging a Streaming Response Issue

1. **Check endpoint:** `Endpoints/OpenAiEndpoints.cs` or `Endpoints/OllamaEndpoints.cs`
2. **Trace streaming:** `Services/ChatStreamingService.cs`
3. **Format conversion:** If Ollama endpoint, see `OllamaResponseBuilder` for SSE→NDJSON transform
4. **Test with curl:**
   ```bash
   curl -X POST http://localhost:11434/v1/chat/completions \
     -H "Content-Type: application/json" \
     -d '{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}],"stream":true}'
   ```

### Understanding a Test Failure

1. **Find test file:** Search `tests/ProxyTests/` by test name
2. **Check fixture:** Real tests use `ProxyFixture` (stub provider at localhost)
3. **Identify phase:**
   - **Parameter validation?** → `ParameterValidationTests.cs`
   - **`override_client_params` semantics?** → `OverrideClientParamsTests.cs`
   - **`provider/model` hint resolution?** → `ProviderModelHintTests.cs`
   - **Model selection?** → `ModelSelectionStoreTests.cs` / `ModelCatalogServiceTests.cs`
   - **HTTP behaviour?** → `EndpointTests.cs`
   - **Transform logic?** → `RequestTransformerTests.cs`
4. **Run single test:** `dotnet test --filter MyTestName=*`

---

## Common Parameter Gotchas

| Situation | Solution |
|-----------|----------|
| `reasoning_effort` breaks on non-DeepSeek | `RequestTransformer` filters it; check `ParameterValidationTests` |
| `top_p` + `reasoning_effort` causes API error | DeepSeek docs: omit `top_p` when `reasoning_effort` is set |
| `top_k` not supported by OpenAI | Filtered in `RequestTransformer` |
| User sends `temperature=0.7` to `kimi-k2.6` | Moonshot/ZenMux K2.x mandates `temperature=1.0` — proxy overwrites via `override_client_params=true` |
| Model not in `/v1/models` list | Check `ModelCatalogService.AvailableModels` or `config/model-selection/` enabled flag |
| `provider/model` hint not routing | `ProviderRegistry.ResolveModel` tries 3 levels |
| Model routes to wrong provider | Check `X-Proxy-Provider` header; if unexpected, the bare model name resolves to the lowest-priority claimant |

---

## Config File Locations

```
config/model-selection/
├── deepseek.json       # deepseek-v4-pro, deepseek-v4-flash
├── openai.json         # gpt-5.5, gpt-5.4, gpt-5.4-mini, o4-mini
├── google.json         # gemini-3.5/3.1/3 flash & pro, gemini-2.5-*
├── nvidia.json         # nemotron-3-super/ultra, glm-5.2, deepseek-v4-pro, gpt-oss-120b, minimax-m3, llama-3.3-*
├── groq.json           # qwen3.6-27b, gpt-oss-120b/20b, llama-3.3-70b, llama-3.1-8b, compound(-mini)
├── openrouter.json     # claude-sonnet-4.6, gpt-5.4, gemini-3.5-flash, kimi-k2.7/k2.6, grok-4.3, qwen3.7-plus, qwen3-coder
├── moonshot.json       # kimi-k2.7-code(-highspeed), kimi-k2.6, moonshot-v1-*
├── cerebras.json       # zai-glm-4.7, gpt-oss-120b
├── zai.json            # glm-5.2, glm-5.1, glm-4.7(-flash/-flashx)
├── ollama.json         # Ollama Cloud: kimi-k2.7-code, glm-5.2/5.1, deepseek-v4-pro/flash, minimax-m3, nemotron-3-*, gpt-oss:120b
├── zenmux.json         # whole roster disabled 2026-07-31 (HTTP 402 reject_no_credit)
├── mistral.json        # ships disabled (needs PROVIDER_MISTRAL_API_KEY + live verification)
├── siliconflow.json    # ships disabled (needs PROVIDER_SILICONFLOW_API_KEY)
└── cloudflare.json     # ships disabled (needs API key + PROVIDER_CLOUDFLARE_BASE_URL)
```

---

## Testing Cheat Sheet

```bash
# Run all tests
dotnet test

# Run endpoint tests only
dotnet test --filter ClassName=EndpointTests

# Run parameter validation for specific provider
dotnet test --filter ClassName=ParameterValidationTests

# Run model selection tests
dotnet test --filter ClassName=ModelSelectionStoreTests

# Run override_client_params force-mode tests
dotnet test --filter ClassName=OverrideClientParamsTests

# Run single test by name
dotnet test --filter TestMethodName=MySpecificTest

# Verbose output
dotnet test --verbosity detailed
```

---

## Environment Variables

```bash
# Required (set in .env or system)
PROVIDER_DEEPSEEK_API_KEY=sk-xxxxx
PROVIDER_OPENAI_API_KEY=sk-proj-xxxxx
PROVIDER_NVIDIA_API_KEY=nvapi-xxxxx

# Optional
PROVIDER_ZENMUX_API_KEY=your-zenmux-key-here
PROXY_PORT=11434
LOG_LEVEL=Information
REQUEST_TIMEOUT=300
MAX_CONCURRENT_REQUESTS=1000
DEFAULT_MODEL=deepseek-v4-pro
```

> `.env` is in `.gitignore` and is **never** committed. See `.env.example` for the canonical template.

---

## Architecture Concepts

### Request Transformation Pipeline
```
Client Request
  ↓ [Parse]
JsonElement (incoming)
  ↓ [ModelSelectionStore] Load defaults for requested model
JsonElement + defaults
  ↓ [RequestTransformer] Apply execution defaults + provider-specific filtering
  ↓ [ProviderRegistry] ResolveRoutePlan() → candidates, cooling providers demoted
  ↓ [Failover loop] For each candidate, until one succeeds:
  ↓     [Forward] Send to upstream API (nothing written to the client yet)
  ↓     [UpstreamFailureClassifier] On failure: rate limit? quota gone? bad request?
  ↓     [ProviderHealthService] Record it; a 400 stops the walk, everything else continues
  ↓ [OllamaResponseBuilder] If Ollama endpoint, convert OpenAI → Ollama
  ↓ [Diagnostic headers] X-Proxy-* added, naming the provider that SERVED
```

### Failover rules in one screen

```
429 + "daily limit" in body  → QuotaExhausted → cool until local midnight  → try next
429 bare                     → RateLimit      → 5s * 2^n, capped at 5 min   → try next
413 + "rate_limit_exceeded"  → RateLimit      (Groq reports over-TPM as 413) → try next
402                          → QuotaExhausted → 6h, credits refill on top-up → try next
401 / 403                    → Auth           → 15 min                       → try next
404 / 410                    → ModelUnavailable → 30 min, THAT MODEL only     → try next
408 / 5xx                    → Transient      → nothing until 3 in a row     → try next
no response / conn refused   → Unreachable    → 2 min, escalating to 30 min   → try next
400 / 413 / 422 (genuine)    → BadRequest     → no cooldown                   → STOP

An upstream Retry-After always wins. A success halves the failure count and clears at zero.
Order() demotes cooling providers but NEVER returns an empty list.
A "model@provider" pin is a single candidate: no failover, no reordering.
A "model@auto" alias is the whole candidate list: this is the only id in /api/tags
that reaches failover at all, since every other one is provider-pinned.

429 bodies naming a short window ("per minute", TPM/RPM) are RateLimit even when they
also say "quota" — Cerebras returns {"param":"quota","code":"token_quota_exceeded"}
for a per-minute throttle, which used to cost a stand-down until midnight.
```

### Provider/Model Hint Resolution (3-level)
```
User sends model = "nvidia/qwen3.5-397b-a17b"
  ↓ Level 1: Verbatim lookup → NOT FOUND
  ↓ Level 2: Strip prefix → "qwen3.5-397b-a17b" → NOT FOUND
  ↓ Level 3: Suffix match within hinted provider "nvidia"
                → matches "qwen/qwen3.5-397b-a17b" (NVIDIA's upstream id)
  ↓ Return "qwen/qwen3.5-397b-a17b"
```

---

## Performance Notes

- **Connection pooling:** 256 per provider, HTTP/2 enabled
- **Streaming:** Zero-copy pass-through (not buffered)
- **Model metadata:** Loaded once on startup, cached in RAM
- **JSON parsing:** `System.Text.Json` source-generated (no reflection)
- **Typical latency:** <10ms proxy overhead
- **Test count:** 585 tests, all green (1 skipped)

---

## Related Docs

- **[API.md](API.md)** — Endpoint specifications and examples
- **[CONFIGURATION.md](CONFIGURATION.md)** — Setup, providers, parameter mapping
- **[ARCHITECTURE.md](ARCHITECTURE.md)** — System design, components, data flow
- **[TESTING.md](TESTING.md)** — Test architecture, running tests, adding new tests
- **[DEPLOYMENT.md](DEPLOYMENT.md)** — Docker, bare metal, monitoring, troubleshooting