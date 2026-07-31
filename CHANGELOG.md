# Changelog

All notable changes to AI Proxy Hub (formerly "Multi-Provider AI Proxy") will be documented in
this file.

## 2026-07-31 (3) — VS 2026 agent mode actually works: reasoning fallback, tool-call conversion, tools stripping

Found by driving every published model through the three paths VS 2026 uses — plain chat,
BYOM streaming, and agent mode with tools — rather than plain chat alone.

### Fixed
- **Reasoning-only answers arrived blank from Cerebras, Groq and OpenRouter.** Those providers
  name the field `reasoning`, not `reasoning_content`, so the empty-content fallback never fired
  and a model that spent its whole token budget thinking looked silent. Both `/api/chat` paths
  (streaming and not) now accept either field name (`ReasoningFallbackTests.cs`).
- **Every Ollama Cloud model was text-only in agent mode.** The `/v1` ↔ Ollama-native conversion
  dropped `tool_calls` on the streaming path and forwarded them in Ollama's shape (object
  arguments, no id) on the non-streaming one — either way the OpenAI SDK in VS saw no usable
  tool call. Tool calls now convert in both directions: generated `id` + `type=function` +
  string `arguments` + `finish_reason: "tool_calls"` on the way down, and agent history
  (`tool_calls`, `tool_call_id`) rewritten to Ollama format on the way up.
- **Every successful non-streaming `/v1` call to an Ollama upstream aborted the connection.**
  The handler wrote the response body first and the caller stamped `X-Proxy-*` headers after —
  "Headers are read-only, response has already started", and Kestrel dropped the connection.
  Headers now go out before the first body byte.
- **Groq's `compound`/`compound-mini` answered HTTP 400 to any agent request.** They run Groq's
  own server-side tools and reject a client tools payload. `execution.supports_tools: false` now
  makes `RequestTransformer` strip `tools`/`tool_choice` per model.
- **OpenAI `gpt-5.5` answered 400 in agent mode**: "Function tools with reasoning_effort are not
  supported for gpt-5.5". The configured `reasoning_effort` default was removed for the 5.5
  family; it reasons at its default effort instead.

### Changed
- **ZenMux roster disabled entirely** — every model answered HTTP 402 `reject_no_credit` on
  2026-07-31. The entries stay in `zenmux.json` with a comment; top up and re-enable.

### Notes
- Live e2e status on the fixed build (61 published models): every reachable provider passes all
  three paths. Remaining failures are external — Google free-tier daily quota (429, recovers at
  midnight PT), Z.AI account balance (only `glm-4.7-flash` is free), two NVIDIA models queueing
  past their timeout, and intermittent local DNS on the dev machine.
- 551 → 557 tests.

## 2026-07-31 (2) — Fix Groq's unusable roster, .env precedence, and failover on dead providers

Three problems found by running the proxy against real provider keys rather than stubs.

### Fixed
- **Every Groq request to three models failed before reaching the model.** Groq charges
  `prompt + max_tokens` against its per-minute token budget, and `qwen/qwen3.6-27b`,
  `openai/gpt-oss-120b` and `openai/gpt-oss-20b` shipped with `max_tokens: 8192` against a
  measured 8000 TPM limit — so even a two-word prompt was rejected with HTTP 413. Two more models
  were fragile for a coding workload. Limits were read from `x-ratelimit-limit-tokens` per model
  and `max_tokens` now leaves roughly half the budget for the prompt; the reasoning and the
  re-measurement command are recorded in `groq.json`.
- **`.env` overrode real environment variables**, inverting the documented precedence. A `.env`
  baked into an image silently beat `docker run -e`, compose `environment:` and Kubernetes env
  vars, so a container ignored the configuration it was handed. The file now fills in only what is
  missing, and logs which keys it skipped — an ignored line in `.env` is the kind of thing that
  costs an afternoon. Extracted to `Infrastructure/DotEnvLoader.cs` so the rule is testable.
- **A provider that never answered took the whole request down.** A refused connection, a DNS or
  TLS failure, or nothing back within `timeout_seconds` threw straight past the candidate loop into
  `UpstreamErrorMiddleware`, which answered 502/504 with every other provider untried. This was
  visible with NVIDIA, whose free tier queues some models past their timeout while others answer in
  three seconds. Transport failures are now the new `UpstreamFailureKind.Unreachable`: the request
  fails over, and the provider is stood down on the **first** occurrence — unlike a transient blip,
  a hang costs a full timeout every time it is tried, so the next request routes around it.
- **The `@provider` suffix was ignored when the model id also looked like `provider/model`.**
  `openai/gpt-oss-120b@groq` is a real id — Groq serves a model whose upstream name starts with
  `openai/` — but on the `/v1` surface the prefix pinned it to OpenAI, which rejected it as an
  invalid model. The explicit suffix now wins, matching what `ResolveModel` already did.

### Notes
- NVIDIA's variability is upstream queueing on its free tier, not a proxy fault. Measured on
  2026-07-31: `deepseek-ai/deepseek-v4-pro` 3.5 s, `nvidia/llama-3.3-nemotron-super-49b-v1.5` 44 s,
  `meta/llama-3.3-70b-instruct` and `openai/gpt-oss-120b` still unanswered past 45 s and 90 s.
  The cooldown above is what keeps that out of the IDE's way.
- 533 → 551 tests.

## 2026-07-31 — Quota-aware failover, free-tier budgets, three new providers

Borrowed the mechanics worth having from the [OmniRoute](https://github.com/diegosouzapw/OmniRoute)
gateway: splitting a transient 429 from an exhausted budget, cooldowns whose length matches the
failure, and honest free-tier accounting (shared pools counted once, rate-limit-only tiers listed
but never summed).

### Fixed
- **`/api/chat` had no failover at all.** The Visual Studio 2026 BYOM path called
  `ResolveProvider()` and committed to a single provider, so an exhausted quota or a dead key
  surfaced to the IDE as a hard failure with ten other providers sitting idle. It now walks the
  candidate list, streaming and non-streaming alike.
- **Streaming could never fail over.** `HandleOllamaCloudChatCompletion` flushed the response body
  *before* issuing the upstream request — sync-over-async on a request thread, and it committed the
  response to a provider that had not answered yet. Moved below the success check; streaming
  failover turned out to be a reordering, not a rewrite.
- **A 400 burned every candidate.** Retry was triggered by any non-2xx, so a malformed request was
  re-sent to all eleven providers and returned the same error several seconds later.
- **Groq's over-TPM 413 was read as a malformed request.** Groq answers an over-quota request with
  `413 Payload Too Large` and `"code":"rate_limit_exceeded"`; taken literally the router gave up.
  A 4xx carrying rate-limit wording is now classified as a rate limit and fails over. Found by
  running the proxy against real provider keys, not by a test.
- **`TryHandleOllamaCloudChatCompletion` discarded the upstream status and body.** When an
  Ollama-format provider was the only candidate the client received
  `502 {"error":"no provider candidate available"}` — actively misleading, since a candidate did
  exist and did answer. Every path now reports the last real upstream status and body.
- **Z.AI requests were costed at $0.** `PricingCalculator._providerDefaults` was a second, parallel
  price table that never knew about `zai`, so the dashboard under-reported spend. Folded into
  `PricingCatalog`; a test now fails if any registered provider lacks a price fallback.
- **Two copies of `FormatDisplayName` had drifted apart** (neither knew `zai`), and
  `ProviderBillingService` returned `null` for any provider missing from its switch. All three
  per-provider switches now read from `ProviderCapabilitiesRegistry`.
- **`ProviderRegistryTests` was not isolated.** It hand-rolled a four-key env snapshot instead of
  using `ProviderEnvScope`, so any other provider key in the developer's `.env` silently changed
  cross-provider collision resolution underneath its assertions.
- **Docker lost all usage on every recreation.** The image now creates `/app/data` for the non-root
  user and declares a volume; `docker-compose.yml` mounts one.

### Added
- **Quota-aware failover.** `Infrastructure/UpstreamFailureClassifier.cs` decides what a failure
  *means*: a bare 429 is a throttle worth seconds, a 429 whose body names a daily or monthly limit
  is an exhausted budget. A bare 429 is never promoted — standing a healthy provider down until
  midnight over a one-minute blip is worse than one retry.
- **Provider cooldowns.** `Services/ProviderHealthService.cs` stands a provider down for a length
  that matches the failure (until local midnight for a daily quota, until the 1st for a monthly
  one, 6 h for spent credits, exponential seconds for a rate limit, 15 min for bad credentials). An
  upstream `Retry-After` always wins. A success **halves** the failure count, so a provider that
  recovers early stops being penalised. Ordering demotes cooling providers but never returns an
  empty candidate list.
- **Free-tier budget catalog** — `config/free-tier/catalog.json`: published allowance, RPM/RPD
  limits, signup URL and a terms-of-service verdict per provider. Data, not C#, so a figure can be
  re-verified without a rebuild.
- **Persistent usage** — `data/usage-rollup.json`, a per-day per-provider aggregate flushed every
  60 s and on shutdown, so a *monthly* quota still means something after a restart. Atomic writes,
  400-day retention; an unwritable directory degrades to memory-only rather than failing startup.
- **New endpoints:** `GET /api/free-tier/summary`, `GET /api/resilience/cooldowns`,
  `POST /api/resilience/reset`.
- **Three free-tier providers:** Mistral, SiliconFlow and Cloudflare Workers AI. All models ship
  `"enabled": false` — a model listed by `/v1/models` is not proof of entitlement.
- **New response headers:** `X-Proxy-Candidate-Index` and `X-Proxy-Attempts`. `X-Proxy-Provider`
  now names the provider that *served* the request rather than the one tried first.
- **163 new tests** (370 → 533), including a scriptable two-provider stub
  (`FakeProviders/ScriptedProviderStub.cs`) that made failover testable at all — `ProxyFixture`
  boots one stub that always succeeds and cannot express these scenarios.

### Changed
- Dashboard markup moved from a ~770-line C# verbatim string to `wwwroot/dashboard.html`, with
  Chart.js vendored locally — it used to load from a CDN, so the page broke with no internet.
  New panels: free-tier budget, active cooldowns, recent failovers.
- With `PROXY_API_KEY` set, `/dashboard`, `/vendor/` and `/health` are reachable without a token
  (a browser cannot attach a bearer token to a navigation); the data endpoints are not. Disable
  with `PROXY_DASHBOARD_PUBLIC=false`.
- `ResolveCandidates` stays pure; health-aware routing lives in the new `ResolveRoutePlan`, because
  `ProviderBenchmarkService` depends on being able to probe cooling providers.
- Still **zero NuGet dependencies**. The rollup is a JSON file rather than SQLite: a few thousand
  rows with a single writer does not justify the project's first native dependency.

### Known issues
- **`groq.json` sets `max_tokens: 8192` while the Groq free tier allows 8000 TPM**, so every
  request to `openai/gpt-oss-120b` on that tier fails with a 413 before the model is even reached.
  Lower it to ~6000 if you are on the free tier.

## 2026-07-30 — Rename to AI Proxy Hub, fix Ollama streaming, verify all 11 providers live

### Fixed
- **`/api/chat` streaming was silent for 10 of 11 providers.** It advertised
  `application/x-ndjson` but forwarded the upstream OpenAI SSE verbatim; an Ollama client
  (Visual Studio 2026 BYOM included) discards `data:` frames outright, so every OpenAI-format
  provider looked like it answered nothing over streaming. `ChatStreamingService.StreamOllamaAndCache`
  now converts each SSE chunk into a proper Ollama NDJSON line, including tool-call reassembly
  and a `reasoning_content` fallback for reasoning-only responses.
- **Ollama Cloud streaming used the wrong upstream path** (`"api/chat"` / `"v1/chat/completions"`
  hardcoded instead of `ollamaProvider.Capabilities.ChatPath`), breaking any provider whose chat
  path differs from the default.
- **Tag stripping truncated Ollama Cloud ids at the first colon.** `StripTagSuffix` treated
  `qwen3-coder:480b@ollama:latest` as tag `480b@ollama:latest`, mis-routing every model whose
  upstream id embeds a colon. It now strips only the trailing `:latest` added by `/api/tags`.
- **OpenAI GPT-5.x / o-series returned HTTP 400** (`Unsupported parameter: 'max_tokens'`) and
  reasoning-model calls with an explicit temperature also failed. Added
  `uses_max_completion_tokens` and `supports_temperature` execution flags; `gpt-5.5-pro` (Responses
  API only, 404 on `/v1/chat/completions`) is disabled for Ollama/BYOM clients.
- **`gpt-5.4-mini` inherited `gpt-5.4`'s context window.** `ModelSelectionStore` matched by
  priority order, so the shorter substring `"gpt-5.4"` (priority 3) shadowed the more specific
  `"gpt-5.4-mini"` (priority 5). Matching is now longest-substring-first, priority breaks ties only.
- **Duplicate `"provider": "ollama"` declarations** across `ollama.json` and `ollamacloud.json`
  fought over the same match strings, non-deterministically disabling enabled models depending on
  file enumeration order. Merged into a single `ollama.json`; on a genuine conflict the enabled
  entry now always wins.
- **A provider hint the named provider can't satisfy resolved across providers** (e.g. an
  "OLLAMA - x" pick silently answered by NVIDIA). Falls back to the default model instead.
- **An upstream 200 with an unparseable body threw an unhandled exception**, surfacing as an
  opaque, empty HTTP 500. Both `/api/chat` and a new `UpstreamErrorMiddleware` now return a JSON
  error naming the provider, model and (for transport failures) 502/504 with the underlying cause.
- Removed models confirmed dead against live provider catalogs: `meta-llama/llama-4-scout-17b`
  and `qwen/qwen3-32b` (Groq, HTTP 404), `qwen/qwen3.5-397b-a17b` and
  `qwen/qwen3-coder-480b-a35b-instruct` (NVIDIA, HTTP 410/gone), `moonshotai/kimi-k2.6` (NVIDIA,
  not entitled on this key), `kimi-k3` (Ollama Cloud, requires a paid add-on plan).
- Duplicate `app.MapDashboardEndpoints()` call in `Program.cs`.

### Changed
- Project renamed **AI Proxy Hub** end to end: startup banner, `docker-compose.yml` service/image
  name, `/health` response, root `README.md` (new), `docs/README.md`, CI workflow .NET version
  (8.0.x → 10.0.x, was mismatched with the net10.0 target).
- `config/model-selection/nvidia.json` and `groq.json` rosters re-verified against each
  provider's live `/v1/models` and re-curated around what's actually reachable.
- `scripts/test-all-providers.ps1` (new): drives `/api/tags` → `/api/chat` exactly as VS 2026
  BYOM does, for every published model, streaming or not, and reports per-provider pass/fail.
- Test suite: 8 pre-existing failures fixed, all stale model references updated to the verified
  roster, env-var isolation between tests consolidated into `ProviderEnvScope` (was three
  hand-copied, drifting lists). 370/370 passing, fully offline.

## 2026-06-11 — Add Qwen 3.7 Plus (OpenRouter), restore provider prefix in /api/tags

### Added
- **Qwen 3.7 Plus** (`qwen/qwen3.7-plus`) on OpenRouter: 1M context, vision + tools support, priority 6
- **Provider prefix in /api/tags**: models now display as `PROVEEDOR - modelo` (e.g. `OPENROUTER - qwen3.7-plus:latest`) for better BYOM discoverability in VS 2026

### Changed
- **config/model-selection/openrouter.json**: added qwen3.7-plus entry with `context_length: 1000000`, `max_output_tokens: 65536`, `supports_vision: true`
- **Endpoints/OllamaEndpoints.cs**: restored provider prefix + deduplication logic that was lost in a merge conflict
- **Tests**: updated OpenRouter enabled model count (5→6)

### Fixed
- **Merge conflict resolution**: the `/api/tags` endpoint was reverted to the pre-prefix version after merging `feature/model-list-provider-prefix-order` into `develop`. Provider prefixes and deduplication logic are now restored.

## 2026-06-11 — Provider optimization, dedup, and comprehensive stress testing

### Added
- **Stress test script** (`scripts/stress-test-all-models.ps1`): 3-pass comprehensive model testing (latency, coding agent, Copilot payload simulation) across all 25 models
- **Provider connectivity verification** (`scripts/verify-all-providers.ps1`): validates catalog and chat for all 8 configured providers, generates markdown + JSON reports
- **Duplicate model benchmark** (`scripts/benchmark-duplicates-latency.ps1`): 3-run latency comparison for duplicate models across providers
- **13 new unit tests** in `ProviderRegistryAdvancedTests.cs`, `OllamaEndpointConversionsTests.cs`, `OpenAiEndpointHelpersTests.cs`, `ChatStreamingServiceTests.cs`
- **Changelog** (`CHANGELOG.md`)

### Changed
- **Test coverage**: 408 → 421 tests (1.28% overall increase, ProviderRegistry line coverage 72% → 87%)
- **ProviderRegistry**: improved `ResolveModel` with display provider hint extraction, upstream suffix matching, empty registry edge cases
- **.env.example**: normalized with all 8 providers (deepseek, openai, nvidia, openrouter, groq, ollama, moonshot, cerebras)
- **.env**: added `PROVIDER_DEEPSEEK_API_KEY` alongside legacy `DEEPSEEK_API_KEY`, structured with section headers
- **verify-all-providers.ps1**: fixed PowerShell parser issues (removed backtick-escaped strings in double quotes, renamed `Extract-*` → `Get-*FromResponse` functions, used `$statusIcon` hashtable)

### Configuration (model-selection)
- **Enabled**: `deepseek-v4-flash` (was disabled), `deepseek-v4-pro` (deepseek)
- **Disabled (dedup)**: slower providers for duplicate models based on 3-run latency benchmarks:
  - `nvidia/nemotron-3-super-120b-a12b` on nvidia (openrouter faster: 6071ms vs 8473ms)
  - `moonshotai/kimi-k2.6` on nvidia (openrouter faster: 2263ms vs 3304ms)
  - `openai/gpt-oss-120b` on groq (FAIL 413 vs nvidia 507ms)
  - `kimi-k2.6` on ollama (moonshot faster: 4942ms vs 5945ms)

### Stress test results (2026-06-11)
- **25 models tested** across 7 active providers
- **P1 (latency)**: 23/25 (92%) success, avg 1168ms
- **P2 (coding agent)**: 17/25 (68%) success, avg 1633ms (reasoning models return content in `thinking`/`reasoning_content`)
- **P3 (Copilot payload)**: scripting artifact (Invoke-WebRequest SSE limitation); verified working via curl
- **Fastest**: `gpt-oss-120b` via cerebras (270ms), `llama-3.3-70b` via groq (252ms)
- **Slowest**: `kimi-k2.6` via moonshot (3499ms), `nemotron-3-ultra` via openrouter (2174ms)
- **3 models with transient errors**: `zai-glm-4.7` (rate limited), `openai/gpt-oss-20b` (413), `llama-3.3-70b` (intermittent)
- Reports: `docs/testing/logs/stress-test-*.{json,md}`

### Fixed
- PowerShell script `$statusIcon` unused variable warning
- PowerShell string interpolation parser errors throughout `verify-all-providers.ps1`
- `endpoint` parameter format in `benchmark-duplicates-latency.ps1` to match current proxy `/api/tags` format
- Test isolation: `ProviderRegistryAdvancedTests` empty-registry tests use `SaveAndClearAllProviderEnvVars()` to avoid cross-test pollution

## [2026.06.11] — Reasoning Effort Optimization & Configuration Consolidation

### Fixed
- **Resolved VS Copilot 100s timeout with `deepseek-v4-pro`**: Reduced `reasoning_effort` from `high` to `medium` to keep the silent chain-of-thought phase under the VS client's 100-second timeout limit. Previously, `high` caused 100+ seconds of silent reasoning before any SSE token was emitted, triggering `TaskCanceledException` / `SocketException.OperationAborted` retry loops.
- `deepseek-coder-6.7b-instruct`: `reasoning_effort` changed from `low` to `medium` for consistency with other reasoning models.

### Changed
- **DeepSeek API `reasoning_effort` semantics documented**: According to [DeepSeek API docs](https://api.deepseek.com/), only `high` and `max` are real values. `low` and `medium` are internally mapped to `high`. All DeepSeek models now use `medium` as the canonical label.
- All other providers (NVIDIA, Groq, OpenRouter, Ollama, Moonshot, Cerebras) correctly have no `reasoning_effort` entries — the proxy already filters this parameter for unsupported providers via `RequestTransformer.ApplyExecutionDefaults()`.

### Configuration
- **Unified Ollama config**: Merged `ollama.json` + `ollamacloud.json` → single `ollama.json`. The former `ollamacloud.json` was renamed and the local-only stub was deleted. Migrated the `mistral` model entry with proper priorities and complete execution parameters.
- **OpenRouter `deepseek/deepseek-v4-pro`**: Removed `reasoning_effort` (dead config — OpenRouter does not forward this parameter, `SupportsReasoningEffort = false`).
- **OpenAI `gpt-5` and `gpt-5-mini`**: Restored `reasoning_effort: medium` (GPT-5 series are native reasoning models).

### Added
- `scripts/test-all-models-copilot.ps1` — Comprehensive test suite simulating GitHub Copilot streaming requests on all models, measuring time-to-first-byte (TTFB) and total latency.
- `scripts/test-models-quick.cmd` — Quick non-streaming connectivity test across representative models from all providers.

### Test Results (Live API)
| Model | Provider | HTTP | Latency | reasoning_content |
|--------|----------|------|----------|:---:|
| `deepseek-v4-pro` | DeepSeek | 200 | **1.25s** | ✅ |
| `kimi-k2.6` | Moonshot | 200 | **9.95s** | ✅ |
| `llama-3.3-70b-versatile` | Groq | 200 | **0.57s** | — |
| `moonshot-v1-128k` | Moonshot | 200 | **0.98s** | — |
| `deepseek-v4-pro` (streaming) | DeepSeek | 200 | TTFB immediate | ✅ |

### Files Changed
- `config/model-selection/deepseek.json`
- `config/model-selection/openai.json`
- `config/model-selection/openrouter.json`
- `config/model-selection/ollama.json` (renamed from `ollamacloud.json`)
- `config/model-selection/ollamacloud.json` (deleted)
- `docs/testing/model-copilot-test-results.json` (new)
- `scripts/test-all-models-copilot.ps1` (new)
- `scripts/test-models-quick.cmd` (new)