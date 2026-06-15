# Model Stress Test Report

**Generated:** 2026-06-11T20:31:05.5566232Z
**Total models:** 25

## Pass Summary

| Pass | Description | Runs/Model | Success | Avg Latency |
|------|-------------|------------|---------|-------------|
| 1 | latency | 2 | 23/25 | 1168ms |
| 2 | coding | 1 | 17/25 | 1633ms |
| 3 | copilot | 1 | 0/25 | 0ms |

## Provider Summary

| Provider | Models | P1 OK | P2 OK | P3 OK | Avg Latency |
|----------|--------|-------|-------|-------|-------------|
|  | 25 | 23/25 | 17/25 | 0/25 | 1168ms |

## Model Details (by latency)

| # | Model | Provider | P1 avg | P1 med | P1 ok | P2 ms | P2 | P3 ms | P3 | Chunks |
|---|-------|----------|--------|--------|-------|-------|----|-------|----|--------|
| 1 | `deepseek-v4-pro:latest` |  | 1460ms | 1497ms | 2/2 | 2230ms | ERR | -1ms | ERR | 0 |
| 2 | `kimi-k2.6:latest` |  | 3499ms | 3856ms | 2/2 | 3558ms | OK | -1ms | ERR | 0 |
| 3 | `llama-3.3-70b-versatile:latest` |  | 252ms | 252ms | 1/2 | 290ms | OK | -1ms | ERR | 0 |
| 4 | `qwen/qwen3-coder:latest` |  | 1312ms | 1789ms | 2/2 | 3204ms | OK | -1ms | ERR | 0 |
| 5 | `qwen/qwen3-coder-480b-a35b-instruct:latest` |  | 1162ms | 1167ms | 2/2 | 2216ms | ERR | -1ms | ERR | 0 |
| 6 | `qwen3-coder:480b:latest` |  | 1461ms | 1510ms | 2/2 | 2351ms | ERR | -1ms | ERR | 0 |
| 7 | `zai-glm-4.7:latest` |  | -1ms | -1ms | 0/2 | -1ms | ERR | -1ms | ERR | 0 |
| 8 | `deepseek-coder-6.7b-instruct:latest` |  | 1334ms | 1480ms | 2/2 | 2286ms | ERR | -1ms | ERR | 0 |
| 9 | `gpt-oss-120b:latest` |  | 270ms | 270ms | 2/2 | 430ms | OK | -1ms | ERR | 0 |
| 10 | `kimi-k2.5:latest` |  | 1936ms | 1984ms | 2/2 | 2999ms | OK | -1ms | ERR | 0 |
| 11 | `moonshotai/kimi-k2.6:latest` |  | 661ms | 900ms | 2/2 | 1708ms | ERR | -1ms | ERR | 0 |
| 12 | `nvidia/nemotron-3-super-120b-a12b:latest` |  | 996ms | 1112ms | 2/2 | 1552ms | OK | -1ms | ERR | 0 |
| 13 | `qwen/qwen3-32b:latest` |  | 552ms | 555ms | 2/2 | 585ms | OK | -1ms | ERR | 0 |
| 14 | `qwen3-coder-next:latest` |  | 687ms | 706ms | 2/2 | 1090ms | OK | -1ms | ERR | 0 |
| 15 | `devstral-2:123b:latest` |  | 1392ms | 1542ms | 2/2 | 2784ms | ERR | -1ms | ERR | 0 |
| 16 | `meta-llama/llama-4-scout-17b-16e-instruct:latest` |  | 422ms | 492ms | 2/2 | 626ms | OK | -1ms | ERR | 0 |
| 17 | `moonshot-v1-128k:latest` |  | 1720ms | 2675ms | 2/2 | 1069ms | OK | -1ms | ERR | 0 |
| 18 | `nvidia/nemotron-3-ultra-550b-a55b:latest` |  | 2174ms | 3300ms | 2/2 | 2748ms | OK | -1ms | ERR | 0 |
| 19 | `mistral:latest` |  | 1375ms | 1412ms | 2/2 | 2524ms | OK | -1ms | ERR | 0 |
| 20 | `moonshot-v1-auto:latest` |  | 1382ms | 1979ms | 2/2 | 2178ms | OK | -1ms | ERR | 0 |
| 21 | `openai/gpt-oss-120b:latest` |  | 564ms | 571ms | 2/2 | 881ms | OK | -1ms | ERR | 0 |
| 22 | `deepseek/deepseek-v4-pro:latest` |  | 996ms | 1203ms | 2/2 | 2878ms | ERR | -1ms | ERR | 0 |
| 23 | `moonshot-v1-32k:latest` |  | 670ms | 673ms | 2/2 | 982ms | OK | -1ms | ERR | 0 |
| 24 | `openai/gpt-oss-20b:latest` |  | -1ms | -1ms | 0/2 | 386ms | OK | -1ms | ERR | 0 |
| 25 | `qwen/qwen3.5-397b-a17b:latest` |  | 592ms | 784ms | 2/2 | 2664ms | OK | -1ms | ERR | 0 |

## Failures

- `deepseek-v4-pro:latest` (): P2, P3
- `kimi-k2.6:latest` (): P3
- `llama-3.3-70b-versatile:latest` (): P3
- `qwen/qwen3-coder:latest` (): P3
- `qwen/qwen3-coder-480b-a35b-instruct:latest` (): P2, P3
- `qwen3-coder:480b:latest` (): P2, P3
- `zai-glm-4.7:latest` (): P1, P2, P3
- `deepseek-coder-6.7b-instruct:latest` (): P2, P3
- `gpt-oss-120b:latest` (): P3
- `kimi-k2.5:latest` (): P3
- `moonshotai/kimi-k2.6:latest` (): P2, P3
- `nvidia/nemotron-3-super-120b-a12b:latest` (): P3
- `qwen/qwen3-32b:latest` (): P3
- `qwen3-coder-next:latest` (): P3
- `devstral-2:123b:latest` (): P2, P3
- `meta-llama/llama-4-scout-17b-16e-instruct:latest` (): P3
- `moonshot-v1-128k:latest` (): P3
- `nvidia/nemotron-3-ultra-550b-a55b:latest` (): P3
- `mistral:latest` (): P3
- `moonshot-v1-auto:latest` (): P3
- `openai/gpt-oss-120b:latest` (): P3
- `deepseek/deepseek-v4-pro:latest` (): P2, P3
- `moonshot-v1-32k:latest` (): P3
- `openai/gpt-oss-20b:latest` (): P1, P3
- `qwen/qwen3.5-397b-a17b:latest` (): P3
