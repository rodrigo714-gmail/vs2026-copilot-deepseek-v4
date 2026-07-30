# AI Proxy Hub

One local endpoint that speaks **both** the Ollama API and the OpenAI API, and routes every
request to whichever of **11 AI providers** actually serves the model you picked.

Built for **GitHub Copilot inside Visual Studio 2026** (BYOM / "bring your own model", which
talks to a local Ollama), but any Ollama or OpenAI-compatible client works: Cursor,
Continue.dev, the `ollama` CLI, the OpenAI SDKs.

```
Visual Studio 2026 BYOM ──▶ /api/chat  ┐
Copilot / Cursor / SDKs ──▶ /v1/chat/… ┴──▶ AI Proxy Hub ──▶ DeepSeek · OpenAI · Google
                                                             NVIDIA NIM · Groq · OpenRouter
                                                             Moonshot · Cerebras · Z.AI
                                                             ZenMux · Ollama Cloud
```

| | |
|---|---|
| **Framework** | .NET 10, ASP.NET Core minimal APIs (`CreateSlimBuilder`) |
| **Default port** | `11434` — the Ollama port, so clients need no reconfiguration |
| **Providers** | 11 |
| **Tests** | 370 passing, xUnit + `WebApplicationFactory`, no network required |
| **Deploy** | `dotnet run`, Docker, or docker-compose |

## Quick start

```bash
cp .env.example .env      # then add at least one PROVIDER_*_API_KEY
dotnet run
```

Point Visual Studio 2026 BYOM at `http://localhost:11434` and the curated model list appears
as `PROVIDER - model`.

> **Port conflict:** if the real Ollama daemon is installed it already owns `11434` and the
> proxy will not start. Either stop it (`Get-Process ollama | Stop-Process`) or run the proxy
> elsewhere with `PROXY_PORT=11500` in `.env`.

## Two API surfaces

| Surface | Routes | Used by |
|---|---|---|
| Ollama | `/api/version`, `/api/tags`, `/api/show`, `/api/chat` | VS 2026 BYOM, Ollama clients |
| OpenAI | `/v1/models`, `/v1/chat/completions` | Copilot, Cursor, Continue.dev, OpenAI SDKs |
| Ops | `/health`, `/dashboard`, `/api/usage`, `/api/billing` | humans |

Both surfaces stream. `/api/chat` emits Ollama NDJSON (converting upstream OpenAI SSE on the
fly); `/v1/chat/completions` emits OpenAI SSE (converting upstream Ollama NDJSON on the fly).

## Choosing a provider explicitly

`/api/tags` publishes every model as `<model>@<provider>:latest`, so picking
"GROQ - gpt-oss-120b" in Visual Studio pins the request to Groq even though NVIDIA and
Cerebras also serve a model by that name. A bare model id instead uses the configured
priority order and fails over to the next provider on error (non-streaming requests only —
once a stream's headers are out there is nothing left to fail over to).

## Configuration

Keys and settings come from, in order: system environment → `.env` → `appsettings.json` →
built-in defaults. `.env` is gitignored and never committed; only `.env.example` is tracked.

```bash
PROVIDER_DEEPSEEK_API_KEY=sk-…
PROVIDER_OPENAI_API_KEY=sk-…
PROXY_PORT=11434
PROXY_API_KEY=            # optional: require a bearer token to use the proxy itself
```

Which models each provider exposes — and the temperature, token budget, timeout and
parameter quirks for each — lives in `config/model-selection/{provider}.json`. Editing it
requires a restart; there is no hot reload.

## Testing the providers

```powershell
./scripts/test-all-providers.ps1                       # every model, non-streaming
./scripts/test-all-providers.ps1 -Stream               # every model, streaming
./scripts/test-all-providers.ps1 -Provider groq,ollama -Stream
```

It reads `/api/tags`, sends a tiny prompt to each model, and checks that the answer came back
non-empty **and** from the provider the tag promised. Exit code is non-zero if any model fails.

```bash
dotnet test           # 370 offline tests
```

## Documentation

`docs/ARCHITECTURE.md` · `docs/API.md` · `docs/CONFIGURATION.md` · `docs/TESTING.md` ·
`docs/DEPLOYMENT.md` · `docs/AGENTS.md` · `CHANGELOG.md`
