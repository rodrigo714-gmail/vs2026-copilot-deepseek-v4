# Test all ZenMux models through the proxy
param([int]$Port = 11434)
$BaseUrl = "http://localhost:$Port/v1/chat/completions"

$models = @(
    "z-ai/glm-5.2@zenmux",
    "qwen/qwen3-coder:free@zenmux",
    "qwen/qwen3-coder@zenmux",
    "qwen/qwen3-coder-plus@zenmux",
    "qwen/qwen3.7-plus@zenmux",
    "qwen/qwen3.7-max@zenmux",
    "x-ai/grok-4.3@zenmux",
    "anthropic/claude-fable-5@zenmux",
    "anthropic/claude-opus-4.8@zenmux",
    "minimax/minimax-m3@zenmux",
    "openai/gpt-5.5@zenmux",
    "openai/gpt-5.5-pro@zenmux",
    "google/gemini-3.5-flash@zenmux",
    "deepseek/deepseek-v4-pro@zenmux",
    "deepseek/deepseek-v4-flash@zenmux",
    "moonshotai/kimi-k2.7-code@zenmux",
    "moonshotai/kimi-k2.6@zenmux",
    "x-ai/grok-build-0.1@zenmux",
    "moonshotai/kimi-k2.7-code-highspeed@zenmux",
    "qwen/qwen3.6-plus@zenmux",
    "meta-llama/llama-4-scout-17b-16e-instruct@zenmux"
)

Write-Host "=== ZenMux Model Test ===" -ForegroundColor Cyan
$ok = 0; $fail = 0; $rate = 0

foreach ($model in $models) {
    $body = @{
        model = $model
        messages = @(@{role="user"; content="hi"})
        stream = $false
        max_tokens = 5
    } | ConvertTo-Json -Depth 5 -Compress

    Write-Host -NoNewline "[$model] ... "
    try {
        $result = Invoke-RestMethod -Uri $BaseUrl -Method POST -ContentType "application/json" -Body $body -TimeoutSec 25
        $text = $result.choices[0].message.content
        Write-Host "OK -> '$text'" -ForegroundColor Green
        $ok++
    }
    catch {
        $sc = $_.Exception.Response.StatusCode.value__
        try {
            $rs = $_.Exception.Response.GetResponseStream()
            $rd = New-Object System.IO.StreamReader($rs)
            $err = $rd.ReadToEnd()
            $rd.Close()
            if ($err.Length -gt 100) { $err = $err.Substring(0,100) + "..." }
        } catch { $err = "(no body)" }

        if ($sc -eq 429) {
            Write-Host "RATE-LIMITED (429)" -ForegroundColor Yellow
            $rate++
        } elseif ($sc -eq 402) {
            Write-Host "NO CREDIT (402): $err" -ForegroundColor DarkYellow
            $fail++
        } elseif ($sc -eq 403) {
            Write-Host "FORBIDDEN (403): $err" -ForegroundColor Red
            $fail++
        } elseif ($sc -eq 400) {
            Write-Host "BAD REQUEST (400): $err" -ForegroundColor Red
            $fail++
        } else {
            Write-Host "FAIL ($sc): $err" -ForegroundColor Red
            $fail++
        }
    }
}

Write-Host ""
Write-Host "=== Results: $ok OK, $rate rate-limited, $fail failed ===" -ForegroundColor Cyan