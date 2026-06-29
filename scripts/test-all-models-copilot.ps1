# Test all enabled models simulating GitHub Copilot requests
# Measures streaming time-to-first-byte, total latency, and response quality

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$proxyBase = 'http://localhost:11434'
$results = @()

Write-Host '=== GitHub Copilot Simulation - Model Test Suite ==='
Write-Host "Proxy: $proxyBase"
Write-Host ''

# Get current available models from proxy
try {
    $health = Invoke-RestMethod -Uri "$proxyBase/health" -Method Get -TimeoutSec 10
    $models = $health.available_models | Where-Object { $_ -notmatch '@' } | Select-Object -Unique
    Write-Host "Proxy running - $($models.Count) unique models to test"
    Write-Host ''
} catch {
    Write-Host "ERROR: Proxy not reachable at $proxyBase"
    exit 1
}

# GitHub Copilot typical request patterns
$copilotPrompts = @(
    @{role='system'; content='You are a coding assistant. Be concise and accurate.'},
    @{role='user'; content='Write a Python function that checks if a number is prime. Return only the function, no explanation.'}
)

$bodyTemplate = @{
    stream = $true
    max_tokens = 256
    messages = $copilotPrompts
} | ConvertTo-Json -Depth 6

$simplePrompt = @{
    stream = $false
    max_tokens = 32
    messages = @(@{role='user'; content='Reply exactly: OK'})
} | ConvertTo-Json -Depth 6

function Test-Model {
    param($modelId)

    Write-Host -NoNewline "  $modelId ... "

    $result = [pscustomobject]@{
        model = $modelId
        timestamp = (Get-Date).ToUniversalTime().ToString('o')
        nonstreaming = $null
        streaming = $null
        first_byte_ms = 0
        total_ms = 0
    }

    # Test 1: Non-streaming (basic connectivity)
    try {
        $nsBody = $simplePrompt -replace '"model":\s*"[^"]*"', """model"":""$modelId"""
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-RestMethod -Uri "$proxyBase/v1/chat/completions" -Method Post -Body $nsBody -ContentType 'application/json' -TimeoutSec 120
        $sw.Stop()
        $latency = [int]$sw.Elapsed.TotalMilliseconds
        $content = if ($r.choices[0].message.content) { $r.choices[0].message.content } else { '(empty/reasoning)' }
        $result.nonstreaming = "$($r.model) | ${latency}ms | $content"
    } catch {
        $result.nonstreaming = "FAIL: $($_.Exception.Message)"
    }

    # Test 2: Streaming (GitHub Copilot pattern) - measure TTFB via curl
    try {
        $sBody = $bodyTemplate -replace '"model":\s*"[^"]*"', """model"":""$modelId"""
        
        # Write body to temp file to avoid shell escaping issues
        $tmpBody = [System.IO.Path]::GetTempFileName()
        $sBody | Set-Content $tmpBody -Encoding UTF8
        
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $chunks = 0
        $firstByte = -1
        $contentAccum = ''
        $reasoningAccum = ''
        
        # Use curl with --max-time 120, read line by line via pipe
        $pinfo = New-Object System.Diagnostics.ProcessStartInfo
        $pinfo.FileName = 'curl.exe'
        $pinfo.Arguments = "-s -N -X POST `"$proxyBase/v1/chat/completions`" -H `"Content-Type: application/json`" -d `"@$tmpBody`" --max-time 120"
        $pinfo.RedirectStandardOutput = $true
        $pinfo.RedirectStandardError = $false
        $pinfo.UseShellExecute = $false
        $pinfo.CreateNoWindow = $true
        
        $process = [System.Diagnostics.Process]::Start($pinfo)
        
        while (($line = $process.StandardOutput.ReadLine()) -ne $null) {
            if ($firstByte -lt 0) {
                $firstByte = [int]$sw.Elapsed.TotalMilliseconds
            }
            if ($line -match '^data: (.+)$') {
                $json = $Matches[1]
                if ($json -ne '[DONE]') {
                    try {
                        $chunk = $json | ConvertFrom-Json
                        $chunks++
                        if ($chunk.choices[0].delta.content) { $contentAccum += $chunk.choices[0].delta.content }
                        if ($chunk.choices[0].delta.reasoning_content) { $reasoningAccum += $chunk.choices[0].delta.reasoning_content }
                    } catch { }
                }
            }
        }
        
        $process.WaitForExit()
        $sw.Stop()
        Remove-Item $tmpBody -Force -ErrorAction SilentlyContinue
        
        $total = [int]$sw.Elapsed.TotalMilliseconds
        if ($firstByte -lt 0) { $firstByte = $total }
        
        $result.streaming = "OK | $chunks chunks | TTFB:${firstByte}ms | Total:${total}ms"
        $result.first_byte_ms = $firstByte
        $result.total_ms = $total
    } catch {
        $result.streaming = "FAIL: $($_.Exception.Message)"
    }

    $status = if ($result.streaming -match '^OK') { 'OK' } else { 'FAIL' }
    Write-Host "$status TTFB:$($result.first_byte_ms)ms Total:$($result.total_ms)ms"

    return $result
}

$idx = 0
foreach ($model in $models) {
    $idx++
    Write-Host "[$idx/$($models.Count)]"
    $r = Test-Model -modelId $model
    $results += $r
}

# Summary
Write-Host ''
Write-Host '=============================================='
Write-Host '          TEST SUMMARY'
Write-Host '=============================================='

$success = ($results | Where-Object { $_.streaming -match '^OK' }).Count
$fail = ($results | Where-Object { $_.streaming -notmatch '^OK' }).Count
$total = $results.Count

Write-Host "Total models tested: $total"
Write-Host "Streaming success:   $success"
Write-Host "Streaming failures:  $fail"

if ($success -gt 0) {
    $avgTtfb = [int](($results | Where-Object { $_.first_byte_ms -gt 0 } | Measure-Object -Property first_byte_ms -Average).Average)
    $avgTotal = [int](($results | Where-Object { $_.total_ms -gt 0 } | Measure-Object -Property total_ms -Average).Average)
    Write-Host "Avg TTFB (streaming): ${avgTtfb}ms"
    Write-Host "Avg total (streaming): ${avgTotal}ms"
}

Write-Host ''

# Failures
if ($fail -gt 0) {
    Write-Host '--- FAILURES ---'
    foreach ($r in ($results | Where-Object { $_.streaming -notmatch '^OK' })) {
        Write-Host "  $($r.model): $($r.streaming)"
    }
    Write-Host ''
}

# Detail by TTFB
Write-Host '--- STREAMING LATENCY (sorted by TTFB) ---'
$results | Where-Object { $_.first_byte_ms -gt 0 } | Sort-Object first_byte_ms | ForEach-Object {
    $flags = ''
    if ($_.first_byte_ms -gt 30000) { $flags = 'SLOW(>30s)' }
    if ($_.first_byte_ms -gt 100000) { $flags = 'TIMEOUT_RISK(>100s)' }
    Write-Host ("  {0,-50} TTFB:{1,6}ms  Total:{2,6}ms  {3}" -f $_.model, $_.first_byte_ms, $_.total_ms, $flags)
}

# Save report
$reportPath = 'docs/testing/model-copilot-test-results.json'
$reportDir = Split-Path $reportPath
if (!(Test-Path $reportDir)) { New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }

$report = [pscustomobject]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString('o')
    proxy = $proxyBase
    summary = [pscustomobject]@{
        total = $total
        streaming_ok = $success
        streaming_fail = $fail
        avg_ttfb_ms = if ($success -gt 0) { $avgTtfb } else { $null }
        avg_total_ms = if ($success -gt 0) { $avgTotal } else { $null }
    }
    results = $results | Sort-Object first_byte_ms
}
$report | ConvertTo-Json -Depth 6 | Set-Content $reportPath -Encoding UTF8
Write-Host "Report saved to: $reportPath"