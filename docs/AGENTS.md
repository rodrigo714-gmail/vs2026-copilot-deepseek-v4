# AGENTS.md - AI Assistant Quick Reference

Optimized documentation for GitHub Copilot, Claude, and other AI code assistants working with this codebase.

## Project Essence

**Multi-Provider AI Proxy** — Single HTTP gateway to DeepSeek, OpenAI, NVIDIA, Groq, OpenRouter, Ollama Cloud, **Moonshot/Kimi**, and **Cerebras**.

- **Dual API Support:** OpenAI-compatible (`/v1/*`) + Ollama-compatible (`/api/*`)
- **Smart Routing:** Model names auto-map to providers with intelligent fallback (3-level `provider/model` hint resolution)
- **Parameter Filtering:** Adapt requests for each provider's unique capabilities
- **Override Mode:** `override_client_params: true` force-overrides client values for models with hard requirements (e.g. Moonshot Kimi K2.x mandates `temperature=1.0`)
- **Zero-Copy Streaming:** SSE pass-through with minimal allocations
- **Reasoning Cache:** DeepSeek multi-turn thinking content reuse
- **Production Ready:** HTTP/2, connection pooling, **329-test** suite

**Primary use case:** GitHub Copilot inside Visual Studio 2026 producing code completions and code chat. All curated models are selected for coding strength.

---

## Quick Navigation

### For Implementation Tasks
- **Adding a new endpoint?** → See `Endpoints/` directory and `ARCHITECTURE.md` → Request Lifecycle
- **Fixing parameter issues?** → Edit `config/model-selection/*.json` or `Services/RequestTransformer.cs`
- **Adding provider support?** → `Services/ProviderRegistry.cs` + new `config/model-selection/{provider}.json`
- **Debugging streaming?** → `Services/ChatStreamingService.cs` + `Endpoints/OpenAiEndpoints.cs`
- **Adding `override_client_params` semantics?** → `Services/RequestTransformer.cs` + `OverrideClientParamsTests.cs`

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
GET  /health                       → Health check + provider summary
```

### Ollama Format (`/api/*`)
```
GET  /api/version                  → Proxy version
GET  /api/tags                     → List models (Ollama format)
GET  /api/show?model=X             → Model info (GET variant)
POST /api/show                     → Model info (POST variant)
POST /api/chat                     → Chat completion (Ollama format; NDJSON streaming)
```

---

## Curated Model Roster (2026-06-10)

Each provider exposes **5 enabled models maximum** (a few smaller providers expose 2). The curation is optimised for **GitHub Copilot inside Visual Studio 2026**: coding-first picks with deep context windows, strong tool support, and 1M-token reasoning where available.

| Provider | Top picks (5 max) | Notes |
|----------|-------------------|-------|
| **DeepSeek** | `deepseek-v4-pro`, `deepseek-v4-flash`, `deepseek-coder-6.7b-instruct` | 2 enabled, 1 disabled (coder kept for code-specific tasks) |
| **OpenAI** | `gpt-5`, `gpt-5-mini`, `gpt-4.1`, `gpt-4o`, `gpt-oss-120b` | 5 enabled |
| **NVIDIA NIM** | `qwen/qwen3-coder-480b-a35b-instruct`, `moonshotai/kimi-k2.6`, `nvidia/nemotron-3-super-120b-a12b`, `openai/gpt-oss-120b`, `qwen/qwen3.5-397b-a17b` | 5 enabled, all top coding picks; 1M context on the 480B Qwen coder and Nemotron super |
| **Groq** | `llama-3.3-70b-versatile`, `qwen/qwen3-32b`, `meta-llama/llama-4-scout-17b-16e-instruct`, `openai/gpt-oss-120b`, `openai/gpt-oss-20b` | 5 enabled; Groq's strength is inference speed for chat |
| **OpenRouter** | `qwen/qwen3.7-plus`, `qwen/qwen3-coder`, `nvidia/nemotron-3-super-120b-a12b`, `nvidia/nemotron-3-ultra-550b-a55b`, `moonshotai/kimi-k2.7-code`, `deepseek/deepseek-v4-pro` | 6 enabled |
| **Moonshot/Kimi** | `kimi-k2.7-code`, `kimi-k2.6`, `kimi-k2.5`, `moonshot-v1-128k`, `moonshot-v1-auto` | 5 enabled; **kimi-k2.7-code, kimi-k2.6 and kimi-k2.5 have `override_client_params=true` (forces `temperature=1.0`)** |
| **Cerebras** | `zai-glm-4.7`, `gpt-oss-120b` | 2 enabled (Cerebras has a small curated set) |
| **Ollama Cloud** | `qwen3-coder:480b`, `qwen3-coder-next`, `devstral-2:123b`, `kimi-k2.6`, `deepseek-v4-pro` | 5 enabled; `kimi-k2.6` inherits Moonshot's force-mode |

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
   `"override_client_params": true` in the `execution` block — see Moonshot Kimi K2.x entries.

2. **Update provider routing:** If new provider, edit `ProviderRegistry.DiscoverProviders()` and add the env-var key in `Program.cs`.
3. **Restart proxy** (configuration is not reloaded on-the-fly)
4. **Test:** `dotnet test --filter "FullyQualifiedName~ModelSelectionStoreTests"`

### Fixing Parameter Filtering for a Provider

1. **Identify issue:** Test fails with unsupported parameter
2. **Locate transform logic:** `Services/RequestTransformer.cs` → `ApplyExecutionDefaults()`
3. **Find provider switch:** Look for `p is "provider_name"` checks in the `supportsReasoningEffort` / `supportsTopK` ternaries
4. **Add/remove parameter:** Modify the JSON body rewrite
5. **Add unit test:** `ParameterValidationTests.cs` with a new `[InlineData]` theory case
6. **Run tests:** `dotnet test --filter "FullyQualifiedName~ParameterValidationTests"`

### Debugging a Streaming Response Issue

1. **Check endpoint:** `Endpoints/OpenAiEndpoints.cs` or `Endpoints/OllamaEndpoints.cs`
2. **Trace streaming:** `Services/ChatStreamingService.cs` → `StreamChatCompletion()`
3. **Format conversion:** If Ollama endpoint, see `OllamaResponseBuilder` for SSE→NDJSON transform
4. **Log streaming chunks:** Add a debug breakpoint in `ChatStreamingService`
5. **Test with curl:**
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
5. **Debug:** Set a breakpoint in the test method or the service it calls

---

## Common Parameter Gotchas

| Situation | Solution |
|-----------|----------|
| `reasoning_effort` breaks on non-DeepSeek | `RequestTransformer` filters it; check `ParameterValidationTests` |
| `top_p` + `reasoning_effort` causes API error | DeepSeek docs: omit `top_p` when `reasoning_effort` is set (native reasoner rule) |
| `top_k` not supported by OpenAI | Filtered in `RequestTransformer` (DeepSeek, OpenAI, Moonshot remove it) |
| User sends `temperature=0.7` to `kimi-k2.6` | Moonshot's K2.x mandates `temperature=1.0` — the proxy overwrites it via `override_client_params=true`. The test `ApplyExecutionDefaults_OverrideClientParamsTrue_OverwritesClientTemperature` verifies this. |
| User sends `temperature=0.3` to `moonshot-v1-128k` | Moonshot's moonshot-v1 series is fine with user-supplied temperature; `override_client_params` is `false` (absent) so the proxy preserves the user's value. |
| Model not in `/v1/models` list | Check `ModelCatalogService.AvailableModels` or `config/model-selection/` enabled flag |
| `provider/model` hint not routing | `ProviderRegistry.ResolveModel` tries 3 levels: verbatim, strip-prefix, suffix-match within hinted provider. The third level catches NVIDIA's `qwen/qwen3.5-397b-a17b` family-prefixed upstream ids. |
| User requests `kimi-k2.6` and gets Moonshot | Tie-break is `(priority asc, provider order asc)`. Moonshot has priority 1, Ollama Cloud has priority 4 — Moonshot wins. |

---

## Config File Locations

```
config/model-selection/
├── deepseek.json       # v4-pro, v4-flash, coder
├── openai.json         # gpt-5, gpt-5-mini, gpt-4.1, gpt-4o, gpt-oss-120b
├── nvidia.json         # qwen3-coder-480b, kimi-k2.6, nemotron-3-super, gpt-oss-120b, qwen3.5-397b
├── groq.json           # llama-3.3-70b, qwen3-32b, llama-4-scout, gpt-oss-120b, gpt-oss-20b
├── openrouter.json     # qwen3-coder, nemotron, kimi-k2.6, deepseek-v4-pro
├── moonshot.json       # kimi-k2.6, kimi-k2.5, moonshot-v1-* (kimi's have override_client_params=true)
├── cerebras.json       # zai-glm-4.7, gpt-oss-120b
└── ollamacloud.json    # qwen3-coder:480b, qwen3-coder-next, devstral-2:123b, kimi-k2.6, deepseek-v4-pro
```

> `ollamacloud.json` and `ollama.json` both declare `"provider": "ollama"`, so the loader merges them under the `"ollama"` key. The local-ollama `ollama.json` currently exposes only a few matches (most disabled in the May 2026 curation); Ollama Cloud is the production-ready path.

Each file contains model execution defaults (temperature, max_tokens, reasoning_effort, timeout_seconds, **override_client_params**).

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

# Run provider/model hint tests
dotnet test --filter ClassName=ProviderModelHintTests

# Run single test by name
dotnet test --filter TestMethodName=MySpecificTest

# Verbose output
dotnet test --verbosity detailed

# Coverage report
dotnet test /p:CollectCoverage=true
```

---

## Environment Variables

```bash
# Required (set in .env or system)
PROVIDER_DEEPSEEK_API_KEY=sk-xxxxx
PROVIDER_OPENAI_API_KEY=sk-proj-xxxxx
PROVIDER_NVIDIA_API_KEY=nvapi-xxxxx

# Optional
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
                              (override_client_params=true force-overrides client values)
  ↓ [ProviderRegistry] ResolveCandidates() → ordered failover list
  ↓ [Forward] Send to upstream API
  ↓ [OllamaResponseBuilder] If Ollama endpoint, convert OpenAI → Ollama
```

### Multi-Turn Reasoning (DeepSeek)
```
Turn 1: User asks question
  ↓ DeepSeek returns {"reasoning_content": "...", "content": "..."}
  ↓ ReasoningCacheService stores reasoning_content
  ↓ Response sent to user

Turn 2: User asks follow-up
  ↓ RequestTransformer retrieves cached reasoning_content
  ↓ Injects as assistant context
  ↓ Forward to DeepSeek with reasoning continuity
  ↓ Response + new reasoning_content cached
```

### Streaming + Format Conversion
```
Client (Ollama format)
  ├─ POST /api/chat (expects NDJSON)
  │
  ├─ [Transform to OpenAI] (internal)
  │
  ├─ [Forward to upstream] (SSE stream)
  │
  ├─ [ChatStreamingService] SSE → NDJSON (on-the-fly)
  │
  └─ [Stream to client] (Ollama NDJSON)
```

### Provider/Model Hint Resolution (3-level)
```
User sends model = "nvidia/qwen3.5-397b-a17b"
  ↓ Level 1: verbatim full id in registry? NO ("nvidia/qwen3.5-397b-a17b" isn't a key)
  ↓ Level 2: strip prefix → "qwen3.5-397b-a17b". Bare in registry? NO
  ↓ Level 3: look for any NVIDIA-owned upstream whose SUFFIX equals "qwen3.5-397b-a17b"
                → matches "qwen/qwen3.5-397b-a17b" (NVIDIA's actual upstream id)
  ↓ Return "qwen/qwen3.5-397b-a17b"
```

---

## Common Scenarios

### Scenario: User wants to use NVIDIA model via Copilot
1. Copilot sends: `"model": "qwen3-coder-480b-a35b-instruct"`
2. Proxy resolves: `ProviderRegistry.ResolveCandidates("qwen3-coder-480b-a35b-instruct")` → `[("nvidia", "qwen/qwen3-coder-480b-a35b-instruct")]` (only NVIDIA offers it)
3. Forward to: `https://integrate.api.nvidia.com/v1/chat/completions`
4. Return response in OpenAI format

### Scenario: VS 2026 BYOM requests deepseek-v4-pro via Ollama
1. VS sends: `POST /api/chat { "model": "deepseek-v4-pro", "messages": [...] }`
2. Transform: Ollama format → OpenAI format
3. Resolve: DeepSeek provider
4. Forward: `https://api.deepseek.com/v1/chat/completions`
5. Convert response: OpenAI → Ollama format
6. Stream back as NDJSON

### Scenario: Parameter mismatch (e.g. `reasoning_effort` on NVIDIA)
1. Request arrives with `reasoning_effort`
2. `RequestTransformer` detects provider is `nvidia`
3. Removes `reasoning_effort` from transform (NVIDIA upstream rejects it)
4. Forwards to NVIDIA without unsupported param
5. No error, request succeeds

### Scenario: User-supplied temperature on Kimi K2.6
1. User sends: `{"model": "kimi-k2.6", "temperature": 0.3, ...}`
2. Moonshot's K2.6 entry in `moonshot.json` has `override_client_params: true` and `temperature: 1.0`
3. `RequestTransformer.ApplyExecutionDefaults` sees `force=true`, `exec.Temperature=1.0`
4. Overwrites the user's `temperature: 0.3` with `temperature: 1.0` in the upstream body
5. Forward to Moonshot, request succeeds without 400s

---

## Debugging Tips

### Enable Verbose Logging
```bash
export LOG_LEVEL=Debug
dotnet run
```

### Check Model Availability
```bash
curl http://localhost:11434/v1/models | jq '.data[].id'
```

### Test Direct Provider (Bypass Proxy)
```bash
# Direct DeepSeek call
curl -X POST https://api.deepseek.com/v1/chat/completions \
  -H "Authorization: Bearer $PROVIDER_DEEPSEEK_API_KEY" \
  -d '{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}]}'
```

### Monitor Streaming Response
```bash
curl -X POST http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}],"stream":true}' \
  -N  # Disable buffering
```

### Inspect Request Transform
Set a breakpoint in `RequestTransformer.ApplyExecutionDefaults()` and examine the JsonElement before/after.

---

## Performance Notes

- **Connection pooling:** 256 per provider, HTTP/2 enabled
- **Streaming:** Zero-copy pass-through (not buffered)
- **Model metadata:** Loaded once on startup, cached in RAM
- **JSON parsing:** `System.Text.Json` source-generated (no reflection)
- **Typical latency:** <10ms proxy overhead
- **Test count:** 329 tests, all green

---

## Related Docs

- **[API.md](docs/API.md)** — Endpoint specifications and examples
- **[CONFIGURATION.md](docs/CONFIGURATION.md)** — Setup, providers, parameter mapping
- **[ARCHITECTURE.md](docs/ARCHITECTURE.md)** — System design, components, data flow
- **[TESTING.md](docs/TESTING.md)** — Test architecture, running tests, adding new tests
- **[DEPLOYMENT.md](docs/DEPLOYMENT.md)** — Docker, bare metal, monitoring, troubleshooting
