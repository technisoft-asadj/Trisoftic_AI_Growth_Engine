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
| 2.B1 | Consent: append-only history, evidence required for grants, address-level suppression list, `IContactPolicy` as the single "may we message this person?" gate (also honours the Messaging pause switch and merged contacts) | Done |
| 2.B2 | Companies, contacts (multiple emails/phones, tags, product/campaign links, interaction timeline), search and segmentation (text, tag, product, campaign, company, source, owner, "has marketing consent on channel"), product-scoped visibility | Done. Saved segments not built (filters are query parameters) |
| 2.B3 | Duplicate guard (unique normalized email/phone), duplicate suggestions, merge with full history move and tombstone | Done. **No undo** for merges (audited, source row kept as tombstone) |
| 2.B4 | Lead capture: anonymous public form (honeypot, 20/min per form+IP rate limit, 32 KB cap, CORS for embedding, exact consent wording stored as evidence), CSV import with dry-run and row-level errors (never assumes consent) | Done |
| 2.B5 | Round-robin or manual assignment, status workflow with required notes/reasons, configurable explainable score rules, follow-up dates and overdue filter | Done |
| 2.B6 | Configurable pipelines (validated: open stages + exactly one Won + one Lost; stages holding deals can't be removed), opportunities with value/currency/probability, lead-to-deal conversion, stage history + audit, ownership audit, reopen, activities/tasks, kanban board that never mixes currencies | Done |
| 2.B7 | CRM dashboard: lead counts, funnel (created -> contacted -> qualified -> converted -> won), response time, open pipeline value (weighted), win rate, revenue, sales cycle, overdue follow-ups, per-campaign and attributed vs unknown-source. Anything with nothing to measure is `null`, never 0 | Done |

| 2.B8 | CRM web screens (`web/src/pages/Crm.tsx`, nav "CRM"): dashboard (null shown as "—"), leads list/filters/detail (qualify, disqualify, next action, rescore, convert), contacts search + consent recording + timeline, pipeline board + deal page (move, win/lose with reason, reopen, activities, history), setup (lead forms with endpoint URL, CSV import with preview, score rules) | Built; type-checks, lints, builds. **Never opened in a browser.** Missing: create contact/company form, merge UI, deal edit, pipeline editor, drag-and-drop |

### Phase 3

| WP | What | Status |
|---|---|---|
| 3.4-3.6 | Scheduling module (`src/Modules/Scheduling`): appointment types (duration, buffers, step, notice, window, IANA zone, staff list), weekly hours + closed dates, DST-safe slot generation (pure, unit-tested), **DB exclusion constraint** (btree_gist, per staff, buffers included) so concurrent bookings cannot overlap (test: 50 racing requests -> exactly 1 booking), idempotency keys, anonymous public booking API (rate limited, staff identities never exposed), secret-token reschedule/cancel links (only a hash is stored), HMAC-signed lead links so only staff-minted links attach a booking to a lead. Web: public `/book/:key`, `/booking/:token`, staff "Appointments" tab | Done. Uses permission `appointments.manage`. Needs `Scheduling:LinkSecret` in prod. **Not yet:** anonymous bookings do not create a CRM lead/contact; no confirmation or reminder emails (3.10/3.11); web never opened in a browser |

| 3.7 | Automation module (`src/Modules/Automation`): workflows with immutable versions, triggers from CRM/scheduling events (lead created/assigned/status-changed, deal created/stage-changed, booking created/cancelled, plus `external.*` from n8n), conditions, steps `wait` / `if` / `task` / `notify` / `webhook`, one run per (workflow, event) so redelivery never repeats effects, a log row per step, per-workflow pause (starts nothing, cancels waiting runs) and the global Automation pause, transient failures retried and shown in the log. Modules talk through `IEventPublisher` / `ISubjectStateProvider` in BuildingBlocks (no module dependencies) | Done. Web: Automation page (builder, versions, runs, step log). **No email step yet** (3.10) |
| 3.8 | Signed webhooks both ways (HMAC-SHA256 + timestamp, 5-minute window, receipt table rejects replays; unknown names are 404). Docs in `infra/n8n/README.md` | Done on the platform side. **No n8n instance connected; outbound client never called a real endpoint** (tested with a fake) |
| 3.9 | Tasks and notifications: manual + workflow-created tasks, in-app notifications, one overdue alert per task (sweep job every 5 minutes in the worker), work summary badge in the nav, Inbox page | Done |

| 3.1-3.3 | Publishing module (`src/Modules/Publishing`): channel accounts with Data-Protection-encrypted tokens (never returned by the API), Facebook Page adapter (OAuth connect with signed expiring state + page picker, Graph posting, error mapping: auth / rate-limit / unknown / permanent), a **sandbox** channel for demos and tests, dispatcher + idempotent publish job. "Published" is set only after the network returns a post id; one `PublishedPost` per (item, version); an attempt whose outcome is unknown is checked against the network before any retry (no blind re-send); only the approved current version can go out; the Publishing pause holds posts. Failure recovery: failures list with reason and attempts, retry, abandon, mark published by hand (needs the link, audited). Content gained `Publishing` / `Published` / `PublishFailed` states. Web: Publishing page (needs attention, channels, published) and the connect-page picker | Done and tested with a scripted fake network. **The real Facebook adapter has never run against Facebook** (needs a Meta developer app + app review). Text posts only (no images/video yet). Production needs `DataProtection:KeysPath` on a shared volume for API and worker |
| 3.10 | Messaging module (`src/Modules/Messaging`): consent-gated email outbox (consent is re-checked **at send time**; blocked/failed/sent are all recorded), SMTP adapter (works with Amazon SES SMTP, SendGrid, Mailgun...), marketing mail refused unless sender identity, postal footer and a working unsubscribe link are configured, signed one-click unsubscribe (marketing only; booking confirmations keep working), signed bounce/complaint webhook that suppresses the address, address-level policy for people who are not contacts yet. Web: CRM > Messages and a public unsubscribe page | Done and tested with a fake provider. **No real mail server connected.** SPF/DKIM/DMARC DNS setup is on the sending domain and not something code can do. WhatsApp deferred as planned |
| 3.11 | Booking reminders: automation `waituntil` step (e.g. 60 minutes before `start`) + `if status = Confirmed` + `email` step; follows a rescheduled time, and a cancelled booking sends nothing. Automation `email` step also works for leads (marketing steps go through the consent gate) | Done and tested |

**Phase 3 exit:** approved content publishes on a channel with confirmed status (sandbox verified; Facebook unverified live), a lead can book a demo, follow-up tasks/notifications and reminders fire and are logged.

**Phase 2 is complete** apart from the UI gaps listed on 2.B8. "Influenced" attribution (PRD 5.14) is not modelled; only attributed (lead carries a campaign) vs unknown.


### Phase 4

| WP | What | Status |
|---|---|---|
| 4.1-4.4 | Analytics module: metric definitions (16 built in + custom, each with a plain-language definition), data sources with freshness windows and stale events, CSV import with dry-run, row-level errors, never-overwrite and batch rollback, dashboard tiles that follow null-not-zero (NoData / Unavailable / Partial / Measured), money per currency, two-source conflicts counted, social insights pull framework. Web: Analytics page (dashboard + data/imports) | Done and tested. **The Facebook adapter does not supply insights yet** (Meta's metric names change often and could not be checked); unsupported adapters record nothing and claim no freshness |
| 4.5, 4.6, 4.8 | Budget module: company/product/campaign budgets, allocation lines, four separate spend states (Recommended / Approved / Committed / Actual), hard cap enforced in the backend (concurrency-safe: 24 racing writes of 10 against a cap of 100 -> exactly 10 succeed), launch / cap increase / reallocation each need an approval (four-eyes, bound to the exact amounts, refused if the budget moved on), warn/stop thresholds raise one `budget.threshold-crossed` event per level (workflows can notify; voiding re-arms), spend entries are voided never deleted | Done and tested at API level. Only the Administrator role holds `budget.approve` (deliberate: money approvals are separate from content approvals; change in RoleCatalog if the owner wants managers to approve) |
| 4.7 | Budget planner (rules, not a language model, so every number traces to recorded data): with thin or no history it returns a small capped **learning experiment** with stop/go rules and no forecast; with enough history (>= 6 weeks and >= 30 results per channel) three scenarios with ranges, assumptions, uncertainty, evidence and confidence (never "High"); other currencies are excluded, not converted; adopting a scenario records Recommended figures only | Done and tested. No AI narrative layer; it can be added on top without changing the numbers |
| 4.9 | AI usage/cost breakdown (`GET /api/v1/ai/usage/breakdown?month=2026-11`): by provider+model (with tokens), product and purpose; each grouping sums to the same total; failed/blocked calls shown as "not charged". Costs are estimates from token counts. Shown on the Budget page | Done and tested |

Tests at last run: 136 unit + 17 architecture + 138 integration = **291 passing**. Web typecheck and build pass; the web app has still never been opened in a browser.

## Next steps when work resumes (in order)

1. Phase 5-6 per IMPLEMENTATION_PLAN.md (needs real data first). Also the outstanding gaps: real Facebook/SMTP/n8n/Bedrock-5.x verification, S3 storage, CI run, merge undo, saved segments, contact/company create forms, browser check of the whole web app.
Note: contacts, consent, leads and pipelines live in one `Crm` module (the plan listed CRM, Lead Management and Sales separately); they share transactions and one visibility rule, so splitting them would only add plumbing.

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

## Production deployment (added)

`infra/docker-compose.prod.yml` runs postgres, api, worker and an nginx `web` (serves the built SPA, proxies `/api` and `/health`). Copy `infra/prod.env.example` to `infra/prod.env` and fill it in; then `docker compose -f infra/docker-compose.prod.yml --env-file infra/prod.env up -d --build`.
- Verified locally: all images build, the api migrates every module and turns healthy, the worker starts after it, login works through the proxy, SPA deep links work, the budget and AI-usage endpoints answer.
- `DataProtection:KeysPath=/keys` and `Storage:Root` are volumes shared by api and worker (the worker must decrypt tokens the api stored). Back up the `keys` volume: losing it makes stored provider tokens unreadable and every page connection must be redone.
- TLS is not included: put Caddy or a load balancer in front of `web` and have it send `X-Forwarded-Proto`. `PUBLIC_BASE_URL` must be the public https address.
- Not verified: behaviour with real SMTP/Facebook/Bedrock credentials; a harmless `libgssapi_krb5` warning appears in logs.
