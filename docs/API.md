# Multi-Provider AI Proxy - API Reference

Complete API documentation for the C# multi-provider proxy supporting DeepSeek, OpenAI, NVIDIA NIM, OpenRouter, Groq, Moonshot/Kimi, Cerebras, Ollama Cloud, and ZenMux.

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
- [Reasoning Content Caching](#reasoning-content-caching)
- [Force-Mode Parameter Override](#force-mode-parameter-override)

---

## Overview

The proxy provides two API interfaces:
1. **OpenAI-compatible** (`/v1/*`) — for GitHub Copilot, Cursor, Continue.dev, OpenAI SDKs
2. **Ollama-compatible** (`/api/*`) — for Visual Studio BYOM, native Ollama clients

Both interfaces route requests to the configured backend provider (DeepSeek, OpenAI, NVIDIA, OpenRouter, Groq, Moonshot/Kimi, Cerebras, Ollama Cloud, ZenMux) based on the requested model name.

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
    "gpt-5",
    "kimi2.7-code",
    "glm-5.2",
    "z-ai/glm-5.2-free",
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
| `X-Proxy-Candidate-Count` | Number of failover candidates (OpenAI endpoint only) | `1`, `3` |
| `X-Proxy-Primary-Provider` | Primary candidate provider (OpenAI endpoint only) | `nvidia` |
| `X-Proxy-Primary-Upstream` | Primary upstream model (OpenAI endpoint only) | `qwen/qwen3.5-397b-a17b` |

> Use these headers to verify that the expected provider is being selected. If the provider is unexpected, the model name may need a qualified alias (e.g. `model@provider:latest`).

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
      "id": "z-ai/glm-5.2-free",
      "object": "model",
      "created": 1700000000,
      "owned_by": "zenmux"
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
| `model` | string | Yes | Model ID (e.g., `deepseek-v4-pro`, `glm-5.2-free`, `kimi-k2.7-code-free`) |
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

**Supported providers:** DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, Cerebras, ZenMux. The proxy automatically filters unsupported parameters per provider.

**Diagnostic headers:** Every response includes `X-Proxy-Requested-Model`, `X-Proxy-Resolved-Model`, `X-Proxy-Provider`, `X-Proxy-Candidate-Count`, `X-Proxy-Primary-Provider`, `X-Proxy-Primary-Upstream`.

---

## Ollama-Compatible Endpoints

### GET /api/tags

List available models in Ollama format. The `model` field uses **qualified aliases** (`model@provider:latest`) to ensure requests route to the correct provider.

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

**Important:** The `model` field uses `model@provider:latest` format. When sending this model back via POST, the provider-qualified form ensures correct routing to the specific provider instead of falling back to the default provider.

### POST /api/chat

Chat completion endpoint (Ollama-compatible).

**Request Body:**
```json
{
  "model": "glm-5.2-free",
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

Vision-capable models (e.g. `kimi-k2.7-code-free`, `qwen3.7-plus`, `gemini-3.5-flash`) accept images as input. The proxy supports two formats:

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
   - Bare id like `glm-5.2-free`: returns every provider offering it, ordered by priority
   - Qualified id like `z-ai/glm-5.2-free@zenmux`: returns only that provider (no failover)
4. **Failover**: Non-streaming requests retry next candidate; streaming does not failover
5. **Response**: forwarded with diagnostic headers

### 3-level `provider/model` hint resolution

`ProviderRegistry.ResolveModel()` handles the OpenAI-style `provider/model` form:

1. **Verbatim** — full id exists in the registry
2. **Strip prefix** — strip the provider prefix and look up the bare name
3. **Suffix match within hinted provider** — find any upstream id owned by the hinted provider whose suffix equals the bare name

---

## Force-Mode Parameter Override

Some models have hard requirements. The proxy uses `override_client_params` for:
- Moonshot `kimi-k2.7-code`, `kimi-k2.6`, `kimi-k2.5` (mandates `temperature=1.0`)
- Ollama Cloud `kimi2.7-code`, `kimi-k2.6`
- ZenMux `kimi-k2.7-code-free` (mirrors the Kimi force-mode rule)

---

## Authentication & Security

Provider API keys are set via environment variables:
- `PROVIDER_DEEPSEEK_API_KEY`
- `PROVIDER_OPENAI_API_KEY`
- `PROVIDER_NVIDIA_API_KEY`
- `PROVIDER_OPENROUTER_API_KEY`
- `PROVIDER_GROQ_API_KEY`
- `PROVIDER_OLLAMACLOUD_API_KEY`
- `PROVIDER_MOONSHOT_API_KEY`
- `PROVIDER_CEREBRAS_API_KEY`
- `PROVIDER_ZENMUX_API_KEY`

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