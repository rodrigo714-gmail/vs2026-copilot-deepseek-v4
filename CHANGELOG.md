# Changelog

All notable changes to the Multi-Provider AI Proxy will be documented in this file.

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