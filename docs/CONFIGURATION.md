# Configuration Guide

Complete configuration documentation for the multi-provider proxy supporting DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Moonshot/Kimi, Cerebras, Ollama Cloud, and ZenMux.

## Table of Contents

- [Environment Setup](#environment-setup)
- [Provider Configuration](#provider-configuration)
- [Model Selection & Defaults](#model-selection--defaults)
- [Qualified Model Aliases](#qualified-model-aliases)
- [Diagnostic Response Headers](#diagnostic-response-headers)
- [Parameter Mapping](#parameter-mapping)
- [Context Window Specifications](#context-window-specifications)
- [Advanced Configuration](#advanced-configuration)

---

## Environment Setup

### Required Environment Variables

Provider API keys must be set as environment variables. The proxy reads from:

1. `.env` file (loaded by `Program.cs` if present in the project root)
2. System environment variables
3. `appsettings.json`

**Discovery order:** `deepseek, openai, nvidia, openrouter, groq, ollama, moonshot, cerebras, zenmux`

### Provider Configuration

Each provider requires an API key and optionally a custom base URL:

```bash
# ── DeepSeek ───────────────────────────────────────────────────────
PROVIDER_DEEPSEEK_API_KEY=sk-your-deepseek-key
PROVIDER_DEEPSEEK_BASE_URL=https://api.deepseek.com

# ── OpenAI ─────────────────────────────────────────────────────────
PROVIDER_OPENAI_API_KEY=sk-your-openai-key
PROVIDER_OPENAI_BASE_URL=https://api.openai.com

# ── NVIDIA NIM ─────────────────────────────────────────────────────
PROVIDER_NVIDIA_API_KEY=nvapi-your-nvidia-key
PROVIDER_NVIDIA_BASE_URL=https://integrate.api.nvidia.com

# ── OpenRouter ─────────────────────────────────────────────────────
PROVIDER_OPENROUTER_API_KEY=sk-or-v1-your-key
PROVIDER_OPENROUTER_BASE_URL=https://openrouter.ai/api

# ── Groq ───────────────────────────────────────────────────────────
PROVIDER_GROQ_API_KEY=gsk_your-groq-key
PROVIDER_GROQ_BASE_URL=https://api.groq.com/openai

# ── Ollama Cloud ───────────────────────────────────────────────────
PROVIDER_OLLAMACLOUD_API_KEY=your-ollama-cloud-key

# ── Moonshot/Kimi ──────────────────────────────────────────────────
PROVIDER_MOONSHOT_API_KEY=sk-your-moonshot-key
PROVIDER_MOONSHOT_BASE_URL=https://api.moonshot.ai

# ── Cerebras ───────────────────────────────────────────────────────
PROVIDER_CEREBRAS_API_KEY=csk-your-cerebras-key
PROVIDER_CEREBRAS_BASE_URL=https://api.cerebras.ai

# ── ZenMux ─────────────────────────────────────────────────────────
PROVIDER_ZENMUX_API_KEY=your-zenmux-key
PROVIDER_ZENMUX_BASE_URL=https://zenmux.ai/api

# ── General ────────────────────────────────────────────────────────
DEEPSEEK_MODEL=deepseek-v4-pro
PROXY_PORT=11434
PROXY_API_KEY=          # optional: set to require auth on the proxy
```

### Base URLs

| Provider | Default Base URL | Notes |
|----------|-----------------|-------|
| DeepSeek | `https://api.deepseek.com` | - |
| OpenAI | `https://api.openai.com` | - |
| NVIDIA NIM | `https://integrate.api.nvidia.com` | - |
| OpenRouter | `https://openrouter.ai/api` | OpenAI-compatible |
| Groq | `https://api.groq.com/openai` | OpenAI-compatible |
| Ollama Cloud | `https://ollama.com` | Ollama API format |
| Moonshot/Kimi | `https://api.moonshot.ai` | - |
| Cerebras | `https://api.cerebras.ai` | - |
| ZenMux | `https://zenmux.ai/api` | Multi-model aggregator |

> Only providers with configured API keys are active at runtime. Set `PROVIDER_*_BASE_URL` to override the default (e.g., for self-hosted or region-specific endpoints).

---

## Model Selection & Defaults

### Configuration Files

Model metadata lives in `config/model-selection/{provider}.json`:

```
config/model-selection/
├── deepseek.json       # DeepSeek models
├── openai.json         # OpenAI models
├── nvidia.json         # NVIDIA NIM models
├── groq.json           # Groq models
├── openrouter.json     # OpenRouter models
├── moonshot.json       # Moonshot/Kimi models
├── cerebras.json       # Cerebras models
├── ollamacloud.json    # Ollama Cloud models
├── ollama.json         # Local Ollama (merged with ollamacloud)
└── zenmux.json         # ZenMux models
```

### Model Entry Format

```json
{
  "provider": "zenmux",
  "models": [
    {
      "match": "glm-5.2-free",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1000000,
        "max_output_tokens": 65536,
        "supports_tools": true,
        "supports_vision": false,
        "family": "z-ai",
        "temperature": 0.2,
        "top_p": 0.9,
        "max_tokens": 16384,
        "timeout_seconds": 240
      }
    }
  ]
}
```

### Field Reference

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `provider` | string | Yes | Provider name (must match registry) |
| `models[].match` | string | Yes | Model name substring to match |
| `models[].priority` | int | Yes | Priority order (lower = higher priority) |
| `models[].enabled` | bool | Yes | Include in model lists |
| `models[].execution.context_length` | int | No | Max context window in tokens |
| `models[].execution.max_output_tokens` | int | No | Max output tokens |
| `models[].execution.supports_tools` | bool | No | Tool calling support |
| `models[].execution.supports_vision` | bool | No | Vision/image input support |
| `models[].execution.family` | string | No | Model family name |
| `models[].execution.temperature` | float | No | Default temperature |
| `models[].execution.top_p` | float | No | Default top_p |
| `models[].execution.max_tokens` | int | No | Default max_tokens |
| `models[].execution.reasoning_effort` | string | No | "low", "medium", "high" |
| `models[].execution.timeout_seconds` | int | No | Request timeout |
| `models[].execution.override_client_params` | bool | No | Force-override client values |

### Override Client Params

When `override_client_params: true`, the proxy overwrites client-supplied values for `temperature`, `top_p`, `max_tokens`, and `reasoning_effort` with the configured values.

Currently enabled for:
- Moonshot `kimi-k2.7-code`, `kimi-k2.6`, `kimi-k2.5`
- Ollama Cloud `kimi2.7-code`, `kimi-k2.6`
- ZenMux `kimi-k2.7-code-free`, `kimi-k2.6`

---

## Qualified Model Aliases

The `/api/tags` endpoint now returns `model` fields in the format `model@provider:latest` (e.g., `deepseek-v4-pro@ollama:latest`). This qualified alias ensures that when the client sends this model name back in a request, the proxy routes it to the **correct specific provider** instead of falling back to the default provider (DeepSeek).

**How it works:**
1. `/api/tags` returns: `"model": "deepseek-v4-pro@ollama:latest"`
2. Client sends: `{"model": "deepseek-v4-pro@ollama:latest"}`
3. `ProviderRegistry.ResolveModel()` strips `:latest` → `deepseek-v4-pro@ollama`
4. The `@ollama` suffix forces routing to the Ollama provider (no failover)

**For bare model names** (e.g., `deepseek-v4-pro`), the proxy resolves to the lowest-priority claimant provider based on discovery order. To pin a specific provider, use the qualified `model@provider` form.

---

## Diagnostic Response Headers

Both endpoints include response headers for debugging routing decisions:

| Header | Source | Description |
|--------|--------|-------------|
| `X-Proxy-Requested-Model` | Both | What the client sent |
| `X-Proxy-Resolved-Model` | Both | Internal model after resolution |
| `X-Proxy-Upstream-Model` | Both | Model sent to upstream API |
| `X-Proxy-Provider` | Both | Provider that handled the request |
| `X-Proxy-Candidate-Count` | `/v1/*` | Number of failover candidates |
| `X-Proxy-Primary-Provider` | `/v1/*` | Primary candidate provider |
| `X-Proxy-Primary-Upstream` | `/v1/*` | Primary upstream model |

---

## Parameter Mapping

The following table shows which parameters are supported by each provider:

| Provider | temperature | top_p | top_k | reasoning_effort | tools |
|----------|:-----------:|:-----:|:-----:|:-----------------:|:-----:|
| DeepSeek | ✅ | ⚠️ omitted w/ reasoning | ❌ | ✅ | ✅ |
| OpenAI | ✅ | ⚠️ omitted w/ reasoning | ❌ | ✅ (o-series) | ✅ |
| NVIDIA NIM | ✅ | ✅ | ✅ | ❌ | ✅ |
| Groq | ✅ | ✅ | ✅ | ❌ | ❌ |
| OpenRouter | ✅ | ✅ | ✅ | ❌ (passthrough) | ✅ |
| Ollama Cloud | ✅ | ✅ | ✅ | ❌ | ✅ |
| Moonshot/Kimi | ✅ | ✅ | ❌ | ❌ | ✅ |
| Cerebras | ✅ | ✅ | ✅ | ❌ | ✅ |
| ZenMux | ✅ | ✅ | ❌ | ❌ | ✅ |

**Key rules:**
- `reasoning_effort` is only for DeepSeek and OpenAI o-series. Filtered for all others.
- `top_k` is removed for DeepSeek, OpenAI, Moonshot/Kimi, and ZenMux.
- `top_p` is omitted when `reasoning_effort` is set (DeepSeek/OpenAI rule).
- `tools` are removed for Groq (API quirk).

---

## Context Window Specifications

Enabled models by provider:

| Provider | # Enabled | Models |
|----------|:---------:|--------|
| DeepSeek | 2 | deepseek-v4-pro (1M ctx), deepseek-v4-flash (1M ctx) |
| OpenAI | 5 | gpt-5 (400K), gpt-5-mini (400K), gpt-4.1 (1M), gpt-4o (128K), gpt-oss-120b (131K) |
| NVIDIA NIM | 5 | qwen3-coder-480b (1M), kimi-k2.6 (262K), nemotron-3-super (1M), gpt-oss-120b (131K), qwen3.5-397b (262K) |
| Groq | 5 | llama-3.3-70b (131K), qwen3-32b (131K), llama-4-scout (10M), gpt-oss-120b (131K), gpt-oss-20b (131K) |
| OpenRouter | 7 | qwen3.7-plus (1M), qwen3-coder (1M), nemotron-3-super (1M), nemotron-3-ultra (1M), kimi-k2.7-code (262K), deepseek-v4-pro (1M) |
| Moonshot/Kimi | 6 | kimi-k2.7-code (262K), kimi-k2.6 (262K), kimi-k2.5 (262K), moonshot-v1-128k (131K), moonshot-v1-auto (131K), moonshot-v1-32k (32K) |
| Cerebras | 2 | zai-glm-4.7 (128K), gpt-oss-120b (131K) |
| Ollama Cloud | 10 | kimi2.7-code (262K), glm-5.2 (1M), minimax-m3 (1M), qwen3-coder:480b (1M), qwen3-coder-next (1M), devstral-2:123b (128K), kimi-k2.6 (262K), deepseek-v4-pro (1M), mistral-medium-3.5 (128K) |
| ZenMux | 2 (free) | **glm-5.2-free 🆓** (1M), **kimi-k2.7-code-free 🆓** (262K, vision, reasoning) |

---

## Advanced Configuration

### Local Ollama

The proxy can also connect to a local Ollama instance. Set these in `.env`:

```bash
PROVIDER_OLLAMA_BASE_URL=http://localhost:11434
```

This registers a second "ollama" provider that works without an API key. When both Ollama Cloud and local Ollama are configured, the `ollamacloud.json` and `ollama.json` configs are merged under the `ollama` provider key.

### Legacy DeepSeek Format

```bash
# Backward compatible — works without PROVIDER_ prefix
DEEPSEEK_API_KEY=sk-your-key
DEEPSEEK_BASE_URL=https://api.deepseek.com
```

### Proxy Authentication

```bash
PROXY_API_KEY=your-proxy-key    # Requires bearer token on all endpoints
```

### Port Configuration

```bash
PROXY_PORT=8080                 # Default: 11434