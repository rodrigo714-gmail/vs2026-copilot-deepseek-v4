# Comprehensive provider test script
# Tests every provider with at least one non-streaming model
param(
    [int]$Port = 11434
)

$BaseUrl = "http://localhost:$Port/v1/chat/completions"

$tests = @(
    @{Model="deepseek-v4-pro";            Provider="deepseek";   Desc="DeepSeek V4 Pro (non-stream)"},
    @{Model="deepseek-v4-flash";          Provider="deepseek";   Desc="DeepSeek V4 Flash (non-stream)"},
    @{Model="kimi-k2.7-code@moonshot";    Provider="moonshot";   Desc="Kimi K2.7 Code (non-stream)"},
    @{Model="moonshot-v1-auto@moonshot";  Provider="moonshot";   Desc="Moonshot V1 Auto (non-stream)"},
    @{Model="zai-glm-4.7@cerebras";       Provider="cerebras";   Desc="ZAI GLM 4.7 Cerebras (non-stream)"},
    @{Model="gpt-oss-120b@cerebras";      Provider="cerebras";   Desc="GPT OSS 120B Cerebras (non-stream)"},
    @{Model="llama-3.3-70b-versatile@groq"; Provider="groq";     Desc="Llama 3.3 70B Groq (non-stream)"},
    @{Model="openai/gpt-oss-120b@groq";   Provider="groq";       Desc="GPT OSS 120B Groq (non-stream)"},
    @{Model="moonshotai/kimi-k2.6@nvidia"; Provider="nvidia";    Desc="Kimi K2.6 NVIDIA (non-stream)"},
    @{Model="nvidia/nemotron-3-super-120b-a12b@nvidia"; Provider="nvidia"; Desc="Nemotron NVIDIA (non-stream)"},
    @{Model="qwen/qwen3-coder-next@openrouter"; Provider="openrouter"; Desc="Qwen3 Coder Next OpenRouter (non-stream)"},
    @{Model="models/gemini-2.5-flash@google"; Provider="google"; Desc="Gemini 2.5 Flash Google (non-stream)"},
    @{Model="qwen3.6-plus@zenmux";        Provider="zenmux";     Desc="Qwen3.6 Plus ZenMux (non-stream)"},
    @{Model="glm-5.2@ollama";             Provider="ollama";     Desc="GLM 5.2 Ollama Cloud (non-stream)"}
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "   Multi-Provider Test - Port $Port" -ForegroundColor Cyan
Write-Host "   Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$passed = 0
$failed = 0
$errored = 0

foreach ($test in $tests) {
    $body = @{
        model = $test.Model
        messages = @(
            @{role="user"; content="Say hello in exactly one word"}
        )
        stream = $false
        max_tokens = 10
    } | ConvertTo-Json -Depth 5 -Compress

    Write-Host -NoNewline "[TEST] $($test.Desc) ... "
    try {
        $result = Invoke-RestMethod -Uri $BaseUrl -Method POST -ContentType "application/json" -Body $body -TimeoutSec 30
        $content = $result.choices[0].message.content
        Write-Host "OK (200) -> '$content'" -ForegroundColor Green
        $passed++
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode) {
            try {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $errBody = $reader.ReadToEnd()
                $reader.Close()
                # Truncate
                if ($errBody.Length -gt 200) { $errBody = $errBody.Substring(0, 200) + "..." }
                
                if ($statusCode -eq 429) {
                    Write-Host "RATE-LIMITED (429)" -ForegroundColor Yellow
                    $passed++
                }
                elseif ($statusCode -eq 403) {
                    Write-Host "FORBIDDEN (403): $errBody" -ForegroundColor Red
                    $failed++
                }
                else {
                    Write-Host "FAIL ($statusCode): $errBody" -ForegroundColor Red
                    $failed++
                }
            }
            catch {
                Write-Host "ERROR ($statusCode)" -ForegroundColor Red
                $errored++
            }
        }
        else {
            Write-Host "CONNECTION ERROR: $_" -ForegroundColor Red
            $errored++
        }
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "   Results: $passed passed, $failed failed, $errored errors" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan