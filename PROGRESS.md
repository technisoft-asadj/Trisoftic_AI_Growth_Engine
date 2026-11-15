# Progress log

Last updated: 2026-11-15 (overnight autonomous run). Source of truth for scope/order: [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

## Status by work package

| WP | What | Status |
|---|---|---|
| 1.1 | Solution scaffold (.NET 10), central packages, CI workflow, dev Postgres compose, structured logging, correlation IDs, health checks, migrate-on-startup | Done |
| 1.2 | Identity: users, 8 PRD roles, permission catalog, product-scope access, cookie login, lockout, admin API, CSRF header guard | Done |
| 1.3 | Audit log (append-only, DB trigger enforced) + read API | Done |
| 1.4 | Durable job queue (Postgres, `SKIP LOCKED`), retry/backoff, dead-letter, idempotency keys, replay/cancel API, Worker host | Done |
| 1.5 | Emergency pause switches (Publishing, Automation, Messaging, Ai) | Done |
| 1.6 | Products, categories, versioned product profiles (draft → review → approved, immutable once approved) | Done |
| 1.7 | Asset library: validated uploads (allow-list + size + magic bytes), source/rights metadata, versions, retire | Done (filesystem storage; S3 adapter still to do) |
| 1.8 | Approval engine: version-bound, permission + product scope + four-eyes, idempotent subject handlers | Done |
| 1.9 | AI abstraction: Claude (official SDK) + OpenAI (HTTP), usage ledger, per-product/global monthly budget, redaction, pause | Done, **not run against real providers** |
| 1.10 | Grounded generation: approved+verified facts only, claims must cite `factRef` or be assumptions, prohibited-claim and invented-figure checks, 1 repair retry | Done (tested with a scripted fake provider) |
| 1.11 | Strategy workspace (persisting briefs, hypotheses, versions) | **Not started** (generation endpoint exists; persistence/UI do not) |
| 1.12 | React shell | See bottom of this file |

Tests: 17 unit + 7 architecture + 35 integration (real Postgres via Testcontainers), all passing at the last run. Reproduce: `dotnet test`.

## Decisions made on your behalf (override any)

- First social channel: **Facebook Page** (plan default). Not started; Phase 3.
- .NET 10, Postgres-backed queue, ASP.NET Identity cookie auth, single-VM Docker Compose hosting, per the plan's defaults.
- Four-eyes approval is **on**: a requester cannot approve their own request. With a one-person team set `Governance:AllowSelfApproval=true`, otherwise nothing can ever be approved.
- AI never silently switches model/provider. A safety refusal is returned to the user as an error. (The Claude skill suggests server-side fallbacks; I deliberately did not enable them because the architecture says not to change requirements silently. Say if you want them.)
- Default AI models: Draft tier = `claude-sonnet-5-5`, Strategy tier = `claude-opus-5-5`. Default budgets: $20/product/month, $60 global (config `Ai:*`).
- Module boundary deviation from ADR-03: one project per module instead of Domain/Application/Infrastructure/Contracts split. Boundaries are enforced by architecture tests instead.
- Audit entries are written **after** the business commit in a separate commit (modules have separate DbContexts). A failure after commit would propagate as an error but the business change would stand. Acceptable for MVP; revisit with an outbox if it matters.

## Not verified / known gaps (be honest about these)

- **No real LLM call has ever been made** (no API keys on this machine). Prompt quality, the Anthropic SDK call and the OpenAI HTTP call are untested against the live APIs. First thing to do: set `Ai__Anthropic__ApiKey` and hit `POST /api/v1/products/{id}/ai/generate` once.
- The grounding validator catches structural violations, prohibited text and invented numbers. It cannot catch a fluent but unsupported qualitative claim ("the fastest POS"). Human approval remains the backstop.
- Prod deployment files (compose for API/worker/n8n/Caddy), S3 storage, backups, and the CI workflow have never been executed in CI.
- Cookie `Secure` policy is `SameAsRequest`; behind TLS proxy it must be forced to Always (forwarded headers are configured).
- No rate limiting on login beyond account lockout.
- Bootstrap admin `admin@trisoftic.local` / `DevOnly-ChangeMe-123!` exists **only in `appsettings.Development.json`**. Never deploy with it.

## Run it locally

```
docker compose -f infra/docker-compose.dev.yml up -d      # Postgres on :5433
dotnet run --project src/Host.Api                         # http://localhost:5232, migrates + seeds on start
dotnet run --project src/Host.Worker                      # job runner
dotnet test
```

Secrets via env vars, e.g. `Ai__Anthropic__ApiKey`, `Ai__OpenAi__ApiKey` + `Ai__OpenAi__Models__Draft`.

## Next steps (in order)

1. 1.11 Strategy workspace persistence + UI, then Phase 2 (campaigns, content versions, approvals UI, calendar; CRM track).
2. Make the first real AI call and tune prompts with the pilot products' real fact sheets (the real critical path is the fact sheets, see plan §11).
3. Phase 0 spike for the Facebook Page API (app review lead time) — start the Meta developer app now.
