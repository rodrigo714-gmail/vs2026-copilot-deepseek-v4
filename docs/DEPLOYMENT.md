# Deployment Guide

Production deployment and operational procedures for the multi-provider AI proxy.

## Table of Contents

- [Deployment Methods](#deployment-methods)
- [Docker Deployment](#docker-deployment)
- [Bare Metal Deployment](#bare-metal-deployment)
- [Environment Configuration](#environment-configuration)
- [Health Checks](#health-checks)
- [Monitoring](#monitoring)
- [Troubleshooting](#troubleshooting)

---

## Deployment Methods

### Recommended: Docker Compose

Best for rapid deployment with all dependencies pre-configured.

**Pros:**
- Isolated environment
- Easy upgrades and rollbacks
- Built-in networking
- Volume-based state management

**Cons:**
- Requires Docker engine

### Alternative: Bare Metal

For direct binary execution on Linux/Windows/macOS.

**Pros:**
- Full control over runtime
- No container overhead
- Native performance monitoring

**Cons:**
- Manual dependency management
- Platform-specific setup

---

## Docker Deployment

### Quick Start

```bash
# 1. Clone or navigate to repo
cd /path/to/vs2026-copilot-deepseek-v4

# 2. Create .env file
cp .env.example .env
# Edit .env to add API keys

# 3. Start proxy
docker compose up -d

# 4. Verify
curl http://localhost:11434/health
```

### Docker Compose File

The included `docker-compose.yml`:

```yaml
version: '3.8'

services:
  proxy:
    build: .
    ports:
      - "11434:11434"
    environment:
      # Load from .env file
      PROVIDER_DEEPSEEK_API_KEY: ${PROVIDER_DEEPSEEK_API_KEY}
      PROVIDER_OPENAI_API_KEY: ${PROVIDER_OPENAI_API_KEY}
      PROVIDER_NVIDIA_API_KEY: ${PROVIDER_NVIDIA_API_KEY}
      PROVIDER_GROQ_API_KEY: ${PROVIDER_GROQ_API_KEY}
      PROVIDER_OPENROUTER_API_KEY: ${PROVIDER_OPENROUTER_API_KEY}
      PROVIDER_OLLAMACLOUD_API_KEY: ${PROVIDER_OLLAMACLOUD_API_KEY}
      PROVIDER_MOONSHOT_API_KEY: ${PROVIDER_MOONSHOT_API_KEY}
      PROVIDER_CEREBRAS_API_KEY: ${PROVIDER_CEREBRAS_API_KEY}

      PROXY_PORT: 11434
      LOG_LEVEL: Information
      REQUEST_TIMEOUT: 300
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:11434/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 5s
```

### Docker Image

Multi-stage build produces minimal footprint:

```dockerfile
# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Stage 2: Runtime (chiseled = ~40 MB)
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 11434
ENTRYPOINT ["dotnet", "vs2026-copilot-deepseek-v4.dll"]
```

**Image size:** ~150 MB (runtime + proxy binary)  
**Runtime:** ~50 MB memory at startup

### Docker Commands

```bash
# Start in background
docker compose up -d

# View logs
docker compose logs -f

# Stop
docker compose down

# Rebuild image
docker compose build --no-cache

# Access shell in container
docker compose exec proxy sh

# Check container health
docker compose ps
```

### Multi-Host Deployment

For high availability across multiple servers:

```yaml
version: '3.8'

services:
  proxy-1:
    build: .
    ports:
      - "11434:11434"
    environment:
      - PROXY_PORT=11434
      - ${env_file:-.env}
    deploy:
      replicas: 1

  proxy-2:
    build: .
    ports:
      - "11435:11435"
    environment:
      - PROXY_PORT=11435
      - ${env_file:-.env}
```

Use a load balancer (nginx, HAProxy) to distribute traffic:

```nginx
upstream proxy {
    server localhost:11434;
    server localhost:11435;
}

server {
    listen 11434;

    location / {
        proxy_pass http://proxy;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
    }
}
```

---

## Bare Metal Deployment

### Prerequisites

- .NET 10 SDK or runtime
- API keys for providers
- Port 11434 available (or configurable)

### Setup

```bash
# 1. Clone repository
git clone https://github.com/rodrigo714-gmail/vs2026-copilot-deepseek-v4.git
cd vs2026-copilot-deepseek-v4

# 2. Create .env file
cp .env.example .env
nano .env  # Set PROVIDER_* keys

# 3. Run
dotnet run --configuration Release

# Or publish as self-contained
dotnet publish -c Release --self-contained
./<output>/vs2026-copilot-deepseek-v4.exe
```

### Systemd Service (Linux)

Create `/etc/systemd/system/proxy.service`:

```ini
[Unit]
Description=Multi-Provider AI Proxy
After=network.target

[Service]
Type=simple
User=proxy
WorkingDirectory=/opt/proxy
ExecStart=/usr/bin/dotnet /opt/proxy/vs2026-copilot-deepseek-v4.dll
Restart=on-failure
RestartSec=5s
StandardOutput=journal
StandardError=journal

Environment="PROXY_PORT=11434"
EnvironmentFile=/opt/proxy/.env

[Install]
WantedBy=multi-user.target
```

Enable and start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable proxy
sudo systemctl start proxy
sudo systemctl status proxy
journalctl -u proxy -f  # View logs
```

### Windows Service

With `NSSM` (Non-Sucking Service Manager):

```bash
# Install
nssm install proxy "C:\Program Files\dotnet\dotnet.exe" "C:\proxy\vs2026-copilot-deepseek-v4.dll"

# Set working directory
nssm set proxy AppDirectory "C:\proxy"

# Set environment
nssm set proxy AppEnvironmentExtra PROXY_PORT=11434

# Start
nssm start proxy

# View status
nssm status proxy
```

---

## Environment Configuration

### Required Variables

```bash
# At least one provider API key must be set
PROVIDER_DEEPSEEK_API_KEY=sk-...
# Or any combination of:
PROVIDER_OPENAI_API_KEY=sk-proj-...
PROVIDER_NVIDIA_API_KEY=nvapi-...
PROVIDER_GROQ_API_KEY=gsk-...
PROVIDER_OPENROUTER_API_KEY=sk-or-...
PROVIDER_OLLAMACLOUD_API_KEY=...
PROVIDER_MOONSHOT_API_KEY=sk-...
PROVIDER_CEREBRAS_API_KEY=csk-...
```

> The Moonshot and Cerebras providers were added in 2026-Q2; their env-var prefix is
> `PROVIDER_MOONSHOT_API_KEY` and `PROVIDER_CEREBRAS_API_KEY` respectively. The proxy
> automatically discovers a provider as long as its key is set — see
> `Services/ProviderRegistry.cs` → `DiscoverProviders()` for the order.

### Optional Variables

```bash
# Proxy settings
PROXY_PORT=11434                       # Default: 11434
DEFAULT_MODEL=deepseek-v4-pro          # Default: deepseek-v4-pro
PROXY_API_KEY=secret-key-here          # If set, requires Bearer auth
LOG_LEVEL=Information                  # Debug, Information, Warning, Error

# Performance
REQUEST_TIMEOUT=300                    # Seconds, default: 300
MAX_CONCURRENT_REQUESTS=1000           # No limit if not set
STREAM_CHUNK_SIZE=4096                 # Bytes, default: 4096
```

### Loading Environment Variables

Priority order (highest to lowest):

1. **System environment variables** (Windows: System Properties, Linux: `export`)
2. **.env file** (in working directory)
3. **appsettings.json** (project defaults)

Example `.env`:
```bash
# Required: at least one provider key
PROVIDER_DEEPSEEK_API_KEY=sk-abc123def456...
PROVIDER_OPENAI_API_KEY=sk-proj-xyz789...
PROVIDER_NVIDIA_API_KEY=nvapi-...
PROVIDER_GROQ_API_KEY=gsk-...
PROVIDER_OPENROUTER_API_KEY=sk-or-...
PROVIDER_OLLAMACLOUD_API_KEY=...
PROVIDER_MOONSHOT_API_KEY=sk-...
PROVIDER_CEREBRAS_API_KEY=csk-...

# Optional proxy settings
PROXY_PORT=11434
LOG_LEVEL=Information
DEFAULT_MODEL=deepseek-v4-pro
```

---

## Health Checks

### Endpoint: GET /health

Returns proxy status and available providers:

```bash
curl http://localhost:11434/health
```

Response:
```json
{
  "status": "ok",
  "providers": ["deepseek", "openai", "nvidia", "openrouter", "groq", "ollama", "moonshot", "cerebras"],
  "availableModels": [
    "deepseek-v4-pro",
    "deepseek-v4-flash",
    "gpt-5",
    "qwen/qwen3-coder-480b-a35b-instruct",
    "kimi-k2.6",
"zai-glm-4.7",
    "qwen3-coder:480b",
    "kimi2.7-code",
    "glm-5.2",
    "minimax-m3",
    "... (up to 9 enabled per provider, ~45 total)"
  ],
  "defaultModel": "deepseek-v4-pro"
}
```

### Docker Health Check

Built into `docker-compose.yml`:

```yaml
healthcheck:
  test: ["CMD", "curl", "-f", "http://localhost:11434/health"]
  interval: 30s
  timeout: 10s
  retries: 3
  start_period: 5s
```

Check status:
```bash
docker compose ps  # Shows health status
```

### Systemd Health

Monitor service:
```bash
systemctl status proxy
journalctl -u proxy -n 50  # Last 50 lines
```

---

## Monitoring

### Metrics to Track

1. **Request Latency**
   - Proxy overhead (should be <10ms)
   - Upstream provider latency

2. **Throughput**
   - Requests per second
   - Concurrent connections

3. **Error Rate**
   - Failed requests (4xx, 5xx)
   - Timeout rate

4. **Provider Health**
   - API key validity
   - Rate limit status
   - Service availability

### Logging

Set `LOG_LEVEL` environment variable:

```bash
# Debug (verbose)
LOG_LEVEL=Debug

# Information (default)
LOG_LEVEL=Information

# Warning
LOG_LEVEL=Warning

# Error
LOG_LEVEL=Error
```

### Application Insights / Monitoring

To integrate with Azure Application Insights:

1. Add NuGet package:
```bash
dotnet add package Microsoft.ApplicationInsights.AspNetCore
```

2. In `Program.cs`:
```csharp
builder.Services.AddApplicationInsightsTelemetry();
```

3. Set environment variable:
```bash
APPLICATIONINSIGHTS_CONNECTION_STRING=InstrumentationKey=...
```

### Example: Prometheus Metrics

To add Prometheus scraping:

```bash
dotnet add package prometheus-net.AspNetCore
```

In `Program.cs`:
```csharp
app.UseHttpMetrics();
app.MapMetrics();  // /metrics endpoint
```

Prometheus config:
```yaml
scrape_configs:
  - job_name: 'proxy'
    static_configs:
      - targets: ['localhost:11434']
    metrics_path: '/metrics'
```

---

## Troubleshooting

### Container Won't Start

```bash
# View logs
docker compose logs proxy

# Check image exists
docker images | grep proxy

# Rebuild
docker compose build --no-cache
docker compose up -d
```

### Port Already in Use

```bash
# Linux: Find process using port 11434
lsof -i :11434

# Windows (PowerShell)
netstat -ano | findstr :11434

# Change port
PROXY_PORT=11435 docker compose up -d
```

### API Key Issues

```bash
# Verify environment variable is set
echo $PROVIDER_DEEPSEEK_API_KEY  # Linux/macOS
echo %PROVIDER_DEEPSEEK_API_KEY%  # Windows

# Test API key directly
curl -X POST https://api.deepseek.com/v1/chat/completions \
  -H "Authorization: Bearer sk-..." \
  -d '{"model":"deepseek-v4-pro","messages":[...]}'
```

### Slow Responses

1. Check provider latency:
```bash
curl -X POST http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{...}' \
  -w "\nProxy latency: %{time_total}s\n"
```

2. Check request timeout:
```bash
# Increase REQUEST_TIMEOUT
REQUEST_TIMEOUT=600 docker compose up -d
```

3. Check model availability:
```bash
curl http://localhost:11434/v1/models
```

### Memory Usage

Monitor container memory:
```bash
docker compose stats

# Limit memory (if needed)
# Add to docker-compose.yml:
services:
  proxy:
    mem_limit: 512m
    memswap_limit: 512m
```

### Connection Refused

1. Verify proxy is running:
```bash
docker compose ps
systemctl status proxy
```

2. Verify port binding:
```bash
docker compose logs proxy | grep "listening"
netstat -an | grep 11434
```

3. Check firewall:
```bash
firewall-cmd --add-port=11434/tcp --permanent
firewall-cmd --reload
```

---

## Backup and Recovery

### Configuration Backup

```bash
# Backup .env file (IMPORTANT!)
cp .env .env.backup

# Backup docker volumes
docker compose exec proxy tar czf backup.tar.gz /app
```

### Disaster Recovery

```bash
# Restore from backup
docker compose down
docker volume rm <volumes>
cp .env.backup .env
docker compose up -d
```

---

## Upgrade Procedure

### Docker

```bash
# Pull latest code
git pull

# Rebuild image
docker compose build --no-cache

# Stop old container
docker compose down

# Start new container
docker compose up -d

# Verify
curl http://localhost:11434/health
```

### Bare Metal

```bash
# Backup current binary
cp vs2026-copilot-deepseek-v4 vs2026-copilot-deepseek-v4.old

# Stop service
systemctl stop proxy

# Pull latest code
git pull

# Rebuild
dotnet publish -c Release -o /opt/proxy

# Start service
systemctl start proxy
systemctl status proxy
```

---

## Performance Tuning

### Connection Pool

Adjust in `Program.cs`:
```csharp
services.ConfigureHttpClientDefaults(http =>
{
    http.ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(...);
    }).ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 512,  // Increase if needed
        });
});
```

### Request Timeout

```bash
REQUEST_TIMEOUT=600  # Increase for slow providers
```

### Rate Limiting

Implement with `AspNetCore.RateLimiting`:
```csharp
app.UseRateLimiter();  // Requires policy setup
```

---

## Related Documentation

- [API.md](API.md) — Endpoint reference
- [CONFIGURATION.md](CONFIGURATION.md) — Configuration options
- [TESTING.md](TESTING.md) — Testing deployment setup
- [ARCHITECTURE.md](ARCHITECTURE.md) — System design
