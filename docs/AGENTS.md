# AGENTS.md - AI Assistant Quick Reference

Optimized documentation for GitHub Copilot, Claude, and other AI code assistants working with this codebase.

## Project Essence

**Multi-Provider AI Proxy** — Single HTTP gateway to DeepSeek, OpenAI, NVIDIA, Groq, OpenRouter, Ollama Cloud, **Moonshot/Kimi**, **Cerebras**, and **ZenMux**.

- **Dual API Support:** OpenAI-compatible (`/v1/*`) + Ollama-compatible (`/api/*`)
- **Smart Routing:** Model names auto-map to providers with intelligent fallback (3-level `provider/model` hint resolution). `/api/tags` now emits qualified aliases (`model@provider:latest`) for correct provider routing.
- **Parameter Filtering:** Adapt requests for each provider's unique capabilities
- **Override Mode:** `override_client_params: true` force-overrides client values for models with hard requirements (e.g. Moonshot Kimi K2.x mandates `temperature=1.0`)
- **Diagnostic Response Headers:** Every response includes `X-Proxy-Requested-Model`, `X-Proxy-Resolved-Model`, `X-Proxy-Provider` for debugging routing
- **🖼️ Vision & Image Passthrough:** Multi-part image content is converted between OpenAI and Ollama formats automatically
- **Zero-Copy Streaming:** SSE pass-through with minimal allocations
- **Reasoning Cache:** DeepSeek multi-turn thinking content reuse
- **Production Ready:** HTTP/2, connection pooling, **336-test** suite

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
| `X-Proxy-Candidate-Count` | `/v1/*` | Number of failover candidates |
| `X-Proxy-Primary-Provider` | `/v1/*` | Primary candidate provider |
| `X-Proxy-Primary-Upstream` | `/v1/*` | Primary upstream model |

---

## Curated Model Roster (2026-06-16)

Each provider exposes enabled models optimised for **GitHub Copilot inside Visual Studio 2026**: coding-first picks with deep context windows, strong tool support, and 1M-token reasoning where available.

| Provider | Top picks | Notes |
|----------|-----------|-------|
| **DeepSeek** | `deepseek-v4-pro`, `deepseek-v4-flash` | 2 enabled |
| **OpenAI** | `gpt-5`, `gpt-5-mini`, `gpt-4.1`, `gpt-4o`, `gpt-oss-120b` | 5 enabled |
| **NVIDIA NIM** | `qwen/qwen3-coder-480b-a35b-instruct`, `moonshotai/kimi-k2.6`, `nvidia/nemotron-3-super-120b-a12b`, `openai/gpt-oss-120b`, `qwen/qwen3.5-397b-a17b` | 5 enabled |
| **Groq** | `llama-3.3-70b-versatile`, `qwen/qwen3-32b`, `meta-llama/llama-4-scout-17b-16e-instruct`, `openai/gpt-oss-120b`, `openai/gpt-oss-20b` | 5 enabled |
| **OpenRouter** | `qwen/qwen3.7-plus`, `qwen/qwen3-coder`, `nvidia/nemotron-3-super-120b-a12b`, `nvidia/nemotron-3-ultra-550b-a55b`, `moonshotai/kimi-k2.7-code`, `deepseek/deepseek-v4-pro` | 6 enabled |
| **Moonshot/Kimi** | `kimi-k2.7-code`, `kimi-k2.6`, `kimi-k2.5`, `moonshot-v1-128k`, `moonshot-v1-auto` | 5 enabled; K2.x have `override_client_params=true` (forces `temperature=1.0`) |
| **Cerebras** | `zai-glm-4.7`, `gpt-oss-120b` | 2 enabled |
| **Ollama Cloud** | `kimi2.7-code`, `glm-5.2`, `minimax-m3`, `qwen3-coder:480b`, `qwen3-coder-next`, `devstral-2:123b`, `kimi-k2.6`, `deepseek-v4-pro`, `mistral-medium-3.5` | 9 enabled |
| **ZenMux** 🆕 | **`glm-5.2-free` (free 🆓)**, **`kimi-k2.7-code-free` (free, vision, reasoning 🆓)** | 2 enabled (free tier only) |

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
   `"override_client_params": true` in the `execution` block — see Moonshot Kimi K2.x and ZenMux kimi-k2.7-code-free entries.

2. **Update provider routing:** If new provider, add entry to `ProviderCapabilitiesRegistry.cs` + create `config/model-selection/{provider}.json` (no other code changes needed — routing reads from registry).
3. **Restart proxy** (configuration is not reloaded on-the-fly)
4. **Test:** `dotnet test --filter "FullyQualifiedName~ModelSelectionStoreTests"`

### Debugging a Routing Issue

Check the diagnostic headers on any response:
```bash
curl -s -D - -X POST http://localhost:11434/api/chat \
  -H "Content-Type: application/json" \
  -d '{"model":"glm-5.2-free","messages":[{"role":"user","content":"hi"}],"stream":false}' \
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
├── deepseek.json       # v4-pro, v4-flash
├── openai.json         # gpt-5, gpt-5-mini, gpt-4.1, gpt-4o, gpt-oss-120b
├── nvidia.json         # qwen3-coder-480b, kimi-k2.6, nemotron-3-super, gpt-oss-120b, qwen3.5-397b
├── groq.json           # llama-3.3-70b, qwen3-32b, llama-4-scout, gpt-oss-120b, gpt-oss-20b
├── openrouter.json     # qwen3-coder, nemotron, kimi-k2.6, deepseek-v4-pro
├── moonshot.json       # kimi-k2.6, kimi-k2.5, moonshot-v1-*
├── cerebras.json       # zai-glm-4.7, gpt-oss-120b
├── ollamacloud.json    # kimi2.7-code, glm-5.2, minimax-m3, qwen3-coder:480b, qwen3-coder-next, devstral-2:123b, kimi-k2.6, deepseek-v4-pro, mistral-medium-3.5
└── zenmux.json         # glm-5.2-free 🆓, kimi-k2.7-code-free 🆓 (free tier only)
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
  ↓ [ProviderRegistry] ResolveCandidates() → ordered failover list
  ↓ [Forward] Send to upstream API
  ↓ [OllamaResponseBuilder] If Ollama endpoint, convert OpenAI → Ollama
  ↓ [Diagnostic headers] X-Proxy-* added to response
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
- **Test count:** 336 tests, all green

---

## Related Docs

- **[API.md](docs/API.md)** — Endpoint specifications and examples
- **[CONFIGURATION.md](docs/CONFIGURATION.md)** — Setup, providers, parameter mapping
- **[ARCHITECTURE.md](docs/ARCHITECTURE.md)** — System design, components, data flow
- **[TESTING.md](docs/TESTING.md)** — Test architecture, running tests, adding new tests
- **[DEPLOYMENT.md](docs/DEPLOYMENT.md)** — Docker, bare metal, monitoring, troubleshooting