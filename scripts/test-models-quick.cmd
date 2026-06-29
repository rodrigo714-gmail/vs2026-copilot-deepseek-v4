@echo off
echo === Model Quick Test Suite ===
echo.

for %%M in (
  deepseek-v4-pro
  kimi-k2.6
  llama-3.3-70b-versatile
  "qwen/qwen3-coder"
  "openai/gpt-oss-120b"
  "deepseek/deepseek-v4-pro"
  "nvidia/nemotron-3-super-120b-a12b"
  moonshot-v1-128k
  "meta-llama/llama-4-scout-17b-16e-instruct"
  "qwen/qwen3.5-397b-a17b"
  "moonshotai/kimi-k2.6"
) do (
  echo === %%M ===
  echo {"model":"%%M","stream":false,"max_tokens":10,"messages":[{"role":"user","content":"OK"}]} > "%TEMP%\test_model.json"
  curl.exe -s -w "\n--- Http:%%{http_code} Time:%%{time_total}s\n" -X POST http://localhost:11434/v1/chat/completions -H "Content-Type: application/json" --data-binary "@%TEMP%\test_model.json" --max-time 60
  echo.
)

del "%TEMP%\test_model.json" 2>nul
echo === Done ===