# Changelog

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