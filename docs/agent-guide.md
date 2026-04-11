# Agent / Skill Guide: WhatsApp Message Sender — Further Improvements

This document is aimed at an AI coding agent or a developer who wants to extend
or harden the **WhatsApp Message Sender** service.  Each section describes a
recommended improvement, why it matters, and enough implementation detail to
get started.

---

## Priority 1 — Observability & Operations

### 1.1 Structured Logging (Serilog)

**Why:** `Console.WriteLine` cannot be filtered by level, formatted as JSON, or
forwarded to log aggregators.

**How:**
- Add `Serilog.Extensions.Hosting` and `Serilog.Sinks.Console` / `Serilog.Sinks.File`.
- Replace `Console.WriteLine` calls with `ILogger<T>` injected via DI.
- Emit structured properties: `{MessageId}`, `{StreamName}`, `{RetryCount}`.

### 1.2 Prometheus Metrics

**Why:** Without metrics, it is impossible to alert on queue depth, error rates,
or processing latency in production.

**How:**
- Add `prometheus-net` NuGet package.
- Expose an HTTP `/metrics` endpoint with `MetricServer`.
- Track:
  - `messages_processed_total` (counter, labels: stream, status)
  - `messages_pending` (gauge, label: stream)
  - `message_processing_duration_seconds` (histogram)
  - `retries_scheduled_total` (counter, label: stream)
  - `dead_letters_total` (counter, label: stream)

### 1.3 Health Checks

**Why:** Kubernetes / Docker orchestrators need a `/healthz` endpoint.

**How:**
- Add `Microsoft.Extensions.Diagnostics.HealthChecks`.
- Register checks:
  - Redis connectivity (`PING`).
  - ChromeDriver / WhatsApp Web session alive.
  - Azure Blob Storage reachability.
- Expose HTTP endpoint via `AspNetCore.HealthChecks`.

---

## Priority 2 — Reliability & Scalability

### 2.1 Circuit Breaker for WhatsApp Web

**Why:** If WhatsApp Web is temporarily unavailable (QR scan expired, rate limit),
every message will fail and fill up the retry sorted set.

**How:**
- Add `Polly` NuGet package.
- Wrap `WhatsAppService.SendMessageAsync` with a circuit breaker policy
  (open after 5 consecutive failures, half-open after 60 s).
- When the circuit is open, immediately schedule retries without attempting
  a browser call.

### 2.2 Horizontal Scaling

**Why:** A single process is a single point of failure; Redis consumer groups
natively support multiple competing consumers.

**How:**
- Set `ConsumerName` to a unique value per instance (e.g. `{MachineName}-{PID}`
  or inject via environment variable `CONSUMER_NAME`).
- Run multiple instances; each will receive a share of the stream messages.
- Ensure `MaxConcurrentCalls` per instance × number of instances does not
  overwhelm WhatsApp Web rate limits.

### 2.3 Graceful Shutdown

**Why:** The current `Console.ReadKey()` blocks indefinitely and ignores SIGTERM
(Docker/Kubernetes graceful shutdown signal).

**How:**
- Replace `Console.ReadKey()` with a hosted `IHostedService` that calls
  `processor.CloseAsync()` in `StopAsync`.
- Register `CancellationToken` from `IHostApplicationLifetime.ApplicationStopping`.

```csharp
// In a BackgroundService subclass:
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _processor.StartProcessing();
    await Task.Delay(Timeout.Infinite, stoppingToken);
}

public override async Task StopAsync(CancellationToken cancellationToken)
{
    await _processor.CloseAsync();
    await base.StopAsync(cancellationToken);
}
```

### 2.4 Message Deduplication

**Why:** At-least-once delivery may cause the same message to be processed
more than once (e.g., after a crash between ACK and status-API call).

**How:**
- Store processed `message_name` values in a Redis Set with a TTL of 24 h.
- Before processing: `SISMEMBER processed:<stream> <message_name>`.
- After successful send: `SADD processed:<stream> <message_name>` + `EXPIRE`.

### 2.5 Batch Processing

**Why:** If messages arrive faster than WhatsApp Web can process them,
the stream depth grows unboundedly.

**How:**
- Increase `MaxConcurrentCalls` (with care for WhatsApp rate limits).
- Consider pre-sorting messages by phone number to coalesce multiple messages
  to the same recipient into one browser session.

---

## Priority 3 — Feature Enhancements

### 3.1 Multi-Channel Support (Email, SMS)

**Why:** The architecture already routes by `message_type`; adding new channels
requires only a new handler.

**How:**
- Define an `IMessageSender` interface with `SendAsync(WhatsAppMessage msg)`.
- Create `EmailSender`, `SmsSender` implementations.
- Register in DI and inject into `RedisStreamProcessor`.
- Route in `HandleMessageAsync` based on `messageType`.

### 3.2 Replace Selenium with WhatsApp Business API

**Why:** Selenium automation is fragile (DOM changes break selectors), requires
a Chrome session, and violates WhatsApp ToS for automated bulk messaging.

**How:**
- Register for [WhatsApp Business API](https://developers.facebook.com/docs/whatsapp).
- Replace `WhatsAppService` with an HTTP client that calls the WABA endpoint.
- Remove Selenium / ChromeDriver dependencies from the project.

### 3.3 Message Templating

**Why:** Structured templates improve deliverability and enable WhatsApp's
native template messages (required for outbound messages after 24 h).

**How:**
- Add a `TemplateId` field to `WhatsAppMessage`.
- Resolve the template body from a local dictionary or Frappe API call.
- Pass resolved variables to the WABA API (if switching away from Selenium).

---

## Priority 4 — Security

### 4.1 Secret Management

**Why:** Connection strings and API tokens in `appsettings.json` are insecure
if the file is committed to version control.

**How:**
- Move secrets to environment variables or Azure Key Vault / HashiCorp Vault.
- Use `Microsoft.Extensions.Configuration.UserSecrets` for local development.
- Add `appsettings.json` entries with placeholder values only:
  ```json
  { "Redis": { "ConnectionString": "" } }
  ```
- Read real values from env vars: `REDIS__CONNECTIONSTRING`, etc.

### 4.2 Redis TLS

**Why:** Plain-text Redis connections expose message content.

**How:**
- Add `ssl=true` to the Redis connection string.
- Configure `ConfigurationOptions.SslProtocols`.

### 4.3 Least-Privilege Access

**Why:** The current Blob Storage connection string grants full account access.

**How:**
- Use a SAS token or Managed Identity scoped to the specific container(s).
- For Redis, use ACL rules to restrict this consumer to only the required streams.

---

## Priority 5 — Developer Experience

### 5.1 Docker / Docker Compose

**Why:** Reproducible local development without installing Redis, Chrome, etc.

**Recommended `docker-compose.yml` services:**
- `redis` (official image, version 7.x)
- `whatsapp-sender` (this application)
- `redis-commander` (optional UI for inspecting streams)

### 5.2 Integration Tests

**Why:** No automated tests currently exist; regressions can go undetected.

**How:**
- Use `Testcontainers` to spin up a Redis container per test run.
- Mock `WhatsAppService` and `BlobStorageService`.
- Test: new message → processed → XACK; failure → retry entry in sorted set;
  max retries → dead letter stream.

### 5.3 Dead-Letter Reprocessing Tool

**Why:** Operators need a way to inspect and re-queue dead-lettered messages
without writing raw Redis commands.

**How:**
- Create a small CLI tool or Frappe page that:
  - Lists entries in `stream-<tenant>:dead`.
  - Allows re-queuing selected entries back to `stream-<tenant>`.
  - Allows deleting entries permanently.

---

## Known Limitations (as of current implementation)

| Limitation                       | Details                                                                     |
|----------------------------------|-----------------------------------------------------------------------------|
| Single ChromeDriver instance     | All streams share one browser session; parallel sending is effectively serial |
| No XCLAIM / autoclaim for crash recovery | If the process crashes mid-processing, orphaned PEL messages are not reclaimed automatically (retry sorted set handles scheduled retries, but PEL cleanup is not implemented) |
| Redis 6.2+ required              | `XAUTOCLAIM` (used internally by StackExchange.Redis) requires Redis 6.2+   |
| No TLS on Redis                  | Should be enabled for production deployments                                |
| Polling instead of blocking read | `XREADGROUP BLOCK` is not used; 500 ms polling introduces slight latency    |
