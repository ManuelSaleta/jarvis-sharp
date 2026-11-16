# 🤖 Jarvis.Sharp (`jarvis-sharp`)

[![CI](https://github.com/ManuelSaleta/jarvis-sharp/actions/workflows/ci.yml/badge.svg)](https://github.com/ManuelSaleta/jarvis-sharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](https://github.com/ManuelSaleta/jarvis-sharp/blob/main/LICENSE)
[![.NET Compatibility](https://img.shields.io/badge/.NET-%3E%3D%2010.0-512BD4.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![Container Size](https://img.shields.io/badge/container%20size-48.5%20MB-success?style=flat-square)](https://ghcr.io/manuelsaleta/jarvis-sharp)

**Jarvis.Sharp** is the high-performance, containerized .NET 10 implementation of the Homelab Slack Socket Mode Gateway, reactive alerting coordinator, and autonomous remediation engine.

It runs inside the Kubernetes cluster (`monitoring` namespace), maintaining zero open inbound router ports via an outbound persistent WebSocket to Slack's Edge API.

---

## 🎯 Architecture

```mermaid
flowchart TD
    subgraph SlackCloud["Slack Edge API"]
        User["Homelab Admin"]
        AlertsChannel["#homelab-alerts"]
        OpsChannel["#homelab-ops"]
    end

    subgraph Cluster["K3s Homelab Cluster"]
        subgraph JarvisSharp["jarvis-sharp (48.5 MB non-root container)"]
            SocketMode["SlackNet Socket Mode Listener"]
            AuthGuard["AuthorizationGuard (Fail-Closed)"]
            RateLimit["RateLimiter (Sliding Window)"]
            StateStore["CandidateStateStore (24h TTL)"]
            K8sDiag["KubernetesDiagnostics (Native k8s SDK)"]
            MediaDispatcher["MediaSyncDispatcher (HMAC-SHA256)"]
            PlexService["PlexService (Library Deduplication)"]
        end

        Grafana["Grafana Unified Alertmanager"]
        MediaSync["media-sync (Sync.Sharp)"]
        Plex["Plex Media Server"]
        Workloads["Cluster Workload Pods"]
    end

    SocketMode <-->|"Outbound WSS (xapp-...)"| SlackCloud
    User -->|"/download 1 3" / "/agy status"| SocketMode
    AlertsChannel -->|"Intercept Firing Alerts"| SocketMode
    SocketMode --> AuthGuard --> RateLimit

    SocketMode -->|"Pod Logs & Rollout Restarts"| K8sDiag --> Workloads
    SocketMode -->|"Plex Duplicate Check"| PlexService <--> Plex
    SocketMode -->|"Dispatch Approved Media"| MediaDispatcher -->|"HMAC-Signed Webhook"| MediaSync
    Grafana -->|"Webhook Alert"| AlertsChannel
```

---

## 🛡️ Core Features

1. **Zero Inbound Attack Surface**: Connects exclusively via Slack Socket Mode (`wss://wss-primary.slack.com`). No ports forwarded on your router.
2. **Fail-Closed Authorization**: Enforces strict `USER_WHITELIST` check on every incoming command, slash action, or button interaction. Unauthorized users receive ephemeral `⛔` denials.
3. **Plex Pre-Acquisition Deduplication**: Queries Plex REST API (`/search?query=...`) and local library inventory to skip titles that already exist in your library.
4. **Cryptographic Inter-Service Security**: Dispatches HMAC-SHA256 signed requests to `media-sync` (`Sync.Sharp`) with constant-time signature verification.
5. **Reactive Alert Triage**: Intercepts Grafana alerts in `#homelab-alerts`, executes non-destructive pod diagnostics, and renders interactive Block Kit buttons:
   - `[🔄 Restart Deployment]`: Triggers native Kubernetes rollout restarts.
   - `[📜 View Full Logs]`: Pulls stdout/stderr directly into the Slack thread.
   - `[❌ Dismiss]`: Acknowledges and dismisses triage.
6. **Ultra-Minimal Footprint**: Multi-stage build targeting `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled` (non-root UID 1654, **48.5 MB total image size**, under ~35 MB idle RAM).

---

## 🔧 Environment Variables

| Variable | Description | Default / Example |
| :--- | :--- | :--- |
| `SLACK_APP_TOKEN` | Slack App-level token (`xapp-...`) with `connections:write` | Required |
| `SLACK_BOT_TOKEN` | Slack Bot OAuth token (`xoxb-...`) with `chat:write` | Required |
| `USER_WHITELIST` | Comma-separated list of authorized Slack User IDs | Required (Fail-closed) |
| `SLACK_ALERTS_CHANNEL` | Slack channel ID for alert interception (e.g. `C0CHSUZCF0B`) | Optional |
| `MEDIA_SYNC_WEBHOOK_URL`| In-cluster webhook URL for `media-sync` | `http://media-sync-service.media.svc.cluster.local:30080/api/v1/sync/trigger` |
| `WEBHOOK_SECRET_TOKEN` | Shared 32-byte secret for HMAC-SHA256 signing | Required |
| `GRAFANA_URL` | Base URL for Grafana Ingress | `http://promstack-grafana.monitoring.svc.cluster.local` |
| `GRAFANA_TOKEN` | Read-only Grafana Service Account Bearer Token | Optional |
| `GRAFANA_LOKI_DATASOURCE_UID`| Loki datasource UID in Grafana | `loki` |
| `PLEX_URL` | Local Plex Media Server endpoint | `http://192.168.1.210:32400` |
| `PLEX_TOKEN` | Plex API authentication token | Optional |
| `CANDIDATE_STATE_PATH` | Path to candidate batch JSON persistence file | `/state/candidate-state.json` |

---

## 🚀 Building & Testing

### Local Development (.NET 10 SDK)
```bash
# Build solution
dotnet build Jarvis.Sharp.slnx

# Run unit test suite (14 tests)
dotnet test Jarvis.Sharp.slnx

# Run daemon locally
dotnet run --project src/Jarvis.Sharp/Jarvis.Sharp.csproj
```

### Docker Multi-Stage Container Build
```bash
docker build -t ghcr.io/manuelsaleta/jarvis-sharp:latest .
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
