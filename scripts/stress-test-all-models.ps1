$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$proxy = 'http://127.0.0.1:11434'
$runsPerModel = 2
$timeoutSec = 120
$outDir = 'docs/testing/logs'

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host '=== Multi-Pass Model Stress Test ===' -ForegroundColor Cyan
Write-Host "===================================="
Write-Host ''

# ── Fetch models ───────────────────────────────────────────────────
Write-Host '[1/5] Fetching models from /api/tags...'
$tags = Invoke-RestMethod -Uri "$proxy/api/tags" -TimeoutSec 20
$allModels = @($tags.models)
$total = $allModels.Count
Write-Host "  Got $total models"
Write-Host ''

# ── PASS 1: Latency benchmark (2 runs each, streaming) ─────────────
Write-Host '[2/5] PASS 1: Latency (2 runs, streaming)' -ForegroundColor Yellow
$p1Results = @()
$idx = 0

foreach ($m in $allModels) {
    $idx++
    $modelName = $m.name
    $prov = $m.provider
    $upstream = $m.upstream_model
    $timings = @()
    $errors = 0
    $errorMsg = ''
    $sample = ''

    for ($i = 1; $i -le $runsPerModel; $i++) {
        try {
            $body = @{
                model = $modelName
                stream = $true
                messages = @(@{ role = 'user'; content = 'Say exactly: OK' })
            } | ConvertTo-Json -Depth 4

            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $resp = Invoke-RestMethod -Uri "$proxy/api/chat" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec $timeoutSec
            $sw.Stop()
            $timings += $sw.ElapsedMilliseconds
            if ($resp.message -and $resp.message.content) { $sample = $resp.message.content }
        } catch {
            $errors++
            if (-not $errorMsg) { $errorMsg = $_.Exception.Message }
            if ($errorMsg.Length -gt 200) { $errorMsg = $errorMsg.Substring(0, 200) }
        }
    }

    $avg = if ($timings.Count -gt 0) { [math]::Round(($timings | Measure-Object -Average).Average, 0) } else { -1 }
    $med = if ($timings.Count -gt 0) { ($timings | Sort-Object)[[math]::Floor($timings.Count / 2)] } else { -1 }
    $min = if ($timings.Count -gt 0) { ($timings | Measure-Object -Minimum).Minimum } else { -1 }
    $max = if ($timings.Count -gt 0) { ($timings | Measure-Object -Maximum).Maximum } else { -1 }

    $p1Results += [pscustomobject]@{
        model = $modelName; provider = $prov; upstream_model = $upstream
        pass = '1_latency'; runs = ($timings -join ',')
        success = ($runsPerModel - $errors); total = $runsPerModel
        avg_ms = $avg; med_ms = $med; min_ms = $min; max_ms = $max
        sample = ($sample -replace '\s+', ' '); error = $errorMsg
    }

    $icon = if ($errors -eq 0) { 'OK' } else { 'ERR' }
    Write-Host "  [$idx/$total] $icon $modelName av=$avg`ms med=$med`ms ok=$($runsPerModel-$errors)/$runsPerModel"
}

Write-Host ''

# ── PASS 2: Coding scenario (non-streaming, system prompt) ─────────
Write-Host '[3/5] PASS 2: Coding agent (non-streaming, system prompt)' -ForegroundColor Yellow
$p2Results = @()
$idx = 0

foreach ($m in $allModels) {
    $idx++
    $modelName = $m.name; $prov = $m.provider; $upstream = $m.upstream_model
    $timing = -1; $ok = $false; $sample = ''; $errorMsg = ''; $contentLen = 0

    try {
        $body = @{
            model = $modelName; stream = $false; temperature = 0.2; max_tokens = 128
            messages = @(
                @{ role = 'system'; content = 'You are an expert software engineer. Be precise and concise.' }
                @{ role = 'user'; content = 'Write a Python function that checks if a string is a palindrome. Return ONLY the code, no explanation.' }
            )
        } | ConvertTo-Json -Depth 6

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $resp = Invoke-RestMethod -Uri "$proxy/api/chat" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec $timeoutSec
        $sw.Stop()
        $timing = $sw.ElapsedMilliseconds

        if ($resp.message -and $resp.message.content) {
            $sample = $resp.message.content; $contentLen = $sample.Length
            $ok = $contentLen -gt 10 -and ($sample -match 'def|palindrome|return|reverse')
        }
    } catch {
        $errorMsg = $_.Exception.Message
        if ($errorMsg.Length -gt 200) { $errorMsg = $errorMsg.Substring(0, 200) }
    }

    $trimmed = ($sample -replace '\s+', ' ')
    $p2Results += [pscustomobject]@{
        model = $modelName; provider = $prov; upstream_model = $upstream
        pass = '2_coding'; timing_ms = $timing; success = $ok
        content_length = $contentLen
        sample = if ($trimmed.Length -gt 160) { $trimmed.Substring(0, 160) } else { $trimmed }
        error = $errorMsg
    }

    $icon = if ($ok) { 'OK' } elseif ($errorMsg) { 'ERR' } else { 'BIN' }
    Write-Host "  [$idx/$total] $icon $modelName time=$timing`ms len=$contentLen"
}

Write-Host ''

# ── PASS 3: Copilot payload (v1/chat/completions, streaming) ───────
Write-Host '[4/5] PASS 3: Copilot simulation (v1/chat/completions, streaming)' -ForegroundColor Yellow
$p3Results = @()
$idx = 0

foreach ($m in $allModels) {
    $idx++
    $modelName = $m.name; $prov = $m.provider; $upstream = $m.upstream_model
    $timing = -1; $ok = $false; $finishReason = ''; $errorMsg = ''; $totalChunks = 0

    try {
        $body = @{
            model = $modelName; stream = $true; temperature = 0.2; max_tokens = 256
            top_k = 40; parallel_tool_calls = $true
            response_format = @{ type = 'json_object' }
            messages = @(
                @{ role = 'system'; content = 'You are a highly capable AI coding assistant integrated into an IDE. You help users write, debug, and refactor code. Keep responses focused on code.' }
                @{ role = 'user'; content = 'Write a JavaScript function to sort an array of objects by a given key. Return JSON: {"code":"..."}' }
            )
        } | ConvertTo-Json -Depth 8

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $resp = Invoke-WebRequest -Uri "$proxy/v1/chat/completions" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec $timeoutSec
        $sw.Stop()
        $timing = $sw.ElapsedMilliseconds

        $lines = ($resp.Content -split '\n') | Where-Object { $_ -match '^data: ' -and $_ -notmatch '\[DONE\]' }
        $totalChunks = $lines.Count

        if ($lines.Count -gt 0) {
            $lastData = $lines[-1].Substring(6)
            try {
                $lastObj = $lastData | ConvertFrom-Json
                if ($lastObj.choices[0].finish_reason) {
                    $finishReason = $lastObj.choices[0].finish_reason
                    $ok = ($finishReason -eq 'stop')
                }
            } catch { }
        }
    } catch {
        $errorMsg = $_.Exception.Message
        if ($errorMsg.Length -gt 200) { $errorMsg = $errorMsg.Substring(0, 200) }
    }

    $p3Results += [pscustomobject]@{
        model = $modelName; provider = $prov; upstream_model = $upstream
        pass = '3_copilot'; timing_ms = $timing; success = $ok
        chunks = $totalChunks; finish_reason = $finishReason; error = $errorMsg
    }

    $icon = if ($ok) { 'OK' } elseif ($totalChunks -gt 0) { 'BIN' } else { 'ERR' }
    Write-Host "  [$idx/$total] $icon $modelName time=$timing`ms chunks=$totalChunks finish=$finishReason"
}

Write-Host ''

# ── Combine & Report ───────────────────────────────────────────────
Write-Host '[5/5] Generating reports...' -ForegroundColor Cyan

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

$modelSummary = @()
$modelsByName = $p1Results | Group-Object model
foreach ($g in $modelsByName) {
    $p1 = $g.Group | Select-Object -First 1
    $p2 = $p2Results | Where-Object { $_.model -eq $g.Name } | Select-Object -First 1
    $p3 = $p3Results | Where-Object { $_.model -eq $g.Name } | Select-Object -First 1

    $modelSummary += [pscustomobject]@{
        model = $p1.model; provider = $p1.provider; upstream = $p1.upstream_model
        p1_latency_avg = $p1.avg_ms; p1_latency_med = $p1.med_ms
        p1_success = ($p1.success.ToString() + '/' + $p1.total.ToString())
        p2_coding_ms = $p2.timing_ms; p2_coding_ok = $p2.success; p2_coding_len = $p2.content_length
        p3_copilot_ms = $p3.timing_ms; p3_copilot_ok = $p3.success; p3_chunks = $p3.chunks
    }
}

$provSummary = $modelSummary | Group-Object provider | ForEach-Object {
    $items = $_.Group
    [pscustomobject]@{
        provider = $_.Name; models = $items.Count
        p1_ok = "$(($items | Where-Object { $_.p1_latency_avg -gt 0 }).Count)/$($items.Count)"
        p2_ok = "$(($items | Where-Object { $_.p2_coding_ok }).Count)/$($items.Count)"
        p3_ok = "$(($items | Where-Object { $_.p3_copilot_ok }).Count)/$($items.Count)"
        avg_latency = [math]::Round(($items | Where-Object { $_.p1_latency_avg -gt 0 } | Measure-Object -Property p1_latency_avg -Average).Average, 0)
    }
} | Sort-Object provider

$allPass1 = $p1Results | Where-Object { $_.avg_ms -gt 0 }
$allPass2 = $p2Results | Where-Object { $_.success }
$allPass3 = $p3Results | Where-Object { $_.success }

$report = [pscustomobject]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString('o')
    total_models = $total
    passes = @(
        [pscustomobject]@{ pass = 1; name = 'latency'; runs_per_model = $runsPerModel; success = "$($allPass1.Count)/$total"; avg_latency_ms = [math]::Round(($allPass1 | Measure-Object -Property avg_ms -Average).Average, 0) }
        [pscustomobject]@{ pass = 2; name = 'coding'; runs_per_model = 1; success = "$($allPass2.Count)/$total"; avg_latency_ms = [math]::Round(($allPass2 | Measure-Object -Property timing_ms -Average).Average, 0) }
        [pscustomobject]@{ pass = 3; name = 'copilot'; runs_per_model = 1; success = "$($allPass3.Count)/$total"; avg_latency_ms = [math]::Round(($allPass3 | Measure-Object -Property timing_ms -Average).Average, 0) }
    )
    provider_summary = @($provSummary)
    model_summary = @($modelSummary | Sort-Object p1_latency_avg)
    full_results = @{ pass1 = @($p1Results); pass2 = @($p2Results); pass3 = @($p3Results) }
}

$jsonPath = "$outDir/stress-test-$stamp.json"
$report | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding UTF8

$md = @()
$md += '# Model Stress Test Report'
$md += ''
$md += ('**Generated:** ' + $report.generated_at_utc)
$md += ('**Total models:** ' + $report.total_models)
$md += ''
$md += '## Pass Summary'
$md += ''
$md += '| Pass | Description | Runs/Model | Success | Avg Latency |'
$md += '|------|-------------|------------|---------|-------------|'
foreach ($p in $report.passes) {
    $md += ('| ' + $p.pass + ' | ' + $p.name + ' | ' + $p.runs_per_model + ' | ' + $p.success + ' | ' + $p.avg_latency_ms + 'ms |')
}
$md += ''
$md += '## Provider Summary'
$md += ''
$md += '| Provider | Models | P1 OK | P2 OK | P3 OK | Avg Latency |'
$md += '|----------|--------|-------|-------|-------|-------------|'
foreach ($p in $provSummary) {
    $md += ('| ' + $p.provider + ' | ' + $p.models + ' | ' + $p.p1_ok + ' | ' + $p.p2_ok + ' | ' + $p.p3_ok + ' | ' + $p.avg_latency + 'ms |')
}
$md += ''
$md += '## Model Details (by latency)'
$md += ''
$md += '| # | Model | Provider | P1 avg | P1 med | P1 ok | P2 ms | P2 | P3 ms | P3 | Chunks |'
$md += '|---|-------|----------|--------|--------|-------|-------|----|-------|----|--------|'
$i = 0
foreach ($m in $modelSummary) {
    $i++
    $p2i = if ($m.p2_coding_ok) { 'OK' } else { 'ERR' }
    $p3i = if ($m.p3_copilot_ok) { 'OK' } else { 'ERR' }
    $md += ('| ' + $i + ' | `' + $m.model + '` | ' + $m.provider + ' | ' + $m.p1_latency_avg + 'ms | ' + $m.p1_latency_med + 'ms | ' + $m.p1_success + ' | ' + $m.p2_coding_ms + 'ms | ' + $p2i + ' | ' + $m.p3_copilot_ms + 'ms | ' + $p3i + ' | ' + $m.p3_chunks + ' |')
}
$md += ''
$md += '## Failures'
$md += ''
$failed = @($modelSummary | Where-Object { $_.p1_latency_avg -le 0 -or (-not $_.p2_coding_ok) -or (-not $_.p3_copilot_ok) })
if ($failed.Count -gt 0) {
    foreach ($f in $failed) {
        $reasons = @()
        if ($f.p1_latency_avg -le 0) { $reasons += 'P1' }
        if (-not $f.p2_coding_ok) { $reasons += 'P2' }
        if (-not $f.p3_copilot_ok) { $reasons += 'P3' }
        $md += ('- `' + $f.model + '` (' + $f.provider + '): ' + ($reasons -join ', '))
    }
} else {
    $md += 'All models passed all passes.'
}

$mdPath = "$outDir/stress-test.md"
$md | Set-Content -Path $mdPath -Encoding UTF8

Write-Host ''
Write-Host '====================================' -ForegroundColor Cyan
Write-Host 'RESULTS' -ForegroundColor Cyan
Write-Host '===================================='
Write-Host ('Models: ' + $report.total_models)
Write-Host ('P1 latency: ' + $report.passes[0].success + ' @ ' + $report.passes[0].avg_latency_ms + 'ms')
Write-Host ('P2 coding:  ' + $report.passes[1].success + ' @ ' + $report.passes[1].avg_latency_ms + 'ms')
Write-Host ('P3 copilot: ' + $report.passes[2].success + ' @ ' + $report.passes[2].avg_latency_ms + 'ms')
Write-Host ''
Write-Host ('JSON: ' + $jsonPath)
Write-Host ('MD:   ' + $mdPath)

$provSummary | Format-Table provider, models, p1_ok, p2_ok, p3_ok, avg_latency -AutoSize