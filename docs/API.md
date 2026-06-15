# Multi-Provider AI Proxy - API Reference

Complete API documentation for the C# multi-provider proxy supporting DeepSeek, OpenAI, NVIDIA NIM, OpenRouter, Groq, Moonshot/Kimi, Cerebras, and Ollama Cloud.

## Table of Contents

- [Overview](#overview)
- [Dual API Support](#dual-api-support)
- [Health & Diagnostics](#health--diagnostics)
- [OpenAI-Compatible Endpoints](#openai-compatible-endpoints)
- [Ollama-Compatible Endpoints](#ollama-compatible-endpoints)
- [Request/Response Examples](#requestresponse-examples)
- [Error Handling](#error-handling)
- [Model Resolution](#model-resolution)
- [Reasoning Content Caching](#reasoning-content-caching)
- [Force-Mode Parameter Override](#force-mode-parameter-override)

---

## Overview

The proxy provides two API interfaces:
1. **OpenAI-compatible** (`/v1/*`) — for GitHub Copilot, Cursor, Continue.dev, OpenAI SDKs
2. **Ollama-compatible** (`/api/*`) — for Visual Studio BYOM, native Ollama clients

Both interfaces route requests to the configured backend provider (DeepSeek, OpenAI, NVIDIA, OpenRouter, Groq, Moonshot/Kimi, Cerebras, Ollama Cloud) based on the requested model name.

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
    "cerebras"
  ],
  "availableModels": [
    "deepseek-v4-pro",
    "deepseek-v4-flash",
    "gpt-5",
    "kimi-k2.7-code",
    "kimi-k2.6",
    "zai-glm-4.7",
    "qwen3-coder:480b",
    "... (~40 models, 5 enabled per provider, 1-2 for DeepSeek/Cerebras/Ollama)"
  ],
  "defaultModel": "deepseek-v4-pro"
}
```

> Providers that have no `PROVIDER_*_API_KEY` env var set are silently skipped — only the providers you configured are listed.

**Status Codes:**
- `200 OK` — Proxy is healthy and at least one provider is configured

---

## OpenAI-Compatible Endpoints

### GET /v1/models

List available models in OpenAI format. **Only returns routable ids** — either bare upstream ids (lowest-priority claimant wins) or fully-qualified `upstream@provider` aliases. Raw `provider/model` strings are intentionally **not** listed because the proxy's routing layer cannot accept them on POST requests.

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
      "id": "kimi-k2.7-code",
      "object": "model",
      "created": 1700000000,
      "owned_by": "moonshot"
    },
    {
      "id": "kimi-k2.6",
      "object": "model",
      "created": 1700000000,
      "owned_by": "moonshot"
    },
    "... (5 enabled per provider, ~40 total)"
  ]
}
```

**Notes:**
- A bare `id` (e.g. `kimi-k2.6`) means the lowest-priority claimant provider wins. If multiple providers offer the same upstream id (e.g. NVIDIA and Groq both expose `openai/gpt-oss-120b`), NVIDIA wins by discovery order (`deepseek, openai, nvidia, openrouter, groq, ollama, moonshot, cerebras`).
- A qualified `id` like `kimi-k2.6@moonshot` forces routing to that specific provider with no failover. Use this when you need to pin the upstream.
- Use `POST /v1/chat/completions` with the chosen id verbatim — the proxy resolves both forms.

---

### POST /v1/chat/completions

Chat completion endpoint compatible with OpenAI API.

**Request Headers:**
```
Content-Type: application/json
Authorization: Bearer {optional-api-key}  (typically not needed for proxy)
```

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
  "top_p": 0.9,
  "reasoning_effort": "medium"
}
```

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `model` | string | Yes | Model ID (e.g., `deepseek-v4-pro`, `gpt-5`, `kimi-k2.7-code`) |
| `messages` | array | Yes | Message history with `role` (user/assistant/system) and `content` |
| `stream` | boolean | No | Enable streaming mode (default: `false`) |
| `temperature` | float | No | Sampling temperature (0.0–2.0, default: 0.7) |
| `top_p` | float | No | Nucleus sampling (0.0–1.0, default: 0.9) |
| `max_tokens` | integer | No | Max output tokens (default: model-specific) |
| `reasoning_effort` | string | No | DeepSeek/OpenAI reasoning level: "low", "medium", "high", "default" |

**Response (Non-streaming):**
```json
{
  "id": "chatcmpl-8ABC123",
  "object": "chat.completion",
  "created": 1700000000,
  "model": "deepseek-v4-pro",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "Quantum computing leverages quantum bits (qubits)..."
      },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 42,
    "completion_tokens": 156,
    "total_tokens": 198
  }
}
```

**Response (Streaming - SSE):**
```
data: {"choices":[{"delta":{"content":"Quantum"},"finish_reason":null}]}
data: {"choices":[{"delta":{"content":" computing"},"finish_reason":null}]}
...
data: [DONE]
```

**Status Codes:**
- `200 OK` — Successful response
- `400 Bad Request` — Invalid request format or unsupported parameter combination
- `401 Unauthorized` — Authentication failed with upstream provider
- `502 Bad Gateway` — All provider candidates failed or downstream error
- `503 Service Unavailable` — No providers configured

**Notes:**
- `reasoning_effort` is only supported by DeepSeek and OpenAI (o-series) models. The proxy automatically filters this parameter for unsupported providers.
- When `reasoning_effort` is set, `top_p` is omitted per DeepSeek/OpenAI documentation to avoid undefined behavior.
- `top_k` is automatically filtered for providers that do not support it: DeepSeek, OpenAI, and Moonshot/Kimi. It is preserved for NVIDIA, Groq, and OpenRouter.
- The proxy caches `reasoning_content` from DeepSeek responses and reinjects it on subsequent assistant messages.
- **Moonshot Kimi K2.x force-mode:** the proxy always sets `temperature=1.0` for `kimi-k2.7-code`, `kimi-k2.6`, and `kimi-k2.5` regardless of what the client sends. These models reject any request where `temperature ≠ 1.0`. See [Force-Mode Parameter Override](#force-mode-parameter-override).
- Supported providers: DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, Cerebras.

---

## Ollama-Compatible Endpoints

### GET /api/version

Get proxy version (Ollama-compatible).

**Request:**
```bash
curl http://localhost:11434/api/version
```

**Response:**
```json
{
  "version": "0.5.7"
}
```

---

### GET /api/tags

List available models in Ollama format.

**Request:**
```bash
curl http://localhost:11434/api/tags
```

**Response:**
```json
{
  "models": [
    {
      "name": "deepseek-v4-pro:latest",
      "model": "deepseek-v4-pro",
      "modified_at": "2026-06-04T10:30:00Z",
      "size": 10737418240,
      "digest": "abc123def456"
    },
    "...other models..."
  ]
}
```

---

### GET /api/show

Get model details (GET variant).

**Request:**
```bash
curl "http://localhost:11434/api/show?model=deepseek-v4-pro"
```

**Query Parameters:**
- `model` (string, required) — Model ID to retrieve details for

**Response:**
```json
{
  "name": "deepseek-v4-pro",
  "model": "deepseek-v4-pro",
  "details": {
    "parameter_size": "680B",
    "quantization_level": "native",
    "family": "deepseek",
    "families": ["deepseek"],
    "ParameterSize": "680B",
    "Quantization": "native"
  },
  "modelfile": "FROM deepseek-v4-pro\nSET temperature 0.7\nSET top_p 0.9",
  "template": "{{ .Prompt }}",
  "parameters": "temperature 0.7 top_p 0.9"
}
```

---

### POST /api/show

Get model details (POST variant).

**Request:**
```bash
curl -X POST http://localhost:11434/api/show \
  -H "Content-Type: application/json" \
  -d '{"model": "deepseek-v4-pro"}'
```

**Request Body:**
```json
{
  "model": "deepseek-v4-pro"
}
```

**Response:** Same as GET /api/show

---

### POST /api/chat

Chat completion endpoint (Ollama-compatible).

**Request:**
```bash
curl -X POST http://localhost:11434/api/chat \
  -H "Content-Type: application/json" \
  -d '{
    "model": "deepseek-v4-pro",
    "messages": [
      {"role": "user", "content": "What is Rust?"}
    ],
    "stream": false
  }'
```

**Request Body:**
```json
{
  "model": "deepseek-v4-pro",
  "messages": [
    {
      "role": "user",
      "content": "What is Rust?"
    }
  ],
  "stream": false,
  "keep_alive": "5m",
  "options": {
    "temperature": 0.7,
    "top_p": 0.9
  }
}
```

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `model` | string | Model ID (required) |
| `messages` | array | Message history (required) |
| `stream` | boolean | Streaming mode (default: false) |
| `keep_alive` | string | Session duration (default: "5m") |
| `options` | object | Sampling options (temperature, top_p, etc.) |

**Response (Non-streaming):**
```json
{
  "model": "deepseek-v4-pro",
  "created_at": "2026-06-04T10:30:00Z",
  "message": {
    "role": "assistant",
    "content": "Rust is a systems programming language..."
  },
  "done": true,
  "total_duration": 2345000000,
  "load_duration": 234000000,
  "prompt_eval_count": 12,
  "prompt_eval_duration": 500000000,
  "eval_count": 89,
  "eval_duration": 1611000000
}
```

**Response (Streaming - NDJSON):**
```
{"model":"deepseek-v4-pro","created_at":"2026-06-04T10:30:00Z","message":{"role":"assistant","content":"Rust"},"done":false}
{"model":"deepseek-v4-pro","created_at":"2026-06-04T10:30:00Z","message":{"role":"assistant","content":" is"},"done":false}
...
{"model":"deepseek-v4-pro","created_at":"2026-06-04T10:30:00Z","message":{},"done":true,"total_duration":2345000000,"load_duration":234000000,"prompt_eval_count":12,"prompt_eval_duration":500000000,"eval_count":89,"eval_duration":1611000000}
```

---

## Request/Response Examples

### Example 1: DeepSeek Reasoning Model with Streaming

```bash
curl -X POST http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "deepseek-v4-pro",
    "messages": [
      {
        "role": "user",
        "content": "Solve: 2x + 5 = 15"
      }
    ],
    "stream": true,
    "reasoning_effort": "medium",
    "max_tokens": 8000
  }'
```

**Response Stream:**
```
data: {"choices":[{"delta":{"thinking":"Let me solve this equation..."},"finish_reason":null}]}
data: {"choices":[{"delta":{"content":"The solution is x = 5"},"finish_reason":"stop"}]}
data: [DONE]
```

### Example 2: OpenAI GPT-5 Multi-turn Conversation

```bash
curl -X POST http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-5",
    "messages": [
      {
        "role": "user",
        "content": "What is machine learning?"
      },
      {
        "role": "assistant",
        "content": "Machine learning is a subfield of artificial intelligence..."
      },
      {
        "role": "user",
        "content": "Give me a concrete example."
      }
    ],
    "temperature": 0.5,
    "max_tokens": 1000
  }'
```

### Example 3: Ollama Cloud with Options

```bash
curl -X POST http://localhost:11434/api/chat \
  -H "Content-Type: application/json" \
  -d '{
    "model": "kimi-k2.7-code",
    "messages": [
      {"role": "user", "content": "summarize cloud computing"}
    ],
    "options": {
      "temperature": 1.0,
      "top_p": 0.95
    },
    "stream": false
  }'
```

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

**401 Unauthorized** — Provider API key invalid/expired:
```json
{
  "error": "Invalid API key for deepseek provider",
  "code": "AUTH_FAILED"
}
```

**502 Bad Gateway** — All provider candidates failed:
```json
{
  "error": "no provider candidate available",
  "code": "ALL_PROVIDERS_FAILED"
}
```

**503 Service Unavailable** — No providers configured:
```json
{
  "error": "no provider candidate available",
  "code": "NO_PROVIDERS"
}
```

### Retry Logic

For streaming requests, if the upstream provider fails after headers are sent, the connection may terminate. Use exponential backoff for retries:

```python
import time
backoff = 1
for attempt in range(3):
    try:
        # Make request
        break
    except ConnectionError:
        time.sleep(backoff)
        backoff *= 2
```

---

## Model Resolution

### How the Proxy Selects a Provider

1. **Request arrives** with `model="kimi-k2.7-code"`
2. **Proxy resolves** via `ProviderRegistry.ResolveModel()` (3-level hint resolution, see [ARCHITECTURE.md](ARCHITECTURE.md))
3. **Candidate selection** via `ProviderRegistry.ResolveCandidates()`:
   - For a bare id like `kimi-k2.7-code`, return every provider that offers it, ordered by `(priority asc, provider order asc)`. Tie-breaks go to the earliest-discovered provider.
   - For a qualified id like `kimi-k2.7-code@moonshot`, return only that one candidate (no failover).
4. **Failover**: If the primary candidate fails (non-streaming only), try the next candidate in priority order
5. **Response**: Forward upgraded response in provider-neutral format

### 3-level `provider/model` hint resolution

`ProviderRegistry.ResolveModel()` handles the OpenAI-style `provider/model` form by trying three strategies in order:

1. **Verbatim** — full id exists in the registry (`nvidia/openai/gpt-oss-120b` → `openai/gpt-oss-120b`).
2. **Strip prefix** — strip the provider prefix and look up the bare name (`groq/qwen3-32b` → `qwen3-32b`).
3. **Suffix match within hinted provider** — find any upstream id owned by the hinted provider whose suffix equals the bare name (`nvidia/qwen3.5-397b-a17b` → NVIDIA's family-prefixed `qwen/qwen3.5-397b-a17b`). Must NOT cross providers.

The third level is necessary because NVIDIA exposes many upstream ids with a `family/` prefix that isn't part of the model the user typed.

### Supported Models by Provider

See [CONFIGURATION.md](CONFIGURATION.md) for the complete per-provider model roster and customization.

---

## Force-Mode Parameter Override

Some models have hard requirements that contradict what a client might send. The proxy handles this with the `override_client_params` flag on a model's `execution` block in `config/model-selection/{provider}.json`.

### When force-mode applies

The `override_client_params` field on `ModelExecutionConfig` accepts a boolean. When `true`, the proxy **overwrites** client-supplied `temperature` / `top_p` / `max_tokens` / `reasoning_effort` with the configured value (instead of only injecting defaults for missing fields). When `false` or absent (default), the proxy preserves client values and only injects for missing fields.

### Real-world case: Moonshot Kimi K2.x

The Kimi K2.7-code, K2.6, and K2.5 models reject any request where `temperature ≠ 1.0`. The proxy addresses this with two lines in `moonshot.json`:

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
{ "model": "kimi-k2.7-code", "temperature": 0.7, "messages": [...] }
```

The proxy rewrites the body before forwarding to Moonshot:
```json
{ "model": "kimi-k2.7-code", "temperature": 1.0, "max_tokens": 4096, "messages": [...] }
```

`RequestTransformer.ApplyExecutionDefaults()` is the function that performs this rewrite; the behaviour is exercised end-to-end by `OverrideClientParamsTests.cs` (10 tests covering both directions: force-mode overwrites, default-mode preserves).

### Other models that benefit from force-mode

Force-mode is currently enabled for:
- Moonshot `kimi-k2.7-code`, `kimi-k2.6` and `kimi-k2.5` (the canonical temperature=1.0 case)
- Ollama Cloud `kimi-k2.6` (inherits the moonshot rule — the Ollama Cloud variant of Kimi is just a passthrough to Moonshot's model)

For any other model, leave `override_client_params` absent (or `false`) so the client retains control.

---

## Reasoning Content Caching

DeepSeek reasoning models (v4-pro, v4-flash) return `reasoning_content` alongside regular output. The proxy caches this for multi-turn conversations.

### How It Works

1. **DeepSeek reasoning response** arrives with:
```json
{
  "choices": [{
    "message": {
      "thinking": "Let me think step by step...",
      "content": "The answer is..."
    }
  }]
}
```

2. **Proxy caches** the `thinking` content in `ReasoningCacheService`
3. **Next user message** in conversation is augmented with:
```json
{
  "role": "assistant",
  "content": "The answer is...",
  "_reasoning_context": "Let me think step by step..."
}
```

4. **Subsequent DeepSeek calls** include cached reasoning for context continuity

This enables true multi-turn reasoning without the cost of re-running reasoning on every turn.

---

## Authentication & Security

### API Key Management

- **Proxy does NOT validate incoming API keys** — it passes them through to upstream providers
- **Upstream API keys are set via environment variables**:
  - `PROVIDER_DEEPSEEK_API_KEY`
  - `PROVIDER_OPENAI_API_KEY`
  - `PROVIDER_NVIDIA_API_KEY`
  - `PROVIDER_OPENROUTER_API_KEY`
  - `PROVIDER_GROQ_API_KEY`
  - `PROVIDER_OLLAMACLOUD_API_KEY`
  - `PROVIDER_MOONSHOT_API_KEY`
  - `PROVIDER_CEREBRAS_API_KEY`

- **No authentication required** for clients connecting to the proxy (suitable for trusted networks only)

### Recommended Security Measures

- Run the proxy on a **private network or VPN**
- Use **network-level authentication** (e.g., mutual TLS, firewall rules)
- Monitor **provider rate limits** to detect abuse
- Rotate **upstream API keys regularly**

---

## Performance Characteristics

- **Latency**: < 10 ms overhead per request (pass-through streaming)
- **Throughput**: Up to 256 concurrent connections per provider
- **Memory**: ~50-100 MB baseline; scales with concurrent requests
- **Streaming**: Zero-copy pass-through; minimal buffering

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

---

## Related Documentation

- [CONFIGURATION.md](CONFIGURATION.md) — Provider setup and model defaults
- [ARCHITECTURE.md](ARCHITECTURE.md) — System design and components
- [TESTING.md](TESTING.md) — Test coverage and validation
- [DEPLOYMENT.md](DEPLOYMENT.md) — Docker and production deployment