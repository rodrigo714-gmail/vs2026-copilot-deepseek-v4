# Configuration Guide

Complete configuration documentation for the multi-provider proxy.

## Table of Contents

- [Environment Setup](#environment-setup)
- [Provider Configuration](#provider-configuration)
- [Model Selection & Defaults](#model-selection--defaults)
- [Parameter Mapping](#parameter-mapping)
- [Context Window Specifications](#context-window-specifications)
- [Advanced Configuration](#advanced-configuration)

---

## Environment Setup

### Required Environment Variables

Provider API keys must be set as environment variables. The proxy reads from:

```bash
# DeepSeek (https://platform.deepseek.com)
PROVIDER_DEEPSEEK_API_KEY=sk-xxxxx

# OpenAI (https://platform.openai.com)
PROVIDER_OPENAI_API_KEY=sk-proj-xxxxx

# NVIDIA (NIM on-prem or cloud)
PROVIDER_NVIDIA_API_KEY=nvapi-xxxxx

# OpenRouter (https://openrouter.ai)
PROVIDER_OPENROUTER_API_KEY=sk-or-xxxxx

# Groq (https://console.groq.com)
PROVIDER_GROQ_API_KEY=gsk-xxxxx

# Ollama Cloud (https://ollama.ai)
PROVIDER_OLLAMACLOUD_API_KEY=xxxxx

# Moonshot / Kimi (https://api.moonshot.ai) — K2.x models live here
PROVIDER_MOONSHOT_API_KEY=sk-xxxxx

# Cerebras (https://api.cerebras.ai)
PROVIDER_CEREBRAS_API_KEY=csk-xxxxx
```

> At least one provider key must be set. The proxy auto-discovers providers from env vars in `Services/ProviderRegistry.cs` → `DiscoverProviders()`, in this order: `deepseek, openai, nvidia, openrouter, groq, ollama, moonshot, cerebras`. The first provider in the discovery list is the default when a model id can't be resolved to a specific upstream.

### Optional Configuration Variables

```bash
# Listen port (default: 11434)
PROXY_PORT=11434

# Log level (default: Information)
LOG_LEVEL=Debug

# Request timeout in seconds (default: 300)
REQUEST_TIMEOUT=300

# Max streaming chunk size in bytes (default: 4096)
STREAM_CHUNK_SIZE=8192

# Concurrent request limit (default: no limit)
MAX_CONCURRENT_REQUESTS=1000

# Enable metrics logging (default: true)
ENABLE_METRICS=true
```

### .env File Example

Create a `.env` file at the repository root:

```bash
# API Keys
PROVIDER_DEEPSEEK_API_KEY=sk-xxxxx
PROVIDER_OPENAI_API_KEY=sk-proj-xxxxx
PROVIDER_NVIDIA_API_KEY=nvapi-xxxxx

# Proxy Settings
PROXY_PORT=11434
LOG_LEVEL=Information
REQUEST_TIMEOUT=300

# Optional: Model overrides
DEFAULT_MODEL=deepseek-v4-pro
```

The proxy uses `IConfiguration` and will read from:
1. Environment variables
2. `.env` file (via `DotEnv` loading in `Program.cs`)
3. `appsettings.json` (project defaults)

---

## Provider Configuration

### Provider Registry

Configured providers are defined in `Services/ProviderRegistry.cs` and instantiated with their upstream service URLs:

| Provider | Service URL | Status |
|----------|-------------|--------|
| **DeepSeek** | `https://api.deepseek.com` | ✅ Fully supported |
| **OpenAI** | `https://api.openai.com` | ✅ Fully supported |
| **NVIDIA NIM** | `https://integrate.api.nvidia.com` | ✅ Fully supported |
| **OpenRouter** | `https://openrouter.ai/api/` | ✅ Fully supported |
| **Groq** | `https://api.groq.com/openai` | ✅ Fully supported |
| **Ollama Cloud** | `https://ollama.com` | ✅ Fully supported |
| **Moonshot/Kimi** | `https://api.moonshot.ai` | ✅ Fully supported |
| **Cerebras** | `https://api.cerebras.ai` | ✅ Fully supported |

### Provider Selection Logic

The proxy uses this priority when resolving a model request:

1. **Direct Model-to-Provider Mapping** — `ModelSelectionStore.FindModelSelectionEntry()` looks up the requested id in every enabled entry of every provider's JSON config (substring match on `match`). The first hit in priority order wins.
2. **3-level `provider/model` hint resolution** — for the OpenAI-style request form (`groq/llama-3.3-70b-versatile`, `nvidia/qwen3.5-397b-a17b`), the resolver tries:
   - Verbatim full id in the registry
   - Strip the prefix and look up the bare name
   - Find any upstream id owned by the hinted provider whose suffix equals the bare name
3. **Default Provider** — if no hint and no match, fall back to the first discovered provider (typically `deepseek`) using its `DEFAULT_MODEL`.

### Adding a New Provider

1. Create HTTP client factory in `Services/HttpClientFactory.cs`:
```csharp
services.AddHttpClient("NewProvider", client =>
{
    client.BaseAddress = new Uri("https://api.newprovider.com/v1");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
});
```

2. Update `ProviderRegistry.ResolveCandidates()`:
```csharp
case "newmodel-123":
    candidates.Add("newprovider");
    break;
```

3. Create model selection JSON in `config/model-selection/newprovider.json`:
```json
{
  "provider": "newprovider",
  "models": [
    {
      "match": "newmodel-123",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 200000,
        "max_output_tokens": 32000,
        "temperature": 0.7,
        "max_tokens": 4096,
        "timeout_seconds": 120
      }
    }
  ]
}
```

---

## Model Selection & Defaults

### Default Model

The default model is determined by (in priority order):

1. **Request-specified model** (e.g., `"model": "gpt-5"`)
2. **Environment variable** `DEFAULT_MODEL` (if set)
3. **Configuration** `DefaultModel` in `appsettings.json`
4. **Hardcoded fallback** `"deepseek-v4-pro"` in `ModelSelectionStore`

### Model Selection JSON Files

Model metadata and defaults are loaded from `config/model-selection/*.json` (8 files, one per provider):

```
config/model-selection/
├── deepseek.json      # DeepSeek        (2 enabled: v4-pro, v4-flash, +coder-6.7b disabled)
├── openai.json        # OpenAI          (5 enabled: gpt-5, gpt-5-mini, gpt-4.1, gpt-4o, gpt-oss-120b)
├── nvidia.json        # NVIDIA NIM      (5 enabled: qwen3-coder-480b, kimi-k2.6, nemotron-3-super, gpt-oss-120b, qwen3.5-397b)
├── groq.json          # Groq            (5 enabled: llama-3.3-70b, qwen3-32b, llama-4-scout, gpt-oss-120b, gpt-oss-20b)
├── openrouter.json    # OpenRouter      (6 enabled: qwen3.7-plus, qwen3-coder, nemotron-3-super, nemotron-3-ultra, moonshotai/kimi-k2.7-code, deepseek-v4-pro)
├── moonshot.json      # Moonshot/Kimi   (5 enabled: kimi-k2.7-code, kimi-k2.6, kimi-k2.5, moonshot-v1-128k, moonshot-v1-auto)
├── cerebras.json      # Cerebras        (2 enabled: zai-glm-4.7, gpt-oss-120b)
└── ollamacloud.json   # Ollama Cloud    (5 enabled: qwen3-coder:480b, qwen3-coder-next, devstral-2:123b, kimi-k2.6, deepseek-v4-pro)
```

> `ollamacloud.json` and `ollama.json` both declare `"provider": "ollama"`, so the loader merges them under the `"ollama"` key. The local-ollama `ollama.json` currently exposes only a few matches (most disabled in the May 2026 curation); Ollama Cloud is the production-ready path.

The cap of **5 enabled models per provider** is intentional: it keeps the `/v1/models` listing focused, ensures the proxy's per-model execution defaults stay accurate, and makes the curated picks obvious in any IDE autocomplete. The cap is enforced by `ParameterValidationTests.EnabledModelCount_IsCorrect` (per-provider theory).

### Example: DeepSeek Configuration

From `config/model-selection/deepseek.json`:

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
    },
    {
      "match": "deepseek-v4-flash",
      "priority": 2,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 131072,
        "family": "deepseek",
        "temperature": 0.2,
        "max_tokens": 4096,
        "reasoning_effort": "medium",
        "timeout_seconds": 90
      }
    }
  ]
}
```

### Modifying Defaults

To change default parameters for a model, edit its JSON file:

```bash
# Edit deepseek.json
vi config/model-selection/deepseek.json

# Change temperature, max_tokens, reasoning_effort, etc.
# Restart the proxy to reload configuration
```

**Note:** The proxy does NOT reload configuration on-the-fly; restart required.

### Force-Mode Override (`override_client_params`)

A model's `execution` block can declare:

```json
{
  "execution": {
    "temperature": 1.0,
    "top_p": 0.95,
    "max_tokens": 4096,
    "override_client_params": true
  }
}
```

When `override_client_params` is `true`, the proxy **overwrites** any client-supplied value for the four numeric fields (`temperature`, `top_p`, `max_tokens`, `reasoning_effort`) with the configured one. When `false` or absent, the proxy preserves client values and only injects for missing fields.

**Real-world case:** Moonshot Kimi K2.7-code, K2.6, and K2.5 reject any request with `temperature ≠ 1.0`. The proxy uses force-mode to silently correct the value before forwarding. The relevant entries in `moonshot.json`:

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

The behaviour is exercised end-to-end by `tests/ProxyTests/OverrideClientParamsTests.cs` (10 tests).

> Default for all newly-added models: omit the field (or set `false`). Only enable force-mode for models with documented hard requirements that contradict user intuition.

---

## Parameter Mapping

The proxy automatically filters and adapts parameters based on the upstream provider's API requirements.

### Parameter Filtering Rules

| Parameter | DeepSeek | OpenAI | NVIDIA | Groq | OpenRouter | Moonshot/Kimi | Support |
|-----------|----------|--------|--------|------|------------|---------------|---------|
| `temperature` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | All |
| `top_p` | ✅ (⚠️ no con reasoning) | ✅ (⚠️ no con reasoning) | ✅ | ✅ (⚠️ no recomiendan con temp) | ✅ (passthrough) | ✅ | All |
| `top_k` | ❌ | ❌ | ✅ | ✅ | ✅ (passthrough) | ❌ | NVIDIA/Groq/OpenRouter |
| `max_tokens` | ✅ | ✅ | ✅ | ✅ | ✅ (passthrough) | ✅ | All |
| `reasoning_effort` | ✅ (high/max) | ✅ (gpt-5/mini: o-series) | ❌ | ❌ | ❌ (passthrough si modelo lo soporta) | ❌ | DeepSeek/OpenAI |
| `tools` | ✅ | ✅ | ✅ | ❌ | ✅ (passthrough) | ✅ | Most |
| `tool_choice` | ✅ | ✅ | ✅ | ❌ | ✅ (passthrough) | ✅ | Most |
| `function_call` | ❌ (deprecated) | ❌ (deprecated) | ❌ | ❌ | ❌ | ❌ | None |
| `frequency_penalty` | ✅ | ✅ | ✅ | ✅ | ✅ (passthrough) | ✅ | All |
| `presence_penalty` | ✅ | ✅ | ✅ | ✅ | ✅ (passthrough) | ✅ | All |
| `seed` | ✅ | ✅ | ✅ | ✅ | ✅ (passthrough) | ✅ | All |
| `stop` | ✅ | ✅ | ✅ | ✅ | ✅ (passthrough) | ✅ | All |

### Parameter Normalization Examples

**Request with `top_k`:**
```json
{
  "model": "gpt-5",
  "messages": [...],
  "top_k": 100,
  "top_p": 0.9
}
```

**Normalized for OpenAI (no `top_k` support):**
```json
{
  "model": "gpt-5",
  "messages": [...],
  "top_p": 0.9
}
```

**Request with `reasoning_effort`:**
```json
{
  "model": "llama-3.3-70b-versatile",
  "messages": [...],
  "reasoning_effort": "high"
}
```

**Normalized for Groq (no reasoning support):**
```json
{
  "model": "llama-3.3-70b-versatile",
  "messages": [...],
  "temperature": 0.7,
  "top_p": 0.9
}
```

### RequestTransformer Implementation

The `RequestTransformer` class handles parameter normalization:

```csharp
public class RequestTransformer
{
    public string TransformRequest(string provider, string requestJson)
    {
        // 1. Parse incoming request
        // 2. Filter parameters based on provider capabilities
        // 3. Apply default parameter values
        // 4. Validate parameter ranges
        // 5. Return transformed request
    }
}
```

---

## Context Window Specifications

All curated enabled models and their context window limits. The cap is **5 enabled models per provider** (with a few smaller providers exposing 2).

### DeepSeek (2 enabled)

| Model | Context | Max Output | Reasoning | Force-mode |
|-------|---------|-----------|-----------|------------|
| deepseek-v4-pro | 1M tokens | 384k | ✅ native (`reasoning_effort: high`) | false |
| deepseek-v4-flash | 1M tokens | 131k | ✅ native (`reasoning_effort: medium`) | false |
| deepseek-coder-6.7b-instruct | 128k | 8k | ❌ disabled in current curation | — |

### OpenAI (5 enabled)

| Model | Context | Max Output | Notes |
|-------|---------|-----------|-------|
| gpt-5 | 400k | 128k | o-series reasoning, `reasoning_effort: high` |
| gpt-5-mini | 400k | 128k | o-series reasoning, `reasoning_effort: medium` |
| gpt-4.1 | 1M | 32k | Native tools + vision, no reasoning |
| gpt-4o | 128k | 8k | Multimodal, fastest in the 4-series |
| gpt-oss-120b | 131k | 65k | Open-weights reasoning model |

### NVIDIA NIM (5 enabled — coding-first picks)

| Model | Context | Max Output | Notes |
|-------|---------|-----------|-------|
| qwen/qwen3-coder-480b-a35b-instruct | 1M | 65k | Top coding pick, native tools |
| moonshotai/kimi-k2.6 | 256k | 256k | Vision-capable, fast inference |
| nvidia/nemotron-3-super-120b-a12b | 1M | 256k | Long-context MoE |
| openai/gpt-oss-120b | 131k | 65k | OpenAI-compatible reasoning |
| qwen/qwen3.5-397b-a17b | 256k | 16k | Qwen family-prefixed upstream id |

### Groq (5 enabled — speed-first)

| Model | Context | Max Output | Notes |
|-------|---------|-----------|-------|
| llama-3.3-70b-versatile | 131k | 32k | Tool-capable, ultra-fast |
| qwen/qwen3-32b | 131k | 16k | Qwen-32B chat |
| meta-llama/llama-4-scout-17b-16e-instruct | 10M | 16k | Llama 4 Scout (huge context) |
| openai/gpt-oss-120b | 131k | 65k | OpenAI-compatible reasoning |
| openai/gpt-oss-20b | 131k | 65k | Lighter gpt-oss |

### Moonshot/Kimi (5 enabled — Kimi K2.7-code is the latest)

| Model | Context | Max Output | Vision | Force-mode |
|-------|---------|-----------|--------|------------|
| kimi-k2.7-code | 256k | 256k | ❌ (code-focused) | **true (temperature=1.0)** |
| kimi-k2.6 | 256k | 256k | ❌ (code-focused) | **true (temperature=1.0)** |
| kimi-k2.5 | 256k | 256k | ✅ | **true (temperature=1.0)** |
| moonshot-v1-128k | 128k | 32k | ✅ | false |
| moonshot-v1-auto | 128k | 32k | ❌ | false |

### OpenRouter (6 enabled)

| Model | Context | Max Output | Notes |
|-------|---------|-----------|-------|
| qwen/qwen3.7-plus | 1M | 65k | Qwen 3.7 Plus, priority 1 |
| qwen/qwen3-coder | 1M | 262k | Free-tier Qwen coder |
| nvidia/nemotron-3-super-120b-a12b | 1M | 16k | Passthrough to NVIDIA |
| nvidia/nemotron-3-ultra-550b-a55b | 1M | 256k | Ultra variant |
| moonshotai/kimi-k2.7-code | 256k | 256k | Kimi 2.7 code-specialized, force-mode `temperature=1.0` |
| deepseek/deepseek-v4-pro | 1M | 384k | DeepSeek V4 Pro |

### Cerebras (2 enabled)

| Model | Context | Max Output | Notes |
|-------|---------|-----------|-------|
| zai-glm-4.7 | 128k | 32k | GLM 4.7 (Zhipu) |
| gpt-oss-120b | 131k | 65k | OpenAI-compatible reasoning |

### Ollama Cloud (5 enabled — open-weights quantised)

| Model | Context | Max Output | Notes |
|-------|---------|-----------|-------|
| qwen3-coder:480b | 128k | 32k | Top Ollama Cloud coding pick, 1.5T params |
| qwen3-coder-next | 128k | 32k | Qwen coder, next variant |
| devstral-2:123b | 128k | 32k | Mistral's devstral coder |
| kimi-k2.6 | 256k | 256k | Force-mode `temperature=1.0` (inherits Moonshot rule) |
| deepseek-v4-pro | 128k | 32k | DeepSeek V4 Pro quantised |

> The full per-provider roster (with all disabled entries for documentation) is in `config/model-selection/*.json`. To enable a disabled model, set `"enabled": true` in its JSON and restart the proxy.

---

## Advanced Configuration

### Disable Streaming for Testing

Set in `appsettings.json` or via environment variable:

```json
{
  "Proxy": {
    "DisableStreaming": true
  }
}
```

### Custom HTTP Headers

To inject custom headers (e.g., for authentication with internal proxies):

```csharp
// Modify HttpClientFactory.cs
client.DefaultRequestHeaders.Add("X-Custom-Header", "value");
```

### Connection Pool Settings

The proxy uses `SocketsHttpHandler` with:
- **Max connections per server:** 256
- **HTTP/2 multiplexing:** Enabled
- **Connection lifetime:** Infinite (let OS manage)

To adjust, modify `HttpClientFactory.cs`:

```csharp
var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 512,  // Increase if needed
    AutomaticDecompression = DecompressionMethods.All
};
```

### Request Timeout Settings

Per-provider timeout overrides in `config/model-selection/*.json`:

```json
{
  "execution": {
    "timeout_seconds": 60
  }
}
```

Global timeout: `REQUEST_TIMEOUT` environment variable (default 300s).

---

## Related Documentation

- [API.md](API.md) — Endpoint reference and request/response formats
- [ARCHITECTURE.md](ARCHITECTURE.md) — System design and component overview
- [TESTING.md](TESTING.md) — Test coverage and validation procedures
