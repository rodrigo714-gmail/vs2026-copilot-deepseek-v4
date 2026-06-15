# Multi-Provider AI Proxy

> The fastest way to run DeepSeek, OpenAI, NVIDIA, Groq, OpenRouter, Moonshot/Kimi, Cerebras, and Ollama models in GitHub Copilot, VS BYOM, and Ollama clients — **curated for coding inside Visual Studio 2026**.

**As of June 2026** — Tested with Visual Studio 2026 Insider Edition · .NET 10 · 329 tests passing

A high-performance, ultra-low-overhead HTTP proxy that connects GitHub Copilot and Ollama clients to **DeepSeek, OpenAI, NVIDIA, Groq, OpenRouter, Moonshot/Kimi, Cerebras, and Ollama Cloud** APIs. Built with .NET 10 and ASP.NET Core minimal APIs for maximum throughput and minimal allocations.

| 🏗️ | Details |
|---|---|
| **Providers** | DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, Cerebras |
| **Models** | Auto-discovered from each provider; curated to **5 enabled per provider** for coding |
| **Default Port** | `11434` |
| **Framework** | .NET 10 |
| **Tests** | 329 passing ✅ |
| **Deploy** | Docker / bare metal |

## Key Features

- **🧠 Reasoning Content Caching** — Automatically captures DeepSeek's `reasoning_content` and re-injects it on subsequent messages for true multi-turn reasoning
- **🌐 Multi-Provider Support (8 providers)** — Route requests to DeepSeek, OpenAI, NVIDIA, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, or Cerebras based on model name
- **🔄 Dual API Compatibility**
  - **OpenAI-compatible** (`/v1/chat/completions`) — works with GitHub Copilot, Cursor, Continue.dev, any OpenAI SDK
  - **Ollama-compatible** (`/api/chat`, `/api/tags`, `/api/show`) — works with VS BYOM and Ollama clients
- **🛡️ Force-mode parameter override** — `override_client_params: true` in model JSON force-overwrites client values for models with hard requirements (e.g. Moonshot Kimi K2.x mandates `temperature=1.0`)
- **🎯 3-level `provider/model` hint resolution** — `nvidia/qwen3.5-397b-a17b` correctly resolves to NVIDIA's family-prefixed upstream id `qwen/qwen3.5-397b-a17b`
- **📋 Curated model roster** — Top 5 coding-optimised models per provider, hand-picked for GitHub Copilot in VS 2026
- **⚡ Ultra-Performance** — HTTP/2 connection pooling (256 connections/server), zero-copy streaming, minimal allocations
- **📦 Zero-Copy Streaming** — SSE pass-through without buffering
- **🔧 No External Dependencies** — Uses only built-in ASP.NET Core and System.Text.Json
- **🐳 Docker-Ready** — Multi-stage Dockerfile and docker-compose.yml included
- **🔐 Optional Authentication** — Set `PROXY_API_KEY` to require Bearer token on all endpoints

## Quick Start

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or Docker
- API keys for providers you want to use

### 1. Configure

Copy `.env.example` to `.env` and set your API keys:

```bash
cp .env.example .env
# Edit .env → set PROVIDER_DEEPSEEK_API_KEY=sk-your-key
```

Key environment variables:
```
PROVIDER_DEEPSEEK_API_KEY=sk-...
PROVIDER_OPENAI_API_KEY=sk-proj-...
PROVIDER_NVIDIA_API_KEY=nvapi-...
PROVIDER_GROQ_API_KEY=gsk-...
PROVIDER_OPENROUTER_API_KEY=sk-or-...
PROVIDER_OLLAMACLOUD_API_KEY=...
PROVIDER_MOONSHOT_API_KEY=sk-...
PROVIDER_CEREBRAS_API_KEY=csk-...

PROXY_PORT=11434                    # (optional)
DEFAULT_MODEL=deepseek-v4-pro       # (optional)
```

`.env` is git-ignored and never committed. Only `.env.example` is tracked.

### 2a. Run with Docker (Recommended)

```bash
docker compose up -d
```

### 2b. Run with .NET

```bash
dotnet run
```

You should see startup output listing the 8 providers (whichever have keys) and ~40 curated models.

## API Reference

### OpenAI-Compatible Endpoints

```
GET  /v1/models                          # List models (bare + 'upstream@provider' aliases)
POST /v1/chat/completions                # Chat (streaming or non-streaming)
GET  /health                             # Health check + provider summary
```

### Ollama-Compatible Endpoints

```
GET  /api/version                        # Version info
GET  /api/tags                           # List models (Ollama format)
GET  /api/show?model=...                 # Model details
POST /api/show                           # Model details
POST /api/chat                           # Chat (Ollama format; NDJSON streaming)
```

**[→ Full API Documentation](docs/API.md)**

## Configuration

### GitHub Copilot (VS Code)

In VS Code settings:

```json
{
  "github.copilot.advanced": {
    "debug.chatOverride": {
      "provider": "openai",
      "endpoint": "http://localhost:11434/v1/chat/completions",
      "model": "deepseek-v4-pro"
    }
  }
}
```

### VS 2026 BYOM (the proxy's primary use case)

Point the Ollama BYOM at:
```
http://localhost:11434/api/chat
```

Top picks for coding in VS 2026:
- `qwen3-coder:480b` (Ollama Cloud) — 1.5T Qwen coder, native tool support
- `qwen/qwen3-coder-480b-a35b-instruct` (NVIDIA NIM) — same model on NIM with 1M context
- `kimi-k2.6` (Moonshot) — 256K context, force-mode `temperature=1.0`
- `gpt-5` (OpenAI) — best general reasoning
- `gpt-oss-120b` (Cerebras / OpenAI / NVIDIA / Groq) — open-weights reasoning

### Continue.dev / Cursor

```json
{
  "models": [{
    "title": "DeepSeek V4 Pro",
    "provider": "openai",
    "model": "deepseek-v4-pro",
    "apiBase": "http://localhost:11434/v1"
  }]
}
```

## Documentation

- **[API.md](docs/API.md)** — Complete endpoint reference with examples
- **[CONFIGURATION.md](docs/CONFIGURATION.md)** — Setup, providers, parameter mapping, context windows
- **[ARCHITECTURE.md](docs/ARCHITECTURE.md)** — System design, components, request lifecycle
- **[TESTING.md](docs/TESTING.md)** — Test architecture, running tests, adding new tests
- **[DEPLOYMENT.md](docs/DEPLOYMENT.md)** — Docker, Kubernetes, monitoring, troubleshooting
- **[AGENTS.md](docs/AGENTS.md)** — Quick reference for AI assistants (Copilot, Claude, etc.)
- **[CLAUDE.md](CLAUDE.md)** — Claude Code session memory + hard constraints

## Performance

- **Connection pooling:** 256 per provider with HTTP/2 multiplexing
- **Streaming:** Zero-copy pass-through (minimal memory overhead)
- **Model metadata:** Loaded once on startup, cached in RAM
- **Typical latency:** <10ms proxy overhead
- **Test coverage:** 329 tests covering endpoints, parameters, model selection, transformations, force-mode, hint resolution

## Testing

```bash
# Run all tests
dotnet test

# Run specific suite
dotnet test --filter ClassName=EndpointTests
dotnet test --filter ClassName=OverrideClientParamsTests
dotnet test --filter ClassName=ProviderModelHintTests

# Verbose output
dotnet test --verbosity detailed
```

**[→ Testing Guide](docs/TESTING.md)**

## How Reasoning Caching Works

DeepSeek models return `reasoning_content` with their responses. The proxy:

1. Captures reasoning from each assistant response
2. Stores it in `ReasoningCacheService`
3. Re-injects cached reasoning into subsequent assistant messages in the same conversation
4. Enables coherent multi-turn reasoning without losing context

## How Force-Mode Override Works

Some models have hard requirements that contradict the user's request. The proxy handles this with the `override_client_params` flag in `config/model-selection/*.json`:

- **Default (`false` / absent):** the proxy only injects defaults for missing fields. Client-supplied values win.
- **Force mode (`true`):** the proxy **overwrites** client-supplied values for `temperature`, `top_p`, `max_tokens`, `reasoning_effort` with the configured value.

The canonical use case is Moonshot Kimi K2.5 / K2.6 which reject any `temperature ≠ 1.0`. With `override_client_params: true` and `temperature: 1.0` in `moonshot.json`, the proxy silently corrects the client's value before forwarding. See `OverrideClientParamsTests.cs` for the test suite.

## Provider Support

Each provider exposes a curated set of **5 enabled models** (max), prioritised for coding. The full per-provider roster is in `config/model-selection/*.json`.

| Provider | # enabled | Top picks | Notes |
|----------|----------:|-----------|-------|
| **DeepSeek** | 2 | deepseek-v4-pro, deepseek-v4-flash | 1M context, native reasoning |
| **OpenAI** | 5 | gpt-5, gpt-5-mini, gpt-4.1, gpt-4o, gpt-oss-120b | o-series support |
| **NVIDIA NIM** | 5 | qwen3-coder-480b, kimi-k2.6, nemotron-3-super, gpt-oss-120b, qwen3.5-397b | 1M context, all top coding picks |
| **Groq** | 5 | llama-3.3-70b-versatile, qwen3-32b, llama-4-scout, gpt-oss-120b, gpt-oss-20b | Speed-optimised inference |
| **OpenRouter** | 5 | qwen3-coder, nemotron-3-super, nemotron-3-ultra, kimi-k2.6, deepseek-v4-pro | Multi-backend passthrough |
| **Moonshot/Kimi** | 5 | kimi-k2.6, kimi-k2.5, moonshot-v1-{128k,auto,32k} | Kimi K2.x forces `temperature=1.0` |
| **Cerebras** | 2 | zai-glm-4.7, gpt-oss-120b | Small curated set |
| **Ollama Cloud** | 5 | qwen3-coder:480b, qwen3-coder-next, devstral-2:123b, kimi-k2.6, deepseek-v4-pro | Quantised open models |

**[→ Configuration Guide](docs/CONFIGURATION.md#context-window-specifications)**

## Architecture Overview

```
Clients (Copilot, VS BYOM, Ollama)
    ↓
Proxy (localhost:11434)
  ├─ Parameter filtering (RequestTransformer, with override_client_params force-mode)
  ├─ Model routing (ProviderRegistry; 3-level provider/model hint resolution)
  ├─ Reasoning caching (ReasoningCacheService)
  ├─ Format conversion (OpenAI ↔ Ollama)
  └─ Streaming handler (ChatStreamingService)
    ↓
Upstream Providers
  ├─ DeepSeek API
  ├─ OpenAI API
  ├─ NVIDIA NIM
  ├─ Groq API
  ├─ OpenRouter API
  ├─ Moonshot/Kimi API
  ├─ Cerebras API
  └─ Ollama Cloud API
```

**[→ Full Architecture](docs/ARCHITECTURE.md)**

## License

WTFPL (Do What The Fuck You Want To Public License)

## Support

For issues, questions, or contributions:
- Check **[AGENTS.md](docs/AGENTS.md)** for quick reference
- Review **[TESTING.md](docs/TESTING.md)** for test architecture
- See **[ARCHITECTURE.md](docs/ARCHITECTURE.md)** for design details
- Read **[CLAUDE.md](CLAUDE.md)** for the project-grounded Claude session rules
