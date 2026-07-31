# AI Proxy Hub

One local endpoint that speaks **both** the Ollama API and the OpenAI API, and routes every
request to whichever of **14 AI providers** actually serves the model you picked — hopping to
the next one when a provider throttles you or its free quota runs out.

Built for **GitHub Copilot inside Visual Studio 2026** (BYOM / "bring your own model", which
talks to a local Ollama), but any Ollama or OpenAI-compatible client works: Cursor,
Continue.dev, the `ollama` CLI, the OpenAI SDKs.

```
Visual Studio 2026 BYOM ──▶ /api/chat  ┐
Copilot / Cursor / SDKs ──▶ /v1/chat/… ┴──▶ AI Proxy Hub ──▶ DeepSeek · OpenAI · Google
                                                             NVIDIA NIM · Groq · OpenRouter
                                                             Moonshot · Cerebras · Z.AI
                                                             ZenMux · Ollama Cloud · Mistral
                                                             SiliconFlow · Cloudflare Workers AI
```

| | |
|---|---|
| **Framework** | .NET 10, ASP.NET Core minimal APIs (`CreateSlimBuilder`), zero NuGet dependencies |
| **Default port** | `11434` — the Ollama port, so clients need no reconfiguration |
| **Providers** | 14 |
| **Tests** | 551 passing, xUnit + `WebApplicationFactory`, no network required |
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
| Ops | `/health`, `/dashboard`, `/api/usage`, `/api/billing`, `/api/free-tier/summary`, `/api/resilience/cooldowns` | humans |

Both surfaces stream. `/api/chat` emits Ollama NDJSON (converting upstream OpenAI SSE on the
fly); `/v1/chat/completions` emits OpenAI SSE (converting upstream Ollama NDJSON on the fly).

## Failover and free-tier quotas

A bare model id is tried against every provider that serves it, in the configured priority
order, until one answers — on **all four** chat paths, streaming included. Nothing is written
to the client until an upstream succeeds, which is what makes retrying safe; once the first
byte is on the wire the response is committed to that provider.

The proxy also reads *why* a provider said no, because the HTTP status alone does not say:

- a bare `429` is a throttle worth a few seconds of backoff;
- a `429` whose body mentions a daily or monthly limit is an exhausted budget, and that
  provider is stood down until the budget actually resets;
- a `400`/`413`/`422` is a malformed request, so it is **not** retried against everyone else —
  unless the body reveals it was really a rate limit (Groq reports an over-TPM request as
  `413`);
- a provider that refuses the connection or never answers within its `timeout_seconds` is
  unreachable, so it is skipped **and** stood down, and the next request routes around it
  instead of paying the same wait again.

Providers that are cooling down move to the back of the queue, never off it, so a bad hour
across every free tier still produces an attempt rather than a dead end. A successful response
halves the recorded failure count, so a provider that recovers early stops being penalised.

`GET /api/free-tier/summary` reports each provider's published free allowance, how much of it
you have spent this month, and any active cooldown. Usage is written to
`data/usage-rollup.json`, so a *monthly* budget still means something after a restart.

> For a Copilot workload the binding limit is usually **requests per minute**, not the token
> pool. Mistral has the largest free pool of any provider (~1B tokens/month) behind roughly two
> requests per minute — a good fallback for chat, a poor primary for completions.

## Choosing a provider explicitly

`/api/tags` publishes every model as `<model>@<provider>:latest`, so picking
"GROQ - gpt-oss-120b" in Visual Studio pins the request to Groq even though NVIDIA and
Cerebras also serve a model by that name. A pinned request never fails over and is never
reordered around a cooldown: answering an explicit choice from somewhere else is worse than an
honest error.

## Dashboard

`http://localhost:11434/dashboard` shows usage, cost, latency and RPM per provider, plus the
free-tier budget, active cooldowns and recent failovers. It is a static page under `wwwroot/`
with Chart.js vendored locally, so it works with no internet.

With `PROXY_API_KEY` set, the page itself stays reachable — a browser cannot send a bearer
token when you type a URL — but its data endpoints do not. Open `/dashboard?key=YOUR_KEY` once
and the key is remembered. Set `PROXY_DASHBOARD_PUBLIC=false` to require the token for the page
as well.

## Configuration

Keys and settings come from, in order: system environment → `.env` → `appsettings.json` →
built-in defaults. `.env` is gitignored and never committed; only `.env.example` is tracked.

```bash
PROVIDER_DEEPSEEK_API_KEY=sk-…
PROVIDER_OPENAI_API_KEY=sk-…
PROXY_PORT=11434
PROXY_API_KEY=            # optional: require a bearer token to use the proxy itself
PROXY_DATA_DIR=           # optional: where usage-rollup.json lives (default ./data)
```

Which models each provider exposes — and the temperature, token budget, timeout and parameter
quirks for each — lives in `config/model-selection/{provider}.json`. Free-tier allowances and
terms-of-service verdicts live in `config/free-tier/catalog.json`. Editing either requires a
restart; there is no hot reload.

## Testing the providers

```powershell
./scripts/test-all-providers.ps1                       # every model, non-streaming
./scripts/test-all-providers.ps1 -Stream               # every model, streaming
./scripts/test-all-providers.ps1 -Provider groq,ollama -Stream
```

It reads `/api/tags`, sends a tiny prompt to each model, and checks that the answer came back
non-empty **and** from the provider the tag promised. Exit code is non-zero if any model fails.

A model appearing in `/v1/models` is not proof you are entitled to it — it can still answer
404, 402 or 410. New providers therefore ship with every model `"enabled": false`; run the
script and enable only what actually responds.

```bash
dotnet test           # 551 offline tests
```

## Documentation

`docs/ARCHITECTURE.md` · `docs/API.md` · `docs/CONFIGURATION.md` · `docs/TESTING.md` ·
`docs/DEPLOYMENT.md` · `docs/AGENTS.md` · `CHANGELOG.md`
