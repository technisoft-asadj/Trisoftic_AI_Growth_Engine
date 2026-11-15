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
| 1.11 | Strategy workspace: B2C-gaming / B2B-software / blank templates, immutable versions with optimistic concurrency, link to saved AI drafts; every AI generation is now saved server-side as a draft with provenance | Done (backend tested, UI built). Strategy approval is deferred to the Campaign work in Phase 2 |
| 1.12 | React shell (login, products, profile editor + submit, assets upload/list, approvals inbox, AI studio, admin: pause/jobs/audit) | Built, type-checks, lints, production build OK. **Never opened in a browser** (no browser available overnight). Smoke-tested the API it calls against the real dev DB |

Tests: 17 unit + 7 architecture + 35 integration (real Postgres via Testcontainers), all passing at the last run. Reproduce: `dotnet test`.

### Phase 2

| WP | What | Status |
|---|---|---|
| 2.A1 | Campaigns (brief: objective, audience, market, channels, destination, tracking, budget plan, dates, thresholds; status rules) | Done |
| 2.A2 | Content items with immutable versions, 12 formats, optimistic concurrency, link to saved AI drafts | Done |
| 2.A3 | Submit -> approve / request changes / reject through the approval engine; approvals are bound to the exact version; editing approved/scheduled/in-review content invalidates earlier approval and withdraws the pending request | Done (state machine unit-tested, flow integration-tested) |
| 2.A4 | Schedule queue + calendar API and UI (only the approved current version can be scheduled; past times refused) | Done. **Nothing publishes yet**; scheduled items wait for Phase 3 |
| 2.B1-2.B7 | Consent service, CRM contacts/organizations, leads, pipeline | **Not started** (next) |

## Decisions made on your behalf (override any)

- First social channel: **Facebook Page** (plan default). Not started; Phase 3.
- .NET 10, Postgres-backed queue, ASP.NET Identity cookie auth, single-VM Docker Compose hosting, per the plan's defaults.
- Four-eyes approval is **on**: a requester cannot approve their own request. With a one-person team set `Governance:AllowSelfApproval=true`, otherwise nothing can ever be approved.
- AI never silently switches model/provider. A safety refusal is returned to the user as an error. (The Claude skill suggests server-side fallbacks; I deliberately did not enable them because the architecture says not to change requirements silently. Say if you want them.)
- Default AI models: Draft tier = `claude-sonnet-5-5`, Strategy tier = `claude-opus-5-5`. Default budgets: $20/product/month, $60 global (config `Ai:*`).
- Module boundary deviation from ADR-03: one project per module instead of Domain/Application/Infrastructure/Contracts split. Boundaries are enforced by architecture tests instead.
- Audit entries are written **after** the business commit in a separate commit (modules have separate DbContexts). A failure after commit would propagate as an error but the business change would stand. Acceptable for MVP; revisit with an outbox if it matters.

## Not verified / known gaps (be honest about these)

- **AI via Amazon Bedrock is verified live** (Converse API, `us.anthropic.claude-sonnet-4-6` / `opus-4-6`, dev account, us-east-1): a full flow with a demo product (profile -> approval -> grounded draft) produced a correct, grounded post that ignored the unverified fact and both prohibited claims; ~1.5 cents, 2 attempts. Enable with `Ai__DefaultProvider=Bedrock` and `Ai__Bedrock__Region=us-east-1`. The 5.x Claude models are listed on Bedrock but **not enabled for this AWS account** (AccessDenied) until model access is granted in the console.
- The direct Anthropic API provider (`Ai__Anthropic__ApiKey`) and the OpenAI provider have **not** been run against the live APIs.
- PowerShell `Get-Content`/`Set-Content` corrupts non-ASCII characters in this repo (it did once, repaired). Edit source files with the editor tools or `sed`, not those cmdlets.
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
cd web && npm install && npm run dev                      # http://localhost:5173, proxies /api to :5232
dotnet test
```

Sign in at the web app with `admin@trisoftic.local` / `DevOnly-ChangeMe-123!` (dev only). Then: create a product, start a profile, add at least one verified fact with evidence, submit it, and approve it with a **second** user (create one in the API: `POST /api/v1/users` with role `Approver`; there is no user-management screen yet) — or set `Governance__AllowSelfApproval=true` for a solo trial.

Known UI gaps: no user-management screen, no strategy save/versioning, drafts from the AI studio are display-only (not persisted until the Content module exists).

Secrets via env vars, e.g. `Ai__Anthropic__ApiKey`, `Ai__OpenAi__ApiKey` + `Ai__OpenAi__Models__Draft`.

## Next steps (in order)

1. 1.11 Strategy workspace persistence + UI, then Phase 2 (campaigns, content versions, approvals UI, calendar; CRM track).
2. Make the first real AI call and tune prompts with the pilot products' real fact sheets (the real critical path is the fact sheets, see plan §11).
3. Phase 0 spike for the Facebook Page API (app review lead time) — start the Meta developer app now.
