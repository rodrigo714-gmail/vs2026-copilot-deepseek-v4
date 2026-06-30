using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DashboardEndpoints
{
    internal static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        // JSON API: full usage data for all providers
        app.MapGet("/api/usage", (UsageTrackerService usageTracker, ProviderRegistry providerRegistry) =>
        {
            // Ensure all configured providers are tracked (even those with zero usage)
            foreach (var provider in providerRegistry.Providers)
            {
                usageTracker.EnsureProvider(provider.Name);
            }

            var data = usageTracker.GetAllStats();
            return Results.Json(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
            });
        });

        // JSON API: usage data for a single provider
        app.MapGet("/api/usage/{provider}", (string provider, UsageTrackerService usageTracker) =>
        {
            var stats = usageTracker.GetProviderStats(provider);
            if (stats is null)
                return Results.NotFound(new { error = $"Provider '{provider}' not found" });

            return Results.Json(new
            {
                name = provider,
                displayName = FormatDisplayName(provider),
                stats
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
            });
        });

        // JSON API: provider billing/usage data from provider-specific APIs
        app.MapGet("/api/billing", async (ProviderBillingService billingService) =>
        {
            var billing = await billingService.GetAllBillingInfo();
            return Results.Json(billing, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
            });
        });

        // JSON API: billing data for a single provider
        app.MapGet("/api/billing/{provider}", async (string provider, ProviderBillingService billingService) =>
        {
            var info = await billingService.GetBillingInfo(provider);
            if (info is null)
                return Results.NotFound(new { error = $"Provider '{provider}' not found" });
            return Results.Json(info, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
            });
        });

        // Serve the dashboard HTML page
        app.MapGet("/dashboard", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(DashboardHtml, ctx.RequestAborted);
        });

        return app;
    }

    private static string FormatDisplayName(string raw)
    {
        return raw switch
        {
            "deepseek" => "DeepSeek",
            "openai" => "OpenAI",
            "nvidia" => "NVIDIA NIM",
            "groq" => "Groq",
            "openrouter" => "OpenRouter",
            "moonshot" => "Kimi/Moonshot",
            "cerebras" => "Cerebras",
            "zenmux" => "ZenMux",
            "ollama" => "Ollama Cloud",
            "google" => "Google Gemini",
            _ => char.ToUpper(raw[0]) + raw[1..]
        };
    }

    // ── Embedded HTML dashboard (SPA with Chart.js) ─────────────────────

    private const string DashboardHtml = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>AI Proxy Hub – Usage Dashboard</title>
<script src=""https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.min.js""></script>
<style>
  :root {
    --bg: #0d1117;
    --card-bg: #161b22;
    --border: #30363d;
    --fg: #e6edf3;
    --fg-muted: #8b949e;
    --accent: #58a6ff;
    --green: #3fb950;
    --red: #f85149;
    --orange: #d29922;
    --purple: #bc8cff;
    --header-bg: #0d1117;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, sans-serif;
    background: var(--bg);
    color: var(--fg);
    padding: 20px;
    min-height: 100vh;
  }
  .header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 0 20px;
    border-bottom: 1px solid var(--border);
    margin-bottom: 24px;
    flex-wrap: wrap;
    gap: 12px;
  }
  .header h1 {
    font-size: 22px;
    font-weight: 600;
    letter-spacing: -0.3px;
  }
  .header .subtitle {
    font-size: 13px;
    color: var(--fg-muted);
  }
  .header-right {
    display: flex;
    align-items: center;
    gap: 16px;
  }
  .last-updated {
    font-size: 12px;
    color: var(--fg-muted);
  }
  .refresh-indicator {
    display: inline-block;
    width: 10px;
    height: 10px;
    border-radius: 50%;
    background: var(--green);
    animation: pulse 2s infinite;
  }
  @keyframes pulse { 0%,100%{opacity:1} 50%{opacity:0.4} }

  .summary {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: 12px;
    margin-bottom: 24px;
  }
  .summary-card {
    background: var(--card-bg);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 16px;
    text-align: center;
  }
  .summary-card .label {
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 1px;
    color: var(--fg-muted);
    margin-bottom: 6px;
  }
  .summary-card .value {
    font-size: 24px;
    font-weight: 700;
    color: var(--accent);
  }
  .summary-card .value.green { color: var(--green); }
  .summary-card .value.orange { color: var(--orange); }
  .summary-card .value.purple { color: var(--purple); }

  .charts-row {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 16px;
    margin-bottom: 24px;
  }
  @media (max-width: 900px) {
    .charts-row { grid-template-columns: 1fr; }
  }
  .chart-card {
    background: var(--card-bg);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 16px;
  }
  .chart-card h3 {
    font-size: 14px;
    font-weight: 600;
    margin-bottom: 12px;
    color: var(--fg-muted);
  }
  .chart-card canvas {
    max-height: 260px;
  }
  .chart-card.full-width {
    grid-column: 1 / -1;
  }

  .providers-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(380px, 1fr));
    gap: 16px;
  }
  .provider-card {
    background: var(--card-bg);
    border: 1px solid var(--border);
    border-radius: 10px;
    padding: 16px;
    cursor: pointer;
    transition: border-color 0.2s, box-shadow 0.2s;
    position: relative;
    overflow: hidden;
  }
  .provider-card:hover {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px var(--accent);
  }
  .provider-card .provider-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 10px;
  }
  .provider-card .provider-name {
    font-size: 15px;
    font-weight: 600;
  }
  .provider-card .provider-status {
    font-size: 11px;
    padding: 2px 8px;
    border-radius: 4px;
  }
  .provider-card .provider-status.ok {
    background: rgba(63,185,80,0.15);
    color: var(--green);
  }
  .provider-card .provider-status.error {
    background: rgba(248,81,73,0.15);
    color: var(--red);
  }
  .provider-card .provider-status.inactive {
    background: rgba(139,148,158,0.15);
    color: var(--fg-muted);
  }
  .provider-card .stat-row {
    display: flex;
    justify-content: space-between;
    padding: 4px 0;
    font-size: 13px;
  }
  .provider-card .stat-row .stat-label {
    color: var(--fg-muted);
  }
  .provider-card .stat-row .stat-value {
    font-weight: 500;
  }
  .provider-card .progress-bar {
    margin: 8px 0;
    height: 4px;
    background: rgba(255,255,255,0.08);
    border-radius: 2px;
    overflow: hidden;
  }
  .provider-card .progress-bar .fill {
    height: 100%;
    border-radius: 2px;
    transition: width 0.5s ease;
  }
  .provider-card .progress-bar .fill.green { background: var(--green); }
  .provider-card .progress-bar .fill.orange { background: var(--orange); }
  .provider-card .progress-bar .fill.red { background: var(--red); }
  .provider-card .progress-bar .fill.blue { background: var(--accent); }

  .provider-chart-container {
    margin-top: 10px;
    padding-top: 10px;
    border-top: 1px solid var(--border);
  }
  .provider-chart-container canvas {
    max-height: 100px;
  }

  .modal-overlay {
    display: none;
    position: fixed;
    top: 0; left: 0; right: 0; bottom: 0;
    background: rgba(0,0,0,0.7);
    z-index: 1000;
    align-items: center;
    justify-content: center;
    padding: 20px;
  }
  .modal-overlay.active { display: flex; }
  .modal {
    background: var(--card-bg);
    border: 1px solid var(--border);
    border-radius: 12px;
    width: 100%;
    max-width: 720px;
    max-height: 85vh;
    overflow-y: auto;
    padding: 24px;
  }
  .modal-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 16px;
  }
  .modal-header h2 { font-size: 20px; }
  .modal-close {
    background: none;
    border: 1px solid var(--border);
    color: var(--fg-muted);
    font-size: 18px;
    cursor: pointer;
    border-radius: 6px;
    padding: 4px 10px;
    transition: color 0.2s;
  }
  .modal-close:hover { color: var(--fg); }
  .modal .detail-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 8px;
  }
  .modal .detail-item {
    padding: 8px 12px;
    background: rgba(255,255,255,0.04);
    border-radius: 6px;
  }
  .modal .detail-item .label {
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.5px;
    color: var(--fg-muted);
  }
  .modal .detail-item .value {
    font-size: 16px;
    font-weight: 600;
    margin-top: 2px;
  }
  .modal .gauge-section {
    margin-top: 16px;
  }
  .loading { text-align: center; padding: 40px; color: var(--fg-muted); }
  .error-state { text-align: center; padding: 40px; color: var(--red); }
  .no-data { text-align: center; padding: 40px; color: var(--fg-muted); font-size: 14px; }
  @media (max-width: 600px) {
    .providers-grid { grid-template-columns: 1fr; }
    .summary { grid-template-columns: 1fr 1fr; }
  }
  .billing-badge {
    display: inline-block;
    font-size: 11px;
    padding: 2px 6px;
    border-radius: 4px;
    background: rgba(88,166,255,0.12);
    color: var(--accent);
    margin-left: 8px;
  }
</style>
</head>
<body>
<div class=""header"">
  <div>
    <h1>🔌 AI Proxy Hub</h1>
    <div class=""subtitle"">Provider subscription status & usage</div>
  </div>
  <div class=""header-right"">
    <span class=""last-updated"" id=""lastUpdated"">Loading...</span>
    <span class=""refresh-indicator"" id=""refreshIndicator""></span>
  </div>
</div>

<div class=""summary"" id=""summaryContainer""></div>

<div class=""charts-row"">
  <div class=""chart-card"">
    <h3>Tokens by Provider</h3>
    <canvas id=""donutChart""></canvas>
  </div>
  <div class=""chart-card"">
    <h3>Requests by Provider</h3>
    <canvas id=""barChart""></canvas>
  </div>
</div>

<div class=""charts-row"">
  <div class=""chart-card full-width"">
    <h3>Token Consumption (Last 24h)</h3>
    <canvas id=""lineChart""></canvas>
  </div>
</div>

<h2 style=""font-size:16px;font-weight:600;margin-bottom:12px;color:var(--fg-muted);"">Providers</h2>
<div class=""providers-grid"" id=""providersContainer"">
  <div class=""loading"">Loading provider data...</div>
</div>

<div class=""modal-overlay"" id=""detailModal"">
  <div class=""modal"">
    <div class=""modal-header"">
      <h2 id=""modalTitle"">Provider Details</h2>
      <button class=""modal-close"" onclick=""closeModal()"">✕</button>
    </div>
    <div class=""detail-grid"" id=""modalDetails""></div>
    <div id=""billingSection"" style=""margin-top:16px;padding-top:16px;border-top:1px solid var(--border)"">
      <h3 style=""font-size:13px;font-weight:600;color:var(--fg-muted);margin-bottom:8px;"">Billing / Quota (from API)</h3>
      <div class=""detail-grid"" id=""modalBillingDetails""></div>
    </div>
    <div class=""gauge-section"">
      <canvas id=""gaugeChart"" style=""max-height:120px""></canvas>
    </div>
  </div>
</div>

<script>
let donutChartInstance = null;
let barChartInstance = null;
let lineChartInstance = null;
let gaugeChartInstance = null;
let billingCache = null;

const PROVIDER_COLORS = [
  '#58a6ff', '#3fb950', '#d29922', '#f85149', '#bc8cff',
  '#79c0ff', '#56d364', '#e3b341', '#ff7b72', '#d2a8ff',
  '#a5d6ff', '#7ee787', '#ffa657', '#ff6b6b', '#cda5ff'
];

async function fetchUsage() {
  try {
    const resp = await fetch('/api/usage');
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    return await resp.json();
  } catch (err) {
    console.error('Failed to fetch usage:', err);
    return null;
  }
}

async function fetchBilling() {
  try {
    const resp = await fetch('/api/billing');
    if (!resp.ok) return null;
    const data = await resp.json();
    billingCache = data;
    return data;
  } catch (err) {
    return null;
  }
}

function getBillingForProvider(name) {
  if (!billingCache) return null;
  return billingCache[name] || null;
}

function formatNumber(n) {
  if (n === undefined || n === null) return '0';
  if (n >= 1_000_000) return (n / 1_000_000).toFixed(1) + 'M';
  if (n >= 1_000) return (n / 1_000).toFixed(1) + 'K';
  return n.toLocaleString();
}

function timeAgo(iso) {
  if (!iso) return 'never';
  const sec = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
  if (sec < 60) return sec + 's ago';
  if (sec < 3600) return Math.floor(sec / 60) + 'm ago';
  if (sec < 86400) return Math.floor(sec / 3600) + 'h ago';
  return Math.floor(sec / 86400) + 'd ago';
}

function renderSummary(totals) {
  const container = document.getElementById('summaryContainer');
  container.innerHTML = `
    <div class=""summary-card"">
      <div class=""label"">Total Requests</div>
      <div class=""value purple"">${formatNumber(totals.total_requests)}</div>
    </div>
    <div class=""summary-card"">
      <div class=""label"">Prompt Tokens</div>
      <div class=""value orange"">${formatNumber(totals.total_prompt_tokens)}</div>
    </div>
    <div class=""summary-card"">
      <div class=""label"">Completion Tokens</div>
      <div class=""value green"">${formatNumber(totals.total_completion_tokens)}</div>
    </div>
    <div class=""summary-card"">
      <div class=""label"">Total Tokens</div>
      <div class=""value"">${formatNumber(totals.total_tokens)}</div>
    </div>
    <div class=""summary-card"">
      <div class=""label"">Estimated Cost (USD)</div>
      <div class=""value"">$${(totals.total_estimated_cost || 0).toFixed(4)}</div>
    </div>
  `;
}

function renderDonut(providers) {
  const canvas = document.getElementById('donutChart');
  const ctx = canvas.getContext('2d');
  if (donutChartInstance) donutChartInstance.destroy();

  const labels = providers.map(p => p.display_name);
  const data = providers.map(p => p.stats.total_tokens);
  const colors = providers.map((_, i) => PROVIDER_COLORS[i % PROVIDER_COLORS.length]);

  donutChartInstance = new Chart(ctx, {
    type: 'doughnut',
    data: { labels, datasets: [{ data, backgroundColor: colors, borderWidth: 0 }] },
    options: {
      responsive: true, maintainAspectRatio: true,
      plugins: { legend: { position: 'right', labels: { color: '#8b949e', font: { size: 11 } } } }
    }
  });
}

function renderBar(providers) {
  const canvas = document.getElementById('barChart');
  const ctx = canvas.getContext('2d');
  if (barChartInstance) barChartInstance.destroy();

  const labels = providers.map(p => p.display_name);
  const data = providers.map(p => p.stats.total_requests);
  const colors = providers.map((_, i) => PROVIDER_COLORS[i % PROVIDER_COLORS.length]);

  barChartInstance = new Chart(ctx, {
    type: 'bar',
    data: { labels, datasets: [{ label: 'Requests', data, backgroundColor: colors, borderRadius: 4 }] },
    options: {
      indexAxis: 'y', responsive: true, maintainAspectRatio: true,
      plugins: { legend: { display: false } },
      scales: {
        x: { ticks: { color: '#8b949e', font: { size: 11 } }, grid: { color: 'rgba(255,255,255,0.05)' } },
        y: { ticks: { color: '#8b949e', font: { size: 11 } }, grid: { display: false } }
      }
    }
  });
}

function renderLineChart(snapshots, providers) {
  const canvas = document.getElementById('lineChart');
  const ctx = canvas.getContext('2d');
  if (lineChartInstance) lineChartInstance.destroy();

  if (!snapshots || snapshots.length === 0) {
    ctx.font = '14px sans-serif';
    ctx.fillStyle = '#8b949e';
    ctx.textAlign = 'center';
    ctx.fillText('No historical data yet (snapshots every 60s)', canvas.width / 2, canvas.height / 2);
    return;
  }

  const labels = snapshots.map(s => {
    const d = new Date(s.timestamp);
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  });

  const datasets = [];
  const providerNames = [...new Set(providers.map(p => p.name))];

  providerNames.forEach((name, i) => {
    const display = providers.find(p => p.name === name)?.display_name || name;
    datasets.push({
      label: display,
      data: snapshots.map(s => s.provider_tokens[name] || 0),
      borderColor: PROVIDER_COLORS[i % PROVIDER_COLORS.length],
      backgroundColor: PROVIDER_COLORS[i % PROVIDER_COLORS.length] + '20',
      fill: true, tension: 0.3, pointRadius: 0, borderWidth: 2
    });
  });

  lineChartInstance = new Chart(ctx, {
    type: 'line', data: { labels, datasets },
    options: {
      responsive: true, maintainAspectRatio: true,
      plugins: { legend: { position: 'bottom', labels: { color: '#8b949e', font: { size: 11 } } } },
      scales: {
        x: { ticks: { color: '#8b949e', font: { size: 10 }, maxTicksLimit: 12 }, grid: { color: 'rgba(255,255,255,0.05)' } },
        y: { ticks: { color: '#8b949e', font: { size: 10 }, callback: v => formatNumber(v) }, grid: { color: 'rgba(255,255,255,0.05)' } }
      }
    }
  });
}

function renderProviderCards(providers) {
  const container = document.getElementById('providersContainer');
  if (!providers || providers.length === 0) {
    container.innerHTML = '<div class=""no-data"">No provider data available. Make a request first.</div>';
    return;
  }

  const maxTokens = Math.max(...providers.map(p => p.stats.total_tokens), 1);

  container.innerHTML = providers.map((p, idx) => {
    const s = p.stats;
    const isOk = s.last_request_success !== false;
    const hasData = s.total_requests > 0;
    const tokenPct = Math.min((s.total_tokens / maxTokens) * 100, 100);
    const barClass = tokenPct > 80 ? 'red' : tokenPct > 50 ? 'orange' : 'green';

    // Check billing cache for this provider
    const bill = getBillingForProvider(p.name);
    let billingHtml = '';
    if (bill && bill.primary_balance_display) {
      billingHtml = `<span class=""billing-badge"">${bill.primary_balance_display}</span>`;
    }

    return `
      <div class=""provider-card"" onclick=""openModal('${p.name}')"">
        <div class=""provider-header"">
          <span class=""provider-name"" style=""color:${PROVIDER_COLORS[idx % PROVIDER_COLORS.length]}"">
            ${p.display_name}${billingHtml}
          </span>
          <span class=""provider-status ${!hasData ? 'inactive' : isOk ? 'ok' : 'error'}"">
            ${!hasData ? 'No activity' : isOk ? 'OK' : 'Error'}
          </span>
        </div>
        <div class=""stat-row"">
          <span class=""stat-label"">Requests</span>
          <span class=""stat-value"">${formatNumber(s.total_requests)}</span>
        </div>
        <div class=""stat-row"">
          <span class=""stat-label"">Tokens</span>
          <span class=""stat-value"">${formatNumber(s.total_tokens)}</span>
        </div>
        <div class=""progress-bar"">
          <div class=""fill ${barClass}"" style=""width:${tokenPct}%""></div>
        </div>
        <div class=""stat-row"">
          <span class=""stat-label"">Last request</span>
          <span class=""stat-value"">${timeAgo(s.last_request_time)}</span>
        </div>
        ${s.rate_limit_remaining_requests !== null ? `
          <div class=""stat-row"">
            <span class=""stat-label"">Rate limit remaining</span>
            <span class=""stat-value"">${s.rate_limit_remaining_requests} / ${s.rate_limit_requests ?? '?'}</span>
          </div>
        ` : ''}
        ${s.average_latency_ms ? `
          <div class=""stat-row"">
            <span class=""stat-label"">Avg latency</span>
            <span class=""stat-value"">${s.average_latency_ms.toFixed(0)}ms</span>
          </div>
        ` : ''}
        ${s.current_rpm ? `
          <div class=""stat-row"">
            <span class=""stat-label"">RPM</span>
            <span class=""stat-value"">${s.current_rpm} req/min</span>
          </div>
        ` : ''}
        ${s.total_estimated_cost > 0 ? `
          <div class=""stat-row"">
            <span class=""stat-label"">Est. cost</span>
            <span class=""stat-value"">$${s.total_estimated_cost.toFixed(6)}</span>
          </div>
        ` : ''}
        ${(s.credits_remaining !== null && s.credits_remaining !== undefined) ? `
          <div class=""stat-row"">
            <span class=""stat-label"">Credits</span>
            <span class=""stat-value"">${Number(s.credits_remaining).toFixed(4)}</span>
          </div>
        ` : ''}
        ${s.last_error_message ? `
          <div class=""stat-row"" style=""color:var(--red)"">
            <span class=""stat-label"">Error</span>
            <span class=""stat-value"">${s.last_error_message}</span>
          </div>
        ` : ''}
      </div>
    `;
  }).join('');
}

function openModal(providerName) {
  const overlay = document.getElementById('detailModal');
  document.getElementById('modalTitle').textContent = `Provider: ${providerName}`;
  overlay.classList.add('active');

  fetch(`/api/usage/${encodeURIComponent(providerName)}`)
    .then(r => r.json())
    .then(data => {
      const s = data.stats;
      document.getElementById('modalDetails').innerHTML = `
        <div class=""detail-item"">
          <div class=""label"">Requests</div>
          <div class=""value"">${formatNumber(s.total_requests)}</div>
        </div>
        <div class=""detail-item"">
          <div class=""label"">Prompt tokens</div>
          <div class=""value"">${formatNumber(s.total_prompt_tokens)}</div>
        </div>
        <div class=""detail-item"">
          <div class=""label"">Completion tokens</div>
          <div class=""value"">${formatNumber(s.total_completion_tokens)}</div>
        </div>
        <div class=""detail-item"">
          <div class=""label"">Total tokens</div>
          <div class=""value"">${formatNumber(s.total_tokens)}</div>
        </div>
        <div class=""detail-item"">
          <div class=""label"">Last request</div>
          <div class=""value"">${timeAgo(s.last_request_time)}</div>
        </div>
        <div class=""detail-item"">
          <div class=""label"">Status</div>
          <div class=""value"" style=""color:${s.last_request_success !== false ? 'var(--green)' : 'var(--red)'}"">
            ${s.last_request_success !== false ? 'OK' : 'Error'}
          </div>
        </div>
        ${s.rate_limit_requests !== null ? `
        <div class=""detail-item"">
          <div class=""label"">Rate limit (requests)</div>
          <div class=""value"">${s.rate_limit_remaining_requests ?? '?'} / ${s.rate_limit_requests}</div>
        </div>
        <div class=""detail-item"">
          <div class=""label"">Rate limit (tokens)</div>
          <div class=""value"">${s.rate_limit_remaining_tokens ?? '?'} / ${s.rate_limit_tokens ?? '?'}</div>
        </div>
        ` : ''}
        ${(s.credits_remaining !== null && s.credits_remaining !== undefined) ? `
        <div class=""detail-item"">
          <div class=""label"">Credits remaining</div>
          <div class=""value"">${Number(s.credits_remaining).toFixed(6)}</div>
        </div>
        ` : ''}
        ${s.last_error_message ? `
        <div class=""detail-item"">
          <div class=""label"">Last error</div>
          <div class=""value"" style=""color:var(--red)"">${s.last_error_message}</div>
        </div>
        ` : ''}
      `;

      // Gauge
      if (s.rate_limit_requests && s.rate_limit_requests > 0) {
        const pct = s.rate_limit_remaining_requests !== null
          ? (s.rate_limit_remaining_requests / s.rate_limit_requests) * 100 : 100;
        renderGauge(pct);
      }
    })
    .catch(err => {
      document.getElementById('modalDetails').innerHTML = `<div class=""error-state"">Error: ${err.message}</div>`;
    });

  // Fetch billing detail for this provider
  fetch(`/api/billing/${encodeURIComponent(providerName)}`)
    .then(r => r.json())
    .then(bill => {
      if (!bill || bill.error) {
        document.getElementById('modalBillingDetails').innerHTML = '<div class=""no-data"">No billing API for this provider</div>';
        return;
      }
      let html = '';
      if (bill.balances && bill.balances.length > 0) {
        bill.balances.forEach(b => {
          html += `<div class=""detail-item""><div class=""label"">Balance (${b.currency})</div><div class=""value"">Total: ${b.total_balance} | Granted: ${b.granted_balance} | Topped: ${b.topped_up_balance}</div></div>`;
        });
      }
      if (bill.total_remaining > 0) {
        html += `<div class=""detail-item""><div class=""label"">Credits</div><div class=""value"">${bill.total_remaining} remaining (${bill.total_used} used of ${bill.total_granted})</div></div>`;
      }
      if (bill.hard_limit_usd > 0) {
        html += `<div class=""detail-item""><div class=""label"">Spend limit</div><div class=""value"">$${bill.hard_limit_usd} hard / $${bill.soft_limit_usd} soft</div></div>`;
      }
      if (bill.credits_limit > 0) {
        html += `<div class=""detail-item""><div class=""label"">Credits</div><div class=""value"">${bill.credits_used} / ${bill.credits_limit} used${bill.is_free_tier ? ' (Free tier)' : ''}</div></div>`;
      }
      if (bill.label) {
        html += `<div class=""detail-item""><div class=""label"">Key label</div><div class=""value"">${bill.label}</div></div>`;
      }
      if (bill.notes) {
        html += `<div class=""detail-item""><div class=""label"">Notes</div><div class=""value"" style=""color:var(--fg-muted);font-size:12px;font-weight:400;padding-top:2px;"">${bill.notes}</div></div>`;
      }
      if (!html) html = '<div class=""no-data"">No billing data available</div>';
      document.getElementById('modalBillingDetails').innerHTML = html;
    })
    .catch(() => {
      document.getElementById('modalBillingDetails').innerHTML = '<div class=""no-data"">No billing data available</div>';
    });
}

function closeModal() {
  document.getElementById('detailModal').classList.remove('active');
  if (gaugeChartInstance) { gaugeChartInstance.destroy(); gaugeChartInstance = null; }
}

function renderGauge(percentage) {
  const canvas = document.getElementById('gaugeChart');
  const ctx = canvas.getContext('2d');
  if (gaugeChartInstance) gaugeChartInstance.destroy();
  const color = percentage > 50 ? '#3fb950' : percentage > 20 ? '#d29922' : '#f85149';

  gaugeChartInstance = new Chart(ctx, {
    type: 'doughnut',
    data: { datasets: [{ data: [percentage, 100 - percentage], backgroundColor: [color, 'rgba(255,255,255,0.08)'], borderWidth: 0, circumference: 180, rotation: 270 }] },
    options: { responsive: true, maintainAspectRatio: true, cutout: '75%', plugins: { legend: { display: false }, tooltip: { callbacks: { label: () => `${percentage.toFixed(1)}% remaining` } } } },
    plugins: [{
      id: 'gaugeCenterText',
      afterDraw(chart) {
        const { ctx, chartArea } = chart;
        ctx.save();
        ctx.fillStyle = '#e6edf3';
        ctx.font = 'bold 20px sans-serif';
        ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
        ctx.fillText(`${percentage.toFixed(0)}%`, chartArea.left + chartArea.width / 2, chartArea.top + chartArea.height / 2 + 5);
        ctx.fillStyle = '#8b949e';
        ctx.font = '11px sans-serif';
        ctx.fillText('Rate limit remaining', chartArea.left + chartArea.width / 2, chartArea.top + chartArea.height / 2 + 28);
        ctx.restore();
      }
    }]
  });
}

document.getElementById('detailModal').addEventListener('click', (e) => {
  if (e.target === e.currentTarget) closeModal();
});

async function render() {
  const data = await fetchUsage();
  if (!data) {
    document.getElementById('providersContainer').innerHTML = '<div class=""error-state"">Failed to load usage data.</div>';
    return;
  }

  document.getElementById('lastUpdated').textContent = 'Updated: ' + new Date().toLocaleTimeString();

  // Render provider cards FIRST (most important — shows provider list).
  // This must execute before chart functions that depend on Chart.js CDN.
  renderProviderCards(data.providers);
  fetchBilling().then(() => { renderProviderCards(data.providers); }).catch(() => {});

  // Charts are secondary — wrap in try/catch in case Chart.js CDN fails.
  renderSummary(data.totals);
  try { renderDonut(data.providers); } catch (e) { console.warn('Donut chart failed:', e); }
  try { renderBar(data.providers); } catch (e) { console.warn('Bar chart failed:', e); }
  try { renderLineChart(data.snapshots, data.providers); } catch (e) { console.warn('Line chart failed:', e); }
}

// Wait for DOM and Chart.js CDN before first render
setTimeout(() => { render(); }, 500);
setInterval(render, 10000);
</script>
</body>
</html>";
}