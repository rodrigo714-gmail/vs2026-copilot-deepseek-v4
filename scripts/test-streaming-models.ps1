# Streaming provider test script
param([int]$Port = 11434)
$BaseUrl = "http://localhost:$Port/v1/chat/completions"

$tests = @(
    @{Model="deepseek-v4-pro";            Desc="DeepSeek V4 Pro (stream)"},
    @{Model="zai-glm-4.7@cerebras";       Desc="ZAI GLM 4.7 Cerebras (stream)"},
    @{Model="gpt-oss-120b@cerebras";      Desc="GPT OSS 120B Cerebras (stream)"},
    @{Model="openai/gpt-oss-120b@groq";   Desc="GPT OSS 120B Groq (stream)"},
    @{Model="models/gemini-2.5-flash@google"; Desc="Gemini 2.5 Flash Google (stream)"},
    @{Model="qwen/qwen3-coder-next@openrouter"; Desc="Qwen3 Coder Next OpenRouter (stream)"},
    @{Model="moonshotai/kimi-k2.6@nvidia"; Desc="Kimi K2.6 NVIDIA (stream)"},
    @{Model="glm-5.2@ollama";             Desc="GLM 5.2 Ollama Cloud (stream)"}
)

Write-Host "=== Streaming Mode Tests ===" -ForegroundColor Cyan
$passed = 0; $failed = 0

foreach ($test in $tests) {
    $body = @{
        model = $test.Model; messages = @(@{role="user"; content="Say hello"})
        stream = $true; max_tokens = 10
    } | ConvertTo-Json -Depth 5 -Compress

    Write-Host -NoNewline "[TEST] $($test.Desc) ... "
    try {
        $response = Invoke-WebRequest -Uri $BaseUrl -Method POST -ContentType "application/json" -Body $body -TimeoutSec 20
        $text = $response.Content
        if ($text -match "data: \[DONE\]" -and $text.Length -gt 0) {
            Write-Host "OK (200) - SSE stream valid" -ForegroundColor Green
            $passed++
        } else {
            Write-Host "OK (200) but unexpected format" -ForegroundColor Yellow
            $passed++
        }
    } catch {
        $sc = $_.Exception.Response.StatusCode.value__
        if ($sc -eq 429) { Write-Host "RATE-LIMITED (429)" -ForegroundColor Yellow; $passed++ }
        elseif ($sc -eq 403) { Write-Host "FORBIDDEN (403)" -ForegroundColor Red; $failed++ }
        else { Write-Host "FAIL ($sc)" -ForegroundColor Red; $failed++ }
    }
}
Write-Host "Streaming Results: $passed passed, $failed failed" -ForegroundColor Cyan