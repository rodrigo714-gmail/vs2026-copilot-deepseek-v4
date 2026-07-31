# Multi-Provider AI Proxy - API Reference

Complete API documentation for the C# multi-provider proxy supporting 14 providers: DeepSeek, OpenAI, Google, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, Cerebras, Z.AI, ZenMux, Mistral, SiliconFlow and Cloudflare Workers AI.

## Table of Contents

- [Overview](#overview)
- [Dual API Support](#dual-api-support)
- [Health & Diagnostics](#health--diagnostics)
- [Diagnostic Response Headers](#diagnostic-response-headers)
- [OpenAI-Compatible Endpoints](#openai-compatible-endpoints)
- [Ollama-Compatible Endpoints](#ollama-compatible-endpoints)
- [Image Support](#image-support)
- [Request/Response Examples](#requestresponse-examples)
- [Error Handling](#error-handling)
- [Model Resolution](#model-resolution)
- [Free-Tier Budget & Resilience](#free-tier-budget--resilience)
- [Reasoning Content Caching](#reasoning-content-caching)
- [Force-Mode Parameter Override](#force-mode-parameter-override)
- [Authentication & Security](#authentication--security)
- [Usage Dashboard](#usage-dashboard)

---

## Overview

The proxy provides two API interfaces:
1. **OpenAI-compatible** (`/v1/*`) — for GitHub Copilot, Cursor, Continue.dev, OpenAI SDKs
2. **Ollama-compatible** (`/api/*`) — for Visual Studio BYOM, native Ollama clients

Both interfaces route requests to the configured backend provider (any of the 14 registered in `ProviderCapabilitiesRegistry`) based on the requested model name.

### Base URL

```
http://localhost:11434
```

Default port can be overridden via `PROXY_PORT` environment variable.

---

## Health & Diagnostics

### GET /health

Health check endpoint returning proxy status and available providers.

**Request:**
```bash
curl http://localhost:11434/health
```

**Response:**
```json
{
  "status": "ok",
  "providers": [
    "deepseek",
    "openai",
    "nvidia",
    "groq",
    "openrouter",
    "ollama",
    "moonshot",
    "cerebras",
    "zenmux"
  ],
  "availableModels": [
    "deepseek-v4-pro",
    "gpt-5.5",
    "kimi-k2.7-code",
    "glm-5.2",
    "z-ai/glm-5.2",
    "... (~60 models total)"
  ],
  "defaultModel": "deepseek-v4-pro"
}
```

> Providers without env vars are silently skipped — only configured providers are listed.

**Status Codes:**
- `200 OK` — Proxy is healthy and at least one provider is configured

---

## Diagnostic Response Headers

Both `/v1/chat/completions` and `/api/chat` endpoints include diagnostic response headers to help verify routing:

| Header | Description | Example |
|--------|-------------|---------|
| `X-Proxy-Requested-Model` | The model name as sent by the client | `deepseek-v4-pro:latest` |
| `X-Proxy-Resolved-Model` | The resolved internal model id after alias resolution | `deepseek-v4-pro` |
| `X-Proxy-Upstream-Model` | The model id that was sent to the upstream API | `deepseek-v4-pro` |
| `X-Proxy-Provider` | The provider that handled the request | `deepseek`, `zenmux`, `ollama` |
| `X-Proxy-Candidate-Count` | How many providers could have served this model | `1`, `3` |
| `X-Proxy-Candidate-Index` | Zero-based position of the provider that answered — non-zero means the request failed over | `0`, `1` |
| `X-Proxy-Attempts` | How many candidates were actually tried before giving up | `1`, `2` |
| `X-Proxy-Primary-Provider` | Primary candidate provider (OpenAI endpoint only) | `nvidia` |
| `X-Proxy-Primary-Upstream` | Primary upstream model (OpenAI endpoint only) | `z-ai/glm-5.2` |

> Use these headers to verify that the expected provider is being selected. If the provider is unexpected, the model name may need a qualified alias (e.g. `model@provider:latest`).
>
> `X-Proxy-Provider` and `X-Proxy-Upstream-Model` are written immediately before the response body, so they name the provider that **served** the request, not the one tried first. Comparing `X-Proxy-Provider` against `X-Proxy-Primary-Provider` shows a failover at a glance.

---

## OpenAI-Compatible Endpoints

### GET /v1/models

List available models in OpenAI format. **Only returns routable ids** — either bare upstream ids (lowest-priority claimant wins) or fully-qualified `upstream@provider` aliases.

**Request:**
```bash
curl http://localhost:11434/v1/models
```

**Response:**
```json
{
  "object": "list",
  "data": [
    {
      "id": "deepseek-v4-pro",
      "object": "model",
      "created": 1700000000,
      "owned_by": "deepseek"
    },
    {
      "id": "z-ai/glm-5.2",
      "object": "model",
      "created": 1700000000,
      "owned_by": "nvidia"
    },
    "... (~60 total)"
  ]
}
```

### POST /v1/chat/completions

Chat completion endpoint compatible with OpenAI API.

**Request Body:**
```json
{
  "model": "deepseek-v4-pro",
  "messages": [
    {
      "role": "user",
      "content": "Explain quantum computing in simple terms."
    }
  ],
  "stream": false,
  "temperature": 0.7,
  "max_tokens": 2000,
  "top_p": 0.9
}
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `model` | string | Yes | Model ID (e.g., `deepseek-v4-pro`, `glm-5.2`, `kimi-k2.7-code`) |
| `messages` | array | Yes | Message history with `role` (user/assistant/system) and `content` |
| `messages[].content` | string/array | Yes | Plain text **or** multi-part array with `type: "text"` and `type: "image_url"` for vision models |
| `stream` | boolean | No | Enable streaming mode (default: `false`) |
| `temperature` | float | No | Sampling temperature (0.0–2.0) |
| `top_p` | float | No | Nucleus sampling (0.0–1.0) |
| `max_tokens` | integer | No | Max output tokens |
| `reasoning_effort` | string | No | DeepSeek/OpenAI reasoning level: "low", "medium", "high" |

**Multi-part content with images (for vision models):**
```json
{
  "role": "user",
  "content": [
    {"type": "text", "text": "What's in this image?"},
    {"type": "image_url", "image_url": {"url": "data:image/png;base64,iVBOR..."}}
  ]
}
```

> For Ollama-format providers, the proxy automatically converts multi-part content to the `images` array format.

**Supported providers:** DeepSeek, OpenAI, Google, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, Cerebras, Z.AI, ZenMux, Mistral, SiliconFlow, Cloudflare Workers AI. The proxy automatically filters unsupported parameters per provider, and strips `tools`/`tool_choice` for models flagged `supports_tools=false`.

**Diagnostic headers:** Every response includes `X-Proxy-Requested-Model`, `X-Proxy-Resolved-Model`, `X-Proxy-Provider`, `X-Proxy-Candidate-Count`, `X-Proxy-Primary-Provider`, `X-Proxy-Primary-Upstream`.

---

## Ollama-Compatible Endpoints

### GET /api/tags

List available models in Ollama format. Each model is published in two forms:

| Form | `name` | `model` | Routing |
|---|---|---|---|
| **Pinned** | `GROQ - gpt-oss-120b:latest` | `openai/gpt-oss-120b@groq:latest` | Exactly one provider. Never fails over, never reordered around a cooldown. |
| **Automatic** | `AUTO - gpt-oss-120b:latest` | `gpt-oss-120b@auto:latest` | Every provider serving the model, best first, cooling ones last. |

`auto` is a reserved token, not a provider. An AUTO entry is published only where **two or more**
configured providers serve the model — with one provider it would behave identically to the pinned
entry.

Its advertised `context_length`, `max_output_tokens` and `supports_tools` are the **floor** across
its candidates, not the best on offer: a limit the client sizes a request against must hold for
whichever candidate ends up answering it.

**Request:**
```bash
curl http://localhost:11434/api/tags
```

**Response:**
```json
{
  "models": [
    {
      "name": "OLLAMA - deepseek-v4-pro:latest",
      "model": "deepseek-v4-pro@ollama:latest",
      "modified_at": "2026-06-04T10:30:00Z",
      "size": 3826793677,
      "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
      "details": {
        "parent_model": "",
        "format": "api",
        "family": "deepseek",
        "families": ["deepseek"],
        "parameter_size": "api",
        "quantization_level": "none"
      },
      "capabilities": ["completion", "tools"],
      "context_length": 1048576,
      "max_output_tokens": 384000,
      "supports_tools": true,
      "supports_vision": false,
      "supports_images": false
    }
  ]
}
```

**Important:** Send the `model` field back verbatim. `model@provider:latest` routes to that exact
provider instead of falling back to the default; `model@auto:latest` routes to the whole candidate
list in order. A bare model name still resolves, but to the lowest-priority claimant only.

### POST /api/chat

Chat completion endpoint (Ollama-compatible).

**Request Body:**
```json
{
  "model": "glm-5.2",
  "messages": [
    {"role": "user", "content": "What is Rust?"}
  ],
  "stream": false,
  "options": {
    "temperature": 0.7,
    "top_p": 0.9
  }
}
```

**Ollama images format (for vision models):**
```json
{
  "role": "user",
  "content": "What's in this image?",
  "images": ["data:image/png;base64,iVBOR..."]
}
```

> When using the OpenAI-compatible multi-part format, the proxy converts it to Ollama's `images` format automatically.

**Diagnostic headers:** Every response includes `X-Proxy-Requested-Model`, `X-Proxy-Resolved-Model`, `X-Proxy-Upstream-Model`, `X-Proxy-Provider`.

---

## Image Support

Vision-capable models (e.g. `qwen3.7-plus`, `gemini-3.5-flash`) accept images as input. The proxy supports two formats:

### OpenAI Format (multi-part content array)
```json
{
  "role": "user",
  "content": [
    {"type": "text", "text": "What's in this image?"},
    {"type": "image_url", "image_url": {"url": "data:image/png;base64,..."}}
  ]
}
```

### Ollama Format (images array)
```json
{
  "role": "user",
  "content": "What's in this image?",
  "images": ["data:image/png;base64,..."]
}
```

**Auto-conversion:** When using `/api/chat` (Ollama endpoint), the proxy automatically converts OpenAI multi-part content to Ollama's `images` array. When forwarding to OpenAI-compatible providers, it converts Ollama's `images` array to multi-part format.

---

## Error Handling

### Common Error Responses

**400 Bad Request** — Invalid parameter combination:
```json
{
  "error": "reasoning_effort not supported by NVIDIA provider",
  "code": "UNSUPPORTED_PARAMETER"
}
```

**502 Bad Gateway** — All provider candidates failed:
```json
{
  "error": "no provider candidate available",
  "code": "ALL_PROVIDERS_FAILED"
}
```

---

## Model Resolution

### How the Proxy Selects a Provider

1. **Request arrives** with model name
2. **Proxy resolves** via `ProviderRegistry.ResolveModel()` (3-level hint resolution)
3. **Candidate selection** via `ProviderRegistry.ResolveCandidates()`:
   - Bare id like `glm-5.2`: returns every provider offering it, ordered by priority
   - Qualified id like `z-ai/glm-5.2@nvidia`: returns only that provider (no failover)
4. **Route planning** via `ProviderRegistry.ResolveRoutePlan()`: the candidate list is reordered so
   providers currently in cooldown are tried last. Never filtered to empty — a last-ditch attempt
   beats a dead end. A single-candidate pin is untouched.
5. **Failover**: every chat path retries the next candidate, streaming included. The only failure
   that stops the walk early is a genuinely malformed request (`400`/`413`/`422` with no
   rate-limit or model-not-found wording), because it would fail identically everywhere.
6. **Response**: forwarded with diagnostic headers. If every candidate fails, the client receives
   the **last real upstream status and body**, not a synthetic 502.

### 3-level `provider/model` hint resolution

`ProviderRegistry.ResolveModel()` handles the OpenAI-style `provider/model` form:

1. **Verbatim** — full id exists in the registry
2. **Strip prefix** — strip the provider prefix and look up the bare name
3. **Suffix match within hinted provider** — find any upstream id owned by the hinted provider whose suffix equals the bare name

---

## Free-Tier Budget & Resilience

### GET /api/free-tier/summary

Everything needed to answer "how much free allowance do I have left" in one response: the published
allowance per provider, spend so far this month, and any active cooldown.

```bash
curl http://localhost:11434/api/free-tier/summary
```

```json
{
  "curated_at": "2026-07-31",
  "persistent": true,
  "totals": {
    "steady_monthly_tokens": 130200000,
    "signup_credit_tokens": 5000000,
    "uncapped_providers": ["zai", "nvidia"],
    "used_this_month": 203,
    "remaining": 130199797,
    "pct_used": 0.0
  },
  "providers": [
    {
      "provider": "google",
      "display_name": "Google Gemini",
      "free_type": "RecurringDaily",
      "monthly_tokens": 62000000,
      "daily_tokens": 2000000,
      "requests_per_minute": 15,
      "used_this_month": 0,
      "used_today": 0,
      "requests_today": 0,
      "remaining": 62000000,
      "pct_used": 0.0,
      "rate_limited_today": 0,
      "quota_exhausted_today": 0,
      "cost_usd_this_month": 0.0,
      "tos": "caution",
      "tos_note": "The free tier is scoped to developers building with Google AI models.",
      "signup_url": "https://aistudio.google.com",
      "verified_at": "2026-07-31",
      "cooldown": null
    }
  ]
}
```

Accounting rules, which exist because free-tier totals are easy to inflate:

- **Shared pools count once.** Providers serving several model variants from one budget share a
  `pool_key`; summing the variants would multiply the same allowance several-fold.
- **Uncapped tiers are listed, never summed.** A permanently free provider that publishes only a
  rate limit appears in `uncapped_providers` but contributes 0 to `steady_monthly_tokens` —
  extrapolating `RPM × 24/7` produces a ceiling nobody reaches.
- **Signup credits are separate.** They do not recur, so they never enter the steady figure.
- `persistent: false` means the data directory is not writable and usage is memory-only.

Fields are omitted when null, so a provider with no free tier has no `monthly_tokens` key.

### GET /api/resilience/cooldowns

```json
{
  "cooldowns": [
    {
      "provider": "groq",
      "display_name": "Groq",
      "model": null,
      "kind": "QuotaExhausted",
      "quota_period": "Daily",
      "reason": "daily-limit",
      "failure_count": 1,
      "until_utc": "2026-08-01T00:00:00.0000000Z",
      "seconds_remaining": 35998
    }
  ],
  "recent_failovers": [
    {
      "at_utc": "2026-07-31T12:06:07.0000000Z",
      "from_provider": "groq",
      "to_provider": "nvidia",
      "model": "openai/gpt-oss-120b",
      "status_code": 429,
      "kind": "QuotaExhausted",
      "latency_ms": 58
    }
  ]
}
```

`model` is `null` for a provider-wide stand-down and set for a `ModelUnavailable` lockout, which is
scoped to that one model so the rest of the provider keeps serving.

### POST /api/resilience/reset

Re-enables a provider immediately — useful after fixing an API key, when waiting out the timer is
pointless.

```bash
curl -X POST "http://localhost:11434/api/resilience/reset?provider=groq"
curl -X POST "http://localhost:11434/api/resilience/reset?provider=nvidia&model=kimi-k2.6"
curl -X POST "http://localhost:11434/api/resilience/reset"     # clears everything
```

The failover history is a log and deliberately survives a reset.

---

## Force-Mode Parameter Override

Some models have hard requirements. The proxy uses `override_client_params` for:
- Moonshot Kimi K2.x models (mandate `temperature=1.0`)
- Ollama Cloud `kimi-k2.7-code` (same rule)

---

## Authentication & Security

Provider API keys are set via environment variables, one per provider:

| Provider | Variable | Notes |
|---|---|---|
| DeepSeek | `PROVIDER_DEEPSEEK_API_KEY` | |
| OpenAI | `PROVIDER_OPENAI_API_KEY` | |
| Google | `PROVIDER_GOOGLE_API_KEY` | |
| NVIDIA NIM | `PROVIDER_NVIDIA_API_KEY` | |
| Groq | `PROVIDER_GROQ_API_KEY` | |
| OpenRouter | `PROVIDER_OPENROUTER_API_KEY` | |
| Ollama Cloud | `PROVIDER_OLLAMACLOUD_API_KEY` | `PROVIDER_OLLAMA_API_KEY` also accepted |
| Moonshot/Kimi | `PROVIDER_MOONSHOT_API_KEY` | |
| Cerebras | `PROVIDER_CEREBRAS_API_KEY` | |
| Z.AI | `PROVIDER_ZAI_API_KEY` | |
| ZenMux | `PROVIDER_ZENMUX_API_KEY` | |
| Mistral | `PROVIDER_MISTRAL_API_KEY` | 🆓 free tier |
| SiliconFlow | `PROVIDER_SILICONFLOW_API_KEY` | 🆓 free tier |
| Cloudflare Workers AI | `PROVIDER_CLOUDFLARE_API_KEY` | 🆓 **also requires `PROVIDER_CLOUDFLARE_BASE_URL`** — the URL embeds your account id, so there is no usable default and the provider is skipped at startup until it is set |

Each provider also accepts `PROVIDER_{PREFIX}_BASE_URL` to override its endpoint.

**Proxy-level auth.** `PROXY_API_KEY` is unrelated to any provider key — it guards access to the
proxy itself. When set, requests need `Authorization: Bearer <key>` (or `X-Proxy-Key: <key>`).
`/dashboard`, `/vendor/` and `/health` are exempt so the page loads in a browser; set
`PROXY_DASHBOARD_PUBLIC=false` to remove that exemption. **Never confuse a provider key with the
proxy key.**

---

## Compatibility Matrix

| Client | Endpoint | Protocol | Status |
|--------|----------|----------|--------|
| GitHub Copilot | `/v1/*` | OpenAI | ✅ Fully supported |
| Cursor | `/v1/*` | OpenAI | ✅ Fully supported |
| Continue.dev | `/v1/*` | OpenAI | ✅ Fully supported |
| VS 2026 BYOM | `/api/*` | Ollama | ✅ Fully supported |
| Native Ollama Client | `/api/*` | Ollama | ✅ Fully supported |
| OpenAI SDK | `/v1/*` | OpenAI | ✅ Fully supported |
## Usage Dashboard

The proxy includes a real-time usage dashboard with cost tracking, latency metrics, and LLM Arena performance data.

### Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/dashboard` | The dashboard page (static, `wwwroot/dashboard.html`, Chart.js vendored locally) |
| GET | `/usage` | Full JSON report: cost, latency, tokens per model, Arena data |
| GET | `/usage/summary` | Text summary for quick terminal view |
| GET | `/usage/pricing` | Complete price catalog with Arena scores |
| POST | `/usage/reset` | Reset all counters (admin) |
| GET | `/api/usage` | Live per-provider stats (tokens, latency, RPM, rate-limit headers) |
| GET | `/api/billing` | Balance probes: DeepSeek, OpenAI and OpenRouter query real APIs; others report a note |
| GET | `/api/free-tier/summary` | Free allowance, spend and cooldowns — see above |

**Authentication.** With `PROXY_API_KEY` set, `/dashboard`, `/vendor/` and `/health` stay reachable
without a token, because a browser cannot attach a bearer token to a plain navigation. Every data
endpoint above still requires one — they expose spend, quota and key metadata. The page reads the
key from `?key=` (or `localStorage`) and sends it as `X-Proxy-Key`. Set
`PROXY_DASHBOARD_PUBLIC=false` to require the token for the page as well. Prefix matching is
path-boundary aware, so `/dashboard-secret` is still rejected.

### `/usage` Response Example

```json
{
  "total_cost_usd": 0.1234,
  "models": [
    {
      "provider": "zai",
      "model": "glm-5.2",
      "tier": "paid",
      "requests": 47,
      "success_rate_pct": 95.7,
      "latency_avg_ms": 1840,
      "tokens_in": 150588,
      "tokens_out": 38164,
      "cost_usd": 0.118,
      "arena": { "elo": 1481, "webdev": 1593, "agent_win_rate_pct": 4.4 }
    }
  ]
}
```

### `/usage/pricing`

Returns the full pricing catalog with official provider costs, LLM Arena scores, and estimated tokens/second for each model.

### Logging

When `PROXY_LOG_LEVEL=info`, each request is logged with:

```
[HH:mm:ss] REQ  model -> provider (1/1 candidates)
[HH:mm:ss] OK   provider/model 200 1840ms in:3204 out:812 $0.0078 [Arena:1481]
[HH:mm:ss] FAIL provider/model 429 2103ms
```

### Economics

The dashboard enables data-driven provider decisions:
- **Real cost per model**: actual tokens used x official pricing
- **Success rate**: which providers/models are reliable
- **Latency**: avg/min/max per provider per model
- **Arena comparison**: ELO, WebDev, Agent win rates for context
- **Weekly/monthly projections**: extrapolate from actual usage

### Configuration (.env)

```
PROXY_LOG_LEVEL=info          # info|debug|warn|error|none
PROXY_LOG_FILE=               # path to log file (empty = console only)
```
