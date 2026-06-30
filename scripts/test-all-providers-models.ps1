# test-all-providers-models.ps1
# Comprehensive test: hits every provider that has an API key and tests
# every enabled model from config/model-selection/*.json directly against
# the upstream provider API.
#
# Usage:  pwsh scripts/test-all-providers-models.ps1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# -- Helpers --

function Get-EnvMap {
    $map = @{}
    Get-Content .env |
        Where-Object { $_ -match '^[A-Za-z_][A-Za-z0-9_]*=' } |
        ForEach-Object {
            $k, $v = $_ -split '=', 2
            $map[$k] = $v
        }
    return $map
}

function Extract-ChatText($resp) {
    if ($resp.choices -and $resp.choices.Count -gt 0) {
        $msg = $resp.choices[0].message
        if ($msg) {
            if (-not [string]::IsNullOrWhiteSpace($msg.content)) { return $msg.content }
            if (-not [string]::IsNullOrWhiteSpace($msg.reasoning_content)) { return $msg.reasoning_content }
            if (-not [string]::IsNullOrWhiteSpace($msg.reasoning)) { return $msg.reasoning }
        }
    }
    if ($resp.message) {
        if (-not [string]::IsNullOrWhiteSpace($resp.message.content)) { return $resp.message.content }
        if (-not [string]::IsNullOrWhiteSpace($resp.message.reasoning)) { return $resp.message.reasoning }
    }
    return ''
}

function Get-ErrorBody($ex) {
    try {
        $sr = New-Object IO.StreamReader($ex.Exception.Response.GetResponseStream())
        $body = $sr.ReadToEnd()
        $sr.Close()
        return $body
    }
    catch {
        return $ex.Exception.Message
    }
}

# -- Load .env --

$envMap = Get-EnvMap

# -- Provider definitions (from ProviderCapabilitiesRegistry) --

$providerDefs = @(
    @{ name = 'deepseek';    key = $envMap['PROVIDER_DEEPSEEK_API_KEY'];    base = $envMap['PROVIDER_DEEPSEEK_BASE_URL'];    default = 'https://api.deepseek.com';                    chat = '/v1/chat/completions';            ollama = $false },
    @{ name = 'openai';      key = $envMap['PROVIDER_OPENAI_API_KEY'];      base = $envMap['PROVIDER_OPENAI_BASE_URL'];      default = 'https://api.openai.com';                      chat = '/v1/chat/completions';            ollama = $false },
    @{ name = 'google';      key = $envMap['PROVIDER_GOOGLE_API_KEY'];      base = $envMap['PROVIDER_GOOGLE_BASE_URL'];      default = 'https://generativelanguage.googleapis.com';  chat = '/v1beta/openai/chat/completions'; ollama = $false },
    @{ name = 'zai';         key = $envMap['PROVIDER_ZAI_API_KEY'];         base = $envMap['PROVIDER_ZAI_BASE_URL'];         default = 'https://api.z.ai/api/paas/v4';                chat = '/chat/completions';               ollama = $false },
    @{ name = 'nvidia';      key = $envMap['PROVIDER_NVIDIA_API_KEY'];      base = $envMap['PROVIDER_NVIDIA_BASE_URL'];      default = 'https://integrate.api.nvidia.com';           chat = '/v1/chat/completions';            ollama = $false },
    @{ name = 'openrouter';  key = $envMap['PROVIDER_OPENROUTER_API_KEY'];  base = $envMap['PROVIDER_OPENROUTER_BASE_URL'];  default = 'https://openrouter.ai/api';                   chat = '/v1/chat/completions';            ollama = $false },
    @{ name = 'groq';        key = $envMap['PROVIDER_GROQ_API_KEY'];        base = $envMap['PROVIDER_GROQ_BASE_URL'];        default = 'https://api.groq.com/openai';                chat = '/v1/chat/completions';            ollama = $false },
    @{ name = 'ollamacloud'; key = $envMap['PROVIDER_OLLAMACLOUD_API_KEY']; base = $envMap['PROVIDER_OLLAMA_BASE_URL'];      default = 'https://ollama.com';                          chat = '/api/chat';                       ollama = $true  },
    @{ name = 'moonshot';    key = $envMap['PROVIDER_MOONSHOT_API_KEY'];    base = $envMap['PROVIDER_MOONSHOT_BASE_URL'];    default = 'https://api.moonshot.ai';                     chat = '/v1/chat/completions';            ollama = $false },
    @{ name = 'cerebras';    key = $envMap['PROVIDER_CEREBRAS_API_KEY'];    base = $envMap['PROVIDER_CEREBRAS_BASE_URL'];    default = 'https://api.cerebras.ai';                     chat = '/v1/chat/completions';            ollama = $false },
    @{ name = 'zenmux';      key = $envMap['PROVIDER_ZENMUX_API_KEY'];      base = $envMap['PROVIDER_ZENMUX_BASE_URL'];      default = 'https://zenmux.ai/api';                       chat = '/v1/chat/completions';            ollama = $false }
)

# -- Load models from config/model-selection/*.json --

$providerModels = @{}
Get-ChildItem 'config/model-selection/*.json' | ForEach-Object {
    $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
    $provName = $j.provider
    $models = @($j.models | Where-Object { $_.enabled -ne $false } | ForEach-Object { $_.match })
    if ($providerModels.ContainsKey($provName)) {
        $providerModels[$provName] += $models
    } else {
        $providerModels[$provName] = $models
    }
}

# -- Run tests --

$results = @()
$total = 0
$passed = 0
$failed = 0
$skipped = 0

Write-Host ''
Write-Host '======================================================================='
Write-Host '  Testing all providers and models with API keys'
Write-Host '======================================================================='
Write-Host ''

foreach ($p in $providerDefs) {
    $provName = $p.name
    $apiKey = $p.key
    $baseUrl = if ($p.base) { $p.base } else { $p.default }
    $chatPath = $p.chat
    $isOllama = $p.ollama

    # ollamacloud config uses provider name "ollama"
    $configKey = if ($provName -eq 'ollamacloud') { 'ollama' } else { $provName }
    $models = @()
    if ($providerModels.ContainsKey($configKey)) {
        $models = $providerModels[$configKey]
    } elseif ($providerModels.ContainsKey($provName)) {
        $models = $providerModels[$provName]
    }

    Write-Host ("-- Provider: {0} ({1} models) --" -f $provName, $models.Count)

    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        Write-Host "  [SKIP] no API key configured" -ForegroundColor Yellow
        $skipped++
        $results += [pscustomobject]@{
            provider = $provName; model = '(all)'; status = 'skipped'; http_status = ''; latency_ms = 0; sample = ''; error = 'no API key'
        }
        continue
    }

    if ($models.Count -eq 0) {
        Write-Host "  [SKIP] no enabled models in config" -ForegroundColor Yellow
        $skipped++
        $results += [pscustomobject]@{
            provider = $provName; model = '(all)'; status = 'skipped'; http_status = ''; latency_ms = 0; sample = ''; error = 'no models'
        }
        continue
    }

    $headers = @{ Authorization = "Bearer $apiKey" }

    foreach ($model in $models) {
        $total++
        $status = 'fail'
        $httpStatus = ''
        $latency = 0
        $sample = ''
        $errorText = ''

        try {
            if ($isOllama) {
                $body = @{
                    model = $model
                    stream = $false
                    messages = @(@{ role = 'user'; content = 'Reply exactly: OK' })
                } | ConvertTo-Json -Depth 6
            } else {
                $body = @{
                    model = $model
                    stream = $false
                    max_tokens = 16
                    temperature = 0.2
                    messages = @(@{ role = 'user'; content = 'Reply exactly: OK' })
                } | ConvertTo-Json -Depth 6
            }

            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $chatResp = Invoke-RestMethod -Uri ($baseUrl + $chatPath) -Method Post -Headers ($headers + @{ 'Content-Type' = 'application/json' }) -Body $body -TimeoutSec 60
            $sw.Stop()
            $latency = [int]$sw.Elapsed.TotalMilliseconds

            $sample = Extract-ChatText $chatResp

            if ([string]::IsNullOrWhiteSpace($sample)) {
                $status = 'empty'
            } else {
                $status = 'ok'
                $passed++
                if ($sample.Length -gt 80) { $sample = $sample.Substring(0, 80) }
            }
        }
        catch {
            try { $httpStatus = 'http_' + $_.Exception.Response.StatusCode.value__ } catch { $httpStatus = 'error' }
            $errorText = Get-ErrorBody $_
            if ($errorText.Length -gt 200) { $errorText = $errorText.Substring(0, 200) }
            $failed++
        }

        $results += [pscustomobject]@{
            provider = $provName; model = $model; status = $status; http_status = $httpStatus; latency_ms = $latency; sample = $sample; error = $errorText
        }

        # Console output
        $icon = switch ($status) {
            'ok'    { '[OK]  ' }
            'empty' { '[EMPTY]' }
            'fail'  { '[FAIL]' }
            default { '[????]' }
        }
        $color = switch ($status) {
            'ok'    { 'Green' }
            'empty' { 'Yellow' }
            'fail'  { 'Red' }
            default { 'Gray' }
        }
        $latStr = if ($latency -gt 0) { "{0,6}ms" -f $latency } else { '      ' }
        $errStr = if ($errorText) { " -- $errorText" } else { '' }
        $line = ("  {0} {1} {2} {3}{4}" -f $icon, $model.PadRight(45), $latStr, $status.ToUpper(), $errStr)
        Write-Host $line -ForegroundColor $color
    }
    Write-Host ''
}

# -- Summary --

Write-Host '======================================================================='
Write-Host '  SUMMARY'
Write-Host '======================================================================='
Write-Host "  Total tested: $total"
Write-Host "  Passed:       $passed" -ForegroundColor Green
Write-Host "  Failed:       $failed" -ForegroundColor Red
Write-Host "  Skipped:      $skipped" -ForegroundColor Yellow
Write-Host ''

# Per-provider breakdown
Write-Host '-- Per-provider breakdown --'
$provGroups = $results | Group-Object provider | Sort-Object Name
foreach ($pg in $provGroups) {
    $ok = ($pg.Group | Where-Object { $_.status -eq 'ok' }).Count
    $fail = ($pg.Group | Where-Object { $_.status -eq 'fail' }).Count
    $empty = ($pg.Group | Where-Object { $_.status -eq 'empty' }).Count
    $skip = ($pg.Group | Where-Object { $_.status -eq 'skipped' }).Count
    $totalP = $pg.Group.Count
    Write-Host ("  {0,-15} {1}/{2} ok, {3} fail, {4} empty, {5} skipped" -f $pg.Name, $ok, $totalP, $fail, $empty, $skip)
}
Write-Host ''

# -- Save JSON report --

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$dir = 'docs/testing'
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$outPath = "$dir/all-providers-models-$stamp.json"

$report = [pscustomobject]@{
    generated_at = (Get-Date).ToString('o')
    summary = [pscustomobject]@{
        total = $total; passed = $passed; failed = $failed; skipped = $skipped
    }
    results = $results
}

$report | ConvertTo-Json -Depth 8 | Set-Content -Path $outPath -Encoding UTF8
Write-Host "Report saved: $outPath"