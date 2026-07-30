<#
.SYNOPSIS
    End-to-end smoke test of every model exposed by the proxy, exactly the way
    Visual Studio 2026 BYOM talks to it (Ollama /api/tags + /api/chat).

.DESCRIPTION
    Reads /api/tags, then sends a tiny prompt to every model (or a filtered
    subset) through /api/chat. Verifies for each model:
      * HTTP status
      * that the request was actually routed to the provider the tag claims
        (X-Proxy-Provider response header)
      * that a non-empty completion came back
    With -Stream it repeats the check over the NDJSON streaming path, which is
    what VS 2026 actually uses.

.EXAMPLE
    ./scripts/test-all-providers.ps1
    ./scripts/test-all-providers.ps1 -BaseUrl http://localhost:11500 -Stream
    ./scripts/test-all-providers.ps1 -Provider ollama,zai -Stream
#>
[CmdletBinding()]
param(
    [string]   $BaseUrl     = "http://localhost:11434",
    [string[]] $Provider    = @(),
    [switch]   $Stream,
    [int]      $MaxTokens   = 24,
    [int]      $TimeoutSec  = 120,
    [string]   $Prompt      = "Reply with exactly one word: OK",
    [string]   $JsonReport
)

$ErrorActionPreference = 'Stop'

function Get-Tags {
    param([string] $BaseUrl)
    try {
        return (Invoke-RestMethod -Uri "$BaseUrl/api/tags" -TimeoutSec 60).models
    }
    catch {
        throw "No se pudo leer $BaseUrl/api/tags — ¿está el proxy levantado? ($($_.Exception.Message))"
    }
}

function Invoke-ChatOnce {
    param(
        [string] $BaseUrl,
        [string] $Model,
        [bool]   $UseStream
    )

    $body = @{
        model    = $Model
        stream   = $UseStream
        messages = @(@{ role = "user"; content = $Prompt })
        options  = @{ num_predict = $MaxTokens }
    } | ConvertTo-Json -Depth 6 -Compress

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-WebRequest -Uri "$BaseUrl/api/chat" -Method Post `
            -ContentType "application/json" -Body $body `
            -TimeoutSec $TimeoutSec -SkipHttpErrorCheck
        $sw.Stop()

        $routed = $resp.Headers['X-Proxy-Provider']    | Select-Object -First 1
        $upstm  = $resp.Headers['X-Proxy-Upstream-Model'] | Select-Object -First 1
        $text   = ""

        # PowerShell hands back a byte[] for content types it does not treat as text
        # (application/x-ndjson is one), so decode before parsing.
        $raw = if ($resp.Content -is [byte[]]) {
            [System.Text.Encoding]::UTF8.GetString($resp.Content)
        } else {
            [string]$resp.Content
        }

        if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 300) {
            if ($UseStream) {
                # NDJSON (Ollama native) or SSE (converted OpenAI) — accept both.
                foreach ($line in ($raw -split "`n")) {
                    $line = $line.Trim()
                    if (-not $line) { continue }
                    if ($line.StartsWith("data:")) { $line = $line.Substring(5).Trim() }
                    if ($line -eq "[DONE]") { continue }
                    try { $obj = $line | ConvertFrom-Json } catch { continue }
                    # Ollama NDJSON chunk: { "message": { "content": "..." } }
                    if ($obj.PSObject.Properties['message'] -and $obj.message.content) {
                        $text += $obj.message.content
                        continue
                    }
                    # OpenAI SSE chunk: { "choices": [ { "delta": { "content": "..." } } ] }
                    if ($obj.PSObject.Properties['choices'] -and $obj.choices) {
                        $delta = $obj.choices[0].delta
                        if ($delta -and $delta.content) { $text += $delta.content }
                    }
                }
            }
            else {
                $obj  = $raw | ConvertFrom-Json
                $text = [string]$obj.message.content
            }
        }
        else {
            $text = $raw
        }

        return [pscustomobject]@{
            Status    = [int]$resp.StatusCode
            Routed    = $routed
            Upstream  = $upstm
            Ms        = $sw.ElapsedMilliseconds
            Text      = $text
        }
    }
    catch {
        $sw.Stop()
        return [pscustomobject]@{
            Status = -1; Routed = ""; Upstream = ""; Ms = $sw.ElapsedMilliseconds
            Text   = $_.Exception.Message
        }
    }
}

# ── Collect targets ───────────────────────────────────────────────────────────
$tags = Get-Tags -BaseUrl $BaseUrl
Write-Host "`n$($tags.Count) modelos publicados por $BaseUrl/api/tags" -ForegroundColor Cyan

$targets = foreach ($t in $tags) {
    # tag model id looks like "<match>@<provider>:latest"
    $id  = [string]$t.model
    $at  = $id.LastIndexOf('@')
    if ($at -lt 0) { continue }
    $rest = $id.Substring($at + 1)
    $prov = ($rest -split ':')[0]
    if ($Provider.Count -gt 0 -and $Provider -notcontains $prov) { continue }
    [pscustomobject]@{ Provider = $prov; Tag = $id; Display = [string]$t.name }
}

$mode = if ($Stream) { "streaming" } else { "no-streaming" }
Write-Host "Probando $($targets.Count) modelos en modo $mode`n" -ForegroundColor Cyan

# ── Run ───────────────────────────────────────────────────────────────────────
$results = foreach ($target in $targets) {
    $r = Invoke-ChatOnce -BaseUrl $BaseUrl -Model $target.Tag -UseStream:$Stream.IsPresent

    $misrouted = $r.Routed -and ($r.Routed -ne $target.Provider)
    $ok        = ($r.Status -eq 200) -and -not [string]::IsNullOrWhiteSpace($r.Text) -and -not $misrouted

    $verdict =
        if ($ok)                { "OK" }
        elseif ($misrouted)     { "MISROUTED" }
        elseif ($r.Status -eq 200) { "EMPTY" }
        else                    { "HTTP $($r.Status)" }

    $color = switch ($verdict) { "OK" { "Green" } "MISROUTED" { "Magenta" } default { "Red" } }
    $snippet = ($r.Text -replace '\s+', ' ')
    if ($snippet.Length -gt 70) { $snippet = $snippet.Substring(0, 70) + "..." }

    Write-Host ("{0,-11} {1,-48} {2,-10} {3,6}ms  {4}" -f `
        $target.Provider, $target.Display, $verdict, $r.Ms, $snippet) -ForegroundColor $color

    [pscustomobject]@{
        Provider = $target.Provider
        Model    = $target.Display
        Tag      = $target.Tag
        Mode     = $mode
        Verdict  = $verdict
        Status   = $r.Status
        RoutedTo = $r.Routed
        Upstream = $r.Upstream
        Ms       = $r.Ms
        Response = $snippet
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host "`n── Resumen por proveedor ($mode) ──" -ForegroundColor Cyan
$results | Group-Object Provider | Sort-Object Name | ForEach-Object {
    $okCount = ($_.Group | Where-Object Verdict -eq 'OK').Count
    $total   = $_.Count
    $color   = if ($okCount -eq $total) { "Green" } elseif ($okCount -eq 0) { "Red" } else { "Yellow" }
    $failed  = ($_.Group | Where-Object Verdict -ne 'OK' | ForEach-Object { "$($_.Model) [$($_.Verdict)]" }) -join ", "
    Write-Host ("{0,-11} {1,2}/{2,-2} OK  {3}" -f $_.Name, $okCount, $total, $failed) -ForegroundColor $color
}

$totalOk = ($results | Where-Object Verdict -eq 'OK').Count
Write-Host "`nTOTAL: $totalOk/$($results.Count) modelos OK" -ForegroundColor $(if ($totalOk -eq $results.Count) { "Green" } else { "Yellow" })

if ($JsonReport) {
    $results | ConvertTo-Json -Depth 5 | Set-Content -Path $JsonReport -Encoding UTF8
    Write-Host "Reporte JSON: $JsonReport"
}

if ($totalOk -lt $results.Count) { exit 1 }
