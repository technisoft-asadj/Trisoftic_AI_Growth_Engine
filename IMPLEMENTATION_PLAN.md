# TriSoftic AI Growth Engine — Implementation Plan

Version 1.0 · 15 Nov 2026 · Source documents: PRD v1.0, System Architecture v1.0, Development Roadmap v1.0 (14 Nov 2026)

Purpose: remove thinking from the build. Every decision that can be made now is made here (marked **DEFAULT** where it still needs the owner's one-line confirmation). Every work package has an order, dependencies, deliverable and exit test. If a question comes up during development, the answer should be in this file; if not, add it to Section 12 rather than improvising.

---

## 1. What we are building (one paragraph)

A modular-monolith web platform (React + .NET + PostgreSQL) that holds verified product facts, uses AI grounded in those facts to produce strategy and content, routes everything through human approval, publishes to one social channel with confirmed status, captures B2B leads into an owned CRM and pipeline, books demos without double-booking, runs consent-safe follow-ups, and tracks budget/performance honestly (missing data ≠ zero). Pilots: **Ludo Fight (B2C)** and **Pharmacy POS (B2B)**.

## 2. Architect's reading of the documents: the "dots"

The documents list 17 modules. They are not equally important. Five **kernels** are used by almost everything else; if they are built late or wrongly, every module gets reworked. Build kernels first; modules plug into them.

| # | Kernel (build first) | Used by | Why it must come early |
|---|---|---|---|
| K1 | **Identity + RBAC + product-scoped authorization** | every endpoint | "Enforce on every API operation" — retrofitting is a rewrite |
| K2 | **Audit log** (append-only, attributable) | approvals, publishing, CRM, budget | Required in every acceptance criterion |
| K3 | **Approval engine** (generic: subject, version, policy, decision) | content, campaigns, budgets, publishing | Content, budget and campaign launch all share one state machine |
| K4 | **Durable job queue + outbox + idempotency keys + correlation IDs** | AI, publishing, imports, reminders, n8n | Architecture forbids in-memory timers for critical work |
| K5 | **Consent & policy service** | leads, follow-ups, email/WhatsApp, reminders | "Check consent immediately before sending" is a single choke point |

Plus two data foundations: **Product Profile Versioning** (grounding source for AI) and **Metric/Freshness model** (missing ≠ zero).

### Dependency graph (build order flows top → bottom)

```
K1 Identity/RBAC ─┬─ K2 Audit ─┬─ K3 Approval engine ─┐
                  │            │                      │
                  └─ Products ─┴─ Product Profile ────┤
                         │            (versioned)     │
                    Asset library                     │
                         │                            │
K4 Jobs/Outbox ──── AI abstraction ── Grounded retrieval
                         │
                  Strategy + Content Studio ── Content versions ── (K3) ── Calendar/Queue
                                                                              │
                                                  Social adapter (1 channel) ─┘── Confirmed publish
K5 Consent ── CRM (Contact/Org) ── Lead ── Pipeline/Opportunity ── Appointments ── Automation (+n8n)
                                                                                         │
Metric model ── Manual imports ── Dashboards ── Budget/Spend ── Planner (AI scenarios) ─┘
                                                      │
                                         Experiments ── Funnel ── Firebase/AdMob (Phase 5)
```

**Two parallel tracks** after Phase 1 (they only meet at Automation and Dashboards):
- **Track A – Marketing:** Content ops → Calendar → Publishing → Analytics → Budget → Intelligence.
- **Track B – Sales:** CRM → Leads → Pipeline → Appointments → Automation.

If there are two engineers, one owns each track. If solo, do Track A's content + approval first (it produces the demo), then Track B.

## 3. Architecture decisions (ADR register)

Items marked **DEFAULT** are chosen to fit the ~USD 100/month constraint and the "no premature complexity" principle. Confirm or override in Phase 0 (Section 12); otherwise they stand.

| ID | Decision | Choice | Rationale |
|---|---|---|---|
| ADR-01 | .NET target | **.NET 10 (LTS)** | .NET 8 reaches end of support Nov 2026; .NET 10 is the current LTS. Confirm at project start. |
| ADR-02 | Style | Modular monolith: `Api` + `Worker` hosts sharing module assemblies | Per Architecture §5.1 |
| ADR-03 | Module isolation | One project per module (`Domain`, `Application`, `Infrastructure`), modules talk via interfaces/integration events, **no cross-module DB joins in code** (each module owns its schema in Postgres) | Keeps extraction/tenancy possible |
| ADR-04 | DB access | EF Core + migrations; Dapper/raw SQL only for reporting queries | Productivity; reporting is where raw SQL pays off |
| ADR-05 | Queue | **DEFAULT:** PostgreSQL-backed job table (`FOR UPDATE SKIP LOCKED`) behind `IJobQueue`; retry/backoff, dead-letter status, replay | Zero extra infra/cost, jobs visible in UI by plain query; swappable to SQS/RabbitMQ later |
| ADR-06 | Identity | **DEFAULT:** ASP.NET Core Identity, cookie session via BFF (React never holds tokens), behind an `IIdentityService` seam so OIDC (Entra/Cognito/Keycloak) can replace it | Internal users only at MVP; no per-MAU cost |
| ADR-07 | Authorization | Policy-based: `Permission` (action) × `ProductScope` (which products). Resource-based handler checks both on every command | Matches PRD roles and product-level auth |
| ADR-08 | Hosting | **DEFAULT:** Single cloud VM (AWS Lightsail/EC2) running Docker Compose for staging and, separately, production; Postgres with automated daily dump to object storage in v1, move to managed Postgres when budget allows | Cheapest honest option; infra budget is separate from the USD 100 media budget |
| ADR-09 | Object storage | **DEFAULT:** S3 (or R2) private bucket, pre-signed URLs, virus/type/size validation on upload | Per Architecture §9 |
| ADR-10 | Secrets | **DEFAULT:** env-injected from AWS SSM Parameter Store / Secrets Manager; provider tokens encrypted at rest with app-level key (ASP.NET Data Protection with persisted keys) | Never in source control |
| ADR-11 | AI abstraction | `ITextGenerator`, `IStructuredGenerator<T>`, `IEmbedder` with OpenAI + Anthropic implementations; every call logs provider, model, prompt-template version, tokens, cost estimate, outcome, correlation ID | Architecture §5.4 |
| ADR-12 | Retrieval | **DEFAULT:** No vector DB in MVP. Grounding = structured fetch of the *approved* `ProductProfileVersion` (facts, claims, brand rules) injected into the prompt. Add `pgvector` only in Phase 5 for research notes | Product facts are small and structured; vector search adds risk, not value, for MVP |
| ADR-13 | Structured output | AI returns JSON validated against a schema; each generated claim carries `factRef` (profile fact ID) or `assumption=true`. Server rejects drafts containing unreferenced, unlabeled claims | Makes "never invent features" testable |
| ADR-14 | n8n | Self-hosted, single instance, reachable only via signed webhooks both ways. n8n **never** holds core rules; it calls `.NET` command endpoints which re-check auth/consent/approval | Architecture §5.2 |
| ADR-15 | Frontend | React + TypeScript, Vite, TanStack Query, React Router, a component library (**DEFAULT:** MUI or shadcn/ui), OpenAPI-generated typed client | Generated client prevents API drift |
| ADR-16 | API | `/api/v1`, DTOs only, RFC 7807 problem-details errors, cursor pagination, `Idempotency-Key` header on publish/send/book, OpenAPI published | Architecture §10 |
| ADR-17 | Multi-tenancy readiness | Every business table carries `OrganizationId` (single org now) + `ProductId` where applicable; global query filter in EF | Cheap now, impossible to retrofit cheaply |
| ADR-18 | Time | Store UTC (`timestamptz`) everywhere; display in user/booking time zone; IANA tz IDs | Appointments depend on it |
| ADR-19 | Currency/money | `numeric(18,4)` + ISO currency code; never floats | Budget/opportunity values |
| ADR-20 | "Missing ≠ zero" | `MetricObservation.Value` nullable + `Status` (`Reported`, `NotAvailable`, `Pending`); UI renders "—" with source/period/freshness | PRD §5.15 |
| ADR-21 | **Client businesses ("agency-lite")** *(owner decision 16 Nov 2026)* | A **Client** is a grouping *inside the single TriSoftic organization*: it owns products and a brand kit. Client users are ordinary users with an approval-only role, scoped by the existing per-user product access, so a client sees and approves only its own products. **Not** separate tenants: no per-client database/org, billing or self-service signup. Full multi-tenancy stays the Phase 6 decision gate (ADR-17 keeps that door open) | Delivers "client approves their own content" at a fraction of the cost of tenancy; reuses ADR-07 product scoping |
| ADR-22 | Visual production providers *(revised 16 Nov 2026: owner has no Canva plan and prefers open source / lower cost)* | Two provider seams in BuildingBlocks, implemented by the AI module and a new Design module. `IImageGenerator`: **default = Stability models on Amazon Bedrock in `us-west-2`** (Stable Image Core / Ultra / SD3.5 Large: listed in the account's region catalogue, one AWS bill, no new vendor; `us-east-1` has only Stability *editing* tools); **OpenAI Images** stays an optional second implementation. `IDesignProvider`: **default = an in-house template renderer** (open-source Chromium/Playwright, already in the toolchain, renders HTML/SVG brand templates to PNG at the right size for each format); **Canva Connect is deferred** behind the same interface and only built if staff or clients need to design templates visually in Canva. Every generated file is stored as an Asset with provenance (provider, model/template, prompt, cost) and, if AI-made, `IsSynthetic = true` | Zero licence cost, no plan-eligibility risk, fully automatable and brand-consistent; providers stay swappable; PRD 5.6 (synthetic never presented as authentic UI/gameplay) is enforced by data. Trade-off accepted: templates are authored by us (a built-in set plus brand-kit variables), not drawn by staff in a visual editor |
| ADR-23 | **Tools deliberately not integrated** *(owner decision 16 Nov 2026)* | **CapCut** (no official public API; unofficial tools break and risk ToS: a person edits and uploads the finished video), **Metricool** (duplicates our own publishing/analytics; API needs a paid Advanced plan), **Meta Business Suite** (a screen for people, no API; we already use the Graph API directly). Revisit triggers: CapCut ships an official API; a second network is needed and a native adapter costs more than Metricool's plan | Avoids fragile or redundant integrations. These remain tools the team may use by hand |
| ADR-24 | AI-generated visuals policy | AI images are *illustrations*: prompts forbid fake UI, fake gameplay, readable text and real people/brands; the product's brand kit (palette, style, banned imagery) is injected; the result is labelled "AI-generated" everywhere it is shown and carries the network's AI-disclosure where one exists. Each generation is budgeted and ledgered like a text call | Keeps visuals honest and inside the AI cost cap |

## 4. Solution layout

```
/TriSoftic.GrowthEngine
  /src
    /Host.Api                 ASP.NET Core API (composition root, auth, OpenAPI)
    /Host.Worker              Background worker (job runner, schedulers)
    /BuildingBlocks           Result type, Clock, Ids, Outbox, Idempotency, Audit API, Policy primitives
    /Modules
      /Identity               /Portfolio  /MarketingIntel  /Campaigns  /Content
      /Publishing             /Crm        /Leads           /Sales      /Appointments
      /Automation             /Analytics  /Budget          /Governance (Audit + Approvals + Pause)
        each: .Domain  .Application  .Infrastructure  .Contracts (public DTOs/events)
    /Integrations
      /Ai.OpenAi  /Ai.Anthropic  /Social.<Channel>  /Messaging.<Provider>  /Email.<Provider>
  /tests
    /Unit  /Integration (Testcontainers Postgres)  /Contract (adapters)  /Architecture (NetArchTest)  /E2E (Playwright)
  /web                        React app
  /infra                      docker-compose, IaC, n8n workflows (exported JSON, version-controlled)
  /docs                       ADRs, data dictionary, runbooks, API notes
```

Architecture tests (NetArchTest) enforce: modules reference only each other's `.Contracts`; `Domain` has no infrastructure refs; controllers never expose entities.

## 5. Engineering conventions (decided once)

- **Branching:** trunk-based, short-lived branches, PR required, CI must pass.
- **CI/CD:** build + unit + integration (Testcontainers) + architecture tests + OpenAPI diff + web lint/typecheck/test on every PR; deploy to staging on merge; production by manual promote.
- **Migrations:** forward-only, reviewed; automatic on staging, applied as an explicit step on production.
- **Config:** typed options, validated at startup (app fails fast on missing secrets).
- **Logging:** Serilog structured JSON, `CorrelationId` on every log, job, outbox message, n8n call and provider call. Never log tokens, prompts containing PII, or message bodies.
- **Errors:** domain failures return `Result`, not exceptions; provider errors are mapped to a small internal error taxonomy (`Transient`, `Auth`, `RateLimited`, `PolicyRejected`, `Permanent`).
- **State machines:** content, publishing job, lead, booking, approval, budget each have an explicit transition table with tests; illegal transitions throw.
- **Definition of Done (every work package):** code + migrations; authorization policy + test; audit events emitted where sensitive; OpenAPI updated; unit + integration tests; UI with loading/empty/error states; seed data; docs line in data dictionary; demoable in staging.

## 6. Phased delivery plan

Phases follow the Roadmap but are broken into **work packages (WP)** with order, dependencies and an exit test. Durations assume ~2 engineers + part-time marketing operator; scale if different (Section 12 asks for confirmation). Dates are indicative, not commitments, per the Roadmap.

### Phase 0 — Discovery & technical validation (Days 1–5) *(spikes, no production code)*

| WP | Task | Output | Gate |
|---|---|---|---|
| 0.1 | Confirm Section 12 decisions with owner | Signed decision list | All "Blocker" items closed |
| 0.2 | **Social API feasibility spike** for the chosen first channel: create developer app, OAuth, publish one test post to an owned test account, read back post ID/status | Working script + notes on scopes, review requirements, rate limits, media rules | Can publish & read status, or fall back to the second-choice channel |
| 0.3 | n8n spike: signed webhook round-trip to a stub .NET endpoint | Proof + signature scheme | Verified signature, replay protection |
| 0.4 | Data dictionary v0: entities from Architecture §4 with owning module, key fields, state machines | `/docs/data-dictionary.md` | Reviewed |
| 0.5 | Product Profile schema: facts, claims (approved / prohibited), personas, brand voice, evidence notes | JSON schema + example for Ludo Fight and Pharmacy POS | Product owner can fill it with real data |
| 0.6 | Wireframes for: login/portfolio, product profile, content studio, approval inbox, calendar, lead board, booking page | Low-fi screens | Operators review |
| 0.7 | **Collect real product facts** (see Section 11) — start now; it is the longest-lead item and is not an engineering task | Verified fact sheets | Owner sign-off |

**Exit:** ADRs confirmed, first channel feasibility proven, wireframes approved.

### Phase 1 — Core platform & product intelligence (Days 6–20)

Order matters; each WP unblocks the next.

| WP | Task | Depends on | Exit test |
|---|---|---|---|
| 1.1 | Repo, solution layout, CI/CD, docker-compose, staging environment, health checks, structured logging, config validation | 0.1 | Merge → auto-deploy to staging |
| 1.2 | **K1 Identity & RBAC:** users, 8 roles from PRD §4, permissions, product-scope assignments, login/logout, password policy, lockout, session management | 1.1 | A user without product access gets 403 on that product's data (integration test) |
| 1.3 | **K2 Audit:** `AuditEvent` append-only table (actor, action, subject, before/after summary, correlationId, timestamp); `IAuditWriter`; read UI for admins | 1.2 | Role/permission change appears in audit |
| 1.4 | **K4 Jobs/Outbox:** job table, runner, retry/backoff, dead-letter state, idempotency keys, correlation IDs, admin "Jobs" page (inspect/retry/cancel) | 1.1 | Kill worker mid-job → job resumes; duplicate idempotency key → one effect |
| 1.5 | **Governance pause switch:** global + per-channel "emergency stop" flags checked by publishing & automation | 1.3 | Flag set → worker refuses to publish |
| 1.6 | **Portfolio & Products:** Product, Category, Market, Persona, `ProductProfileVersion` (draft → approved, immutable once approved), `ProductClaim` (approved/prohibited), brand voice | 1.2, 1.3 | Edit creates new version; approved version is read-only |
| 1.7 | **Asset library:** upload to object storage, type/size validation, source + ownership + usage-rights metadata, product association, versioning | 1.6 | Upload, attach to product, rights shown |
| 1.8 | **K3 Approval engine v1:** `ApprovalRecord` tied to (subject type, subject id, **version**), states Pending/Approved/Rejected/ChangesRequested, who-can-approve policy by product/channel/risk, audit on every transition | 1.2, 1.3 | Approving v2 does not approve v3; editing approved content invalidates approval |
| 1.9 | **AI abstraction:** interfaces, OpenAI + Anthropic impls, usage/cost ledger (`AiUsageRecord`), per-product monthly AI budget + alert, prompt-template store with versions, redaction of secrets/PII before send | 1.4, 1.6 | Same request runs on either provider; usage recorded; budget breach blocks and alerts |
| 1.10 | **Grounded generation:** build prompt from approved profile version; structured output with `factRef`/`assumption`; reject unreferenced claims; missing-fact → clarification question | 1.8, 1.9 | Golden test set: no invented features (see §8) |
| 1.11 | Strategy brief generation + Strategy workspace (objective, audience, market, channel, hypotheses, assumptions, KPIs, versions); B2C vs B2B templates | 1.10 | Generate strategy for both pilots |
| 1.12 | React shell: auth, nav by permission, portfolio dashboard v0 (counts only), product pages, asset pages | 1.2, 1.6 | Operators can register both pilot products without developers |

**Exit:** Team registers Ludo Fight + Pharmacy POS and generates grounded strategy and content drafts. Demo.

### Phase 2 — Content operations & CRM foundation (Days 21–40) — *Tracks A and B run in parallel*

**Track A — Content**
| WP | Task | Depends on | Exit test |
|---|---|---|---|
| 2.A1 | Campaign entity + brief (objective, audience, market, channel, destination, tracking, budget placeholder, duration, thresholds) | 1.11 | Campaign links to product & strategy |
| 2.A2 | **Content Studio:** ContentItem, ContentVersion, ChannelVariant, editor, formats (caption, carousel, script, blog, landing copy, ad variant), language, link to campaign/audience/channel; AI-assist with provenance (model, prompt version) | 1.10, 2.A1 | Edit → new version; provenance saved |
| 2.A3 | Submit → approve/reject/changes via approval engine; approval inbox with diff vs previous version | 1.8, 2.A2 | Approved version immutable; reject returns to author |
| 2.A4 | Calendar & queue: statuses draft→review→approved→scheduled (publishing/published/failed/cancelled reserved for Phase 3), drag to schedule, filters by product/channel | 2.A3 | Only approved items can be scheduled |

**Track B — CRM & sales**
| WP | Task | Depends on | Exit test |
|---|---|---|---|
| 2.B1 | **K5 Consent service:** `ConsentRecord` (contact, channel, purpose, status, source, timestamp, evidence), `ICanContact(contact, channel, purpose)` returning allow/deny + reason, opt-out handling | 1.3 | Deny when no/withdrawn consent (unit + integration) |
| 2.B2 | Contacts, Organizations, ContactMethod, Tags, Interaction timeline; search; segmentation (saved filters); product/campaign association | 1.2, 2.B1 | Search & segment by tag/product |
| 2.B3 | Duplicate detection (normalized email/phone/domain) + merge with audit and undo window | 2.B2 | Merge keeps history, audited |
| 2.B4 | Lead capture: hosted form + embeddable form endpoint (spam protection, rate limit, consent checkbox captured), CSV import with mapping & dry-run | 2.B2, 1.4 | Form submission creates contact+lead+consent |
| 2.B5 | Lead assignment (manual + round-robin), qualification status, basic configurable score (rules table), next action + follow-up date, conversion/disqualification reasons | 2.B4 | Lead moves through statuses with audit |
| 2.B6 | Pipelines & stages (configurable per product), Opportunity (value, currency, probability, expected close, owner, source, next step), activities, won/lost reasons; stage/owner change audit; Kanban board | 2.B5 | Enquiry → opportunity → won/lost |
| 2.B7 | Lead→pipeline dashboard (counts, conversion by stage, response time) with "no data" handling | 2.B6 | Matches seeded test data exactly |

**Exit:** B2B enquiry can be managed through pipeline and linked to product/campaign; content can be written, approved and calendared.

### Phase 3 — Automation, appointments, first publishing channel (Days 41–65)

| WP | Task | Depends on | Exit test |
|---|---|---|---|
| 3.1 | **Social adapter #1** (channel from 0.2): OAuth connect/reconnect, encrypted token storage, refresh job, media validation, `IPublisher` implementation, error mapping | 0.2, 1.4 | Test account connected; token refresh works |
| 3.2 | **Publishing pipeline:** schedule → validator (account, permissions, format, approved *version*, pause flag) → job → adapter → `PublishingAttempt` → poll/confirm → `PublishedPost` (provider post ID, URL). `Published` only after provider confirmation | 3.1, 2.A4, 1.5 | Provider failure shows Failed, retry creates no duplicate (idempotency) |
| 3.3 | Failure UX: failed-jobs list, error reason, retry, mark "published manually" (with URL, audited), cancel | 3.2 | Recovery gate in §8 |
| 3.4 | **Appointments:** Calendar, AppointmentType, AvailabilityRule (weekly + exceptions), buffers, time zones, assigned staff, Booking | 2.B6 | Slots generated correctly across time zones/DST |
| 3.5 | **Double-booking protection:** DB-level exclusion constraint (`tstzrange` + `EXCLUDE USING gist`) per staff/resource + transactional booking + idempotency key | 3.4 | 50 concurrent bookings of one slot → exactly one succeeds |
| 3.6 | Public booking page, confirmation, reschedule/cancel links (signed tokens), link booking↔lead/opportunity | 3.5 | Prospect books from lead email link |
| 3.7 | **Automation engine (in .NET):** triggers (lead created, assigned, stage changed, booking created, no-reply timeout), conditions, delay, actions (create task, notify, send template message via consent gate, call n8n), `WorkflowDefinition/Version/Run/ActionLog`, pause per workflow, retries | 1.4, 2.B1, 2.B6 | Run log shows every step; paused workflow does nothing |
| 3.8 | **n8n integration:** signed webhooks both ways; n8n used for external I/O (e.g., calling a provider) not decisions; workflows exported to `/infra/n8n` and versioned | 0.3, 3.7 | Tampered signature rejected; replay rejected |
| 3.9 | Tasks & notifications (in-app + email to staff); overdue follow-up alerts | 3.7 | Overdue alert appears on dashboard |
| 3.10 | Email provider adapter **only after** consent review: SPF/DKIM/DMARC, unsubscribe link, suppression list, bounce webhook. WhatsApp **deferred** unless an owner decision says otherwise | 2.B1, Section 12 | Send blocked for non-consented; unsubscribe suppresses |
| 3.11 | Reminders for bookings via ICanContact gate | 3.6, 3.10 | Reminder not sent without consent |

**Exit:** approved content publishes on one channel with confirmed status; a lead can book a demo; follow-up tasks/notifications fire and are logged. **→ MVP feature-complete except analytics/budget.**

### Phase 4 — Analytics & budget planner (Days 66–90)

| WP | Task | Depends on | Exit test |
|---|---|---|---|
| 4.1 | Metric model: `DataSource`, `MetricDefinition` (name, unit, definition text, direction), `ImportBatch`, `MetricObservation` (nullable value, status, period, source, retrieved-at) | 1.4 | Definitions visible in UI tooltips |
| 4.2 | Manual import (CSV/template) for campaign performance, spend, store/install data, B2B web metrics, with validation, dry-run, rollback of a batch | 4.1 | Bad rows reported not silently dropped |
| 4.3 | Social metrics pull for the connected channel (reach, impressions, engagement, clicks, followers, views) via scheduled job; freshness tracked; stale alert | 3.1, 4.1 | Stale source flagged after threshold |
| 4.4 | Gaming dashboard & B2B dashboard; filters (product, market, channel, period); every tile shows source, period, freshness; missing data renders "—" | 4.2 | Analytics gate in §8 |
| 4.5 | **Budget module:** Budget (company/product/campaign), currency, hard cap, risk tolerance; `BudgetAllocation`; `SpendRecord` kept as 4 separate states: **Recommended, Approved, Committed, Actual** | 1.8 | Cap enforced in .NET; over-cap write rejected |
| 4.6 | Budget approval flow (via K3): launch / increase / reallocation each require a new approval + audit; approvals bound to specific amount & scope | 4.5 | Increase without approval fails |
| 4.7 | **AI Budget Planner:** conservative/balanced/growth scenarios with assumptions, evidence refs, confidence, uncertainty; refuses precise forecasts when evidence is weak and proposes capped learning experiments instead | 4.2, 4.5, 1.9 | With empty history, output is "learning experiment", not a forecast |
| 4.8 | Stop/go thresholds on campaigns; alerts for budget thresholds | 4.5 | Threshold breach raises alert |
| 4.9 | AI usage/cost dashboard by provider/product | 1.9 | Totals reconcile with ledger |

**Exit:** owner compares proposed vs. actual spend and reviews evidence-based proposals. **→ MVP complete (acceptance list PRD §9 fully met).**

### Phase 5A — Visual content production & client workflow *(inserted 16 Nov 2026 by owner decision; runs before 5.1–5.7)*

Source: the owner's five-step "AI Content Production System" (client information → AI content planning → design **and** video production → human quality control → client approval & publishing → performance improvement). What already exists is marked ✅; the work packages cover only what is missing.

| Step in the owner's workflow | Status today | Delivered by |
|---|---|---|
| 1 Client information (profile, audience, brand guidelines, goals) | ✅ product profile (facts, markets, voice). ❌ no visual brand kit, no client entity | 5A.4, 5A.8 |
| 2 AI content planning (ideas, calendar, hooks, captions) | ✅ growth plans (angles, captions, schedule, review). ❌ plans cannot attach visuals | 5A.3 |
| 3a Design production (templates + AI graphics) | ❌ files can only be uploaded by hand | 5A.2, 5A.5 |
| 3b Video production | ❌ one uploaded video can be attached to a post, but Reels and video stories cannot be published and the video has no brief | 5A.1, 5A.6 |
| 3c Human quality control (accuracy, branding, spelling, quality, platform requirements) | ❌ a single approval only | 5A.7 |
| 4 Client approval & publishing | ✅ approval, scheduling, Facebook posting. ❌ client cannot approve their own products | 5A.8 |
| 5 Performance improvement | ✅ plan review and next plan (needs real figures: Facebook permissions + app Live) | 5A.9 |

**Sequence (each package is shippable on its own; the first needs nothing external):**

| WP | Task | Depends on | Exit test |
|---|---|---|---|
| 5A.0 | **Spikes (1–2 days each, before any build):** (a) *Template renderer:* render one brand template (logo, palette, headline, image slot) to PNG at 1080×1080, 1080×1350 and 1080×1920 inside the container, with the brand fonts embedded. (b) *Image generation:* enable Bedrock model access for Stability Image Core/Ultra in `us-west-2`, generate one image from the Ludo Fight profile + brand kit, record the real per-image price; confirm the current model ids (OpenAI only if Bedrock access is refused). (c) *Facebook Reels / video story:* upload one real clip to the Page. (d) *Meta app Live:* privacy-policy URL, icon, category; confirm a post is publicly visible. (e) *Canva (only if the owner later wants it):* use Canva's free trial of a paid plan to test OAuth + one autofill job + export, **before buying anything** (Canva's docs disagree on which plans may use autofill) | — | Written findings per spike; plan edited if a spike fails |
| 5A.1 | **Reels and video stories (Facebook):** formats `Reel` (one video) and video `Story`; start → upload (rupload host, streamed) → finish → status check; platform specs checked from the file before approval (aspect ratio, duration, size, format) using a media probe in the container; no idempotency key exists upstream, so after an uncertain failure the post is **checked, not retried blindly** | 3.1–3.3, media attach (done) | Fake network: timeout after accept never creates a second Reel; a clip outside the spec is refused at submit with the reason |
| 5A.2 | **AI images:** `IImageGenerator` (ADR-22/24; Bedrock Stability by default); prompt built from the product's approved profile and brand kit; result stored as a synthetic Asset with provenance and cost; per-product image count/cost cap in the existing AI ledger; "Generate image" in the post editor | 1.9, 1.7 | Image cost appears in AI usage and respects the cap; every generated asset is `IsSynthetic`; a prompt asking for a fake screenshot or readable text is refused or rewritten |
| 5A.3 | **Plans attach visuals:** plan option *visuals* (none / my library / Canva / AI / mixed); the planner proposes a visual per slot (an exact existing asset name, a Canva template + fields, or an image prompt); drafting a slot produces the visual and attaches it to the version; the plan shows an **estimated cost before it runs** | 5A.2 (and 5A.5 for Canva) | A plan with visuals drafts N posts each carrying one image; an asset the planner invented is rejected; the estimate is within 10% of the ledger |
| 5A.4 | **Brand kit** per product (and inherited by a client): logo asset, palette, fonts, visual style notes, banned imagery; approved like a profile version; injected into image prompts and used to pick Canva templates | 1.6 | AI image prompts contain the approved kit and nothing from an unapproved version |
| 5A.5 | **Template renderer (`IDesignProvider`, in-house, open source):** a built-in set of brand templates (announcement, feature highlight, quote/hook card, question-to-followers, download banner, countdown, story 9:16) written as HTML/SVG with named fields (headline, body, image, logo, call to action) and brand-kit variables (palette, fonts, logo); the renderer fills a template and exports a PNG at the size the target format needs; per-product choice of which templates are allowed; text that does not fit is shortened or refused rather than overflowing; output stored as an Asset with provenance (template, fields, brand-kit version); "Preview" before attaching. The Canva implementation of the same interface is **deferred** (ADR-22) | 5A.0(a), 5A.4 | A post's headline longer than the template allows is refused with the limit; the same inputs render byte-identical output; fonts and logo from the approved brand kit appear; no network call is needed to render |
| 5A.6 | **Video brief** (the CapCut hand-off, ADR-23): AI writes a grounded brief (hook, script, shot list, on-screen text, caption, length, aspect ratio) as content format `VideoBrief` and a task for the editor; the editor uploads the finished video to Assets and links it to the brief; linking creates the Reel/Story post | 5A.1, 3.7 | Brief claims cite verified facts; a video uploaded against a brief appears on that brief and can be submitted |
| 5A.7 | **Human QC stage before approval:** content status `InQc`; checklist (accuracy: claims all grounded — automatic; branding; spelling; platform requirements — automatic from 5A.1/media rules; quality) where automatic items are pre-ticked and the rest need a named reviewer; a failed item returns the post with a note; role `QC Reviewer` | 1.8, 5A.1 | A post cannot reach approval without a completed checklist; automatic items cannot be ticked by hand |
| 5A.8 | **Clients (ADR-21):** `Client` (name, contact, logo/brand kit link); products belong to a client; client users get an approval-only role scoped to their products; **client approval** as the final stage when the product's client requires it; client-facing "Waiting for you" screen showing the post exactly as it will appear (text, images, video), approve / request changes with a comment; email notice with a signed link (consent-gated like all mail) | 1.2, 1.8, 3.10, 5A.7 | A client user can see and decide only their own products' posts (authorization matrix); self-approval rule still applies; a client's change request returns the post to draft with their comment |
| 5A.9 | **Visual performance loop:** the plan review also compares visual choices (template, AI vs library, video vs photo) using only reported figures, and states when evidence is too thin; learnings feed the next plan | 4.3, 5A.3, plan review (done) | With figures missing the review says so; with figures present it never quotes a number it was not given |

**Not in 5A (by decision):** automation of CapCut, Metricool and Meta Business Suite (ADR-23); a paid Canva plan and the Canva integration (deferred, ADR-22); Instagram and other networks (still Phase 5.7, only if justified by data); video generated by AI; separate tenants for clients (ADR-21).

**Owner inputs needed by the time each package starts:** 5A.0(b) enable Bedrock model access for Stability Image models in `us-west-2` (AWS console, a few clicks); 5A.0(d) a privacy-policy URL for the Meta app; 5A.4 each product's logo, colours and fonts; 5A.8 the first real client and their approver's email. **No paid subscription is required.**

### Phase 5 — Intelligence & optimization (Days 91–130)
Sequence: 5.1 Firebase/gameplay instrumentation in Ludo Fight (separate work in the game app; validate event quality first) → 5.2 Play Console/Firebase/BigQuery/AdMob importers (scheduled, provenance, freshness) → 5.3 sourced competitor/market research (source URL, date, confidence, fact/inference/hypothesis) → 5.4 experiments & variant comparison (hypothesis vs. result) → 5.5 attribution touchpoints and lead-to-revenue funnel (attributed / influenced / unknown) → 5.6 recommendations + learning loop → 5.7 additional social adapters only if justified by data. `pgvector` introduced here if research retrieval needs it.

### Phase 6 — Hardening & optional commercialization (Days 131+)
Security review/pen-test, privacy review (retention/deletion/export), load test, backup + **timed restore test**, runbooks, monitoring/alerts polish, diagnostics UI, export tools. Then a *decision gate* on tenancy/billing; do not start tenancy work before internal validation. (Phase 5A's "clients" are **not** tenancy: see ADR-21. The gate decides only whether clients ever need to be fully separate organizations with their own billing and sign-up.)

## 7. MVP scope guard

MVP = Phases 1–4 (PRD §9 criteria). Everything listed under PRD §10 and Roadmap §5 "Defer" is **out** until a change-control entry is approved by the Product Owner. Change control rule: any new item must name what leaves the phase or move the date.

Mapping of PRD §9 acceptance criteria to work packages:

| PRD §9 criterion | Delivered by |
|---|---|
| Authenticate, access only permitted products/functions | 1.2 |
| Admin creates product profiles + assets | 1.6, 1.7, 1.12 |
| Grounded strategy & content | 1.10, 1.11 |
| Edit/submit/approve/reject/version | 2.A2, 2.A3, 1.8 |
| Calendar & queue | 2.A4 |
| One social integration with confirmed result | 3.1–3.3 |
| CRM create/search/segment/associate | 2.B2, 2.B3 |
| Lead capture→assign→qualify→pipeline | 2.B4–2.B6 |
| Appointment types, availability, no double-booking | 3.4–3.6 |
| Follow-up workflow tasks/notifications/logs; consent-gated external | 3.7–3.11 |
| Budget proposals, approval, caps, actual spend | 4.5–4.7 |
| Dashboard missing-vs-zero, source/period/freshness | 4.1, 4.4 |
| Audit of approvals, publishing, CRM, budget | 1.3 + emitted throughout |

## 8. Quality gates (each is an automated test that must pass before a phase can close)

| Gate | How we test |
|---|---|
| Product grounding | Golden set of ~30 prompts per pilot incl. adversarial ("add a feature X", "say it has Y certification"). Assert every claim has a `factRef` or `assumption=true`; prohibited claims never appear. Run on both providers; run on each prompt-template change |
| Authorization | Matrix test: every endpoint × every role × in-scope/out-of-scope product, auto-generated from endpoint metadata |
| Consent | `ICanContact` unit tests + integration: no send path exists that bypasses it (architecture test scans for provider send calls outside the gate) |
| Publishing | Fake provider returns timeout/500/duplicate; assert status never `Published` without confirmation |
| Idempotency | Replay every job/webhook/booking 3×; assert one effect |
| Appointments | Concurrency test (parallel tasks) + DB constraint test |
| Budget | Property tests: sum of committed+actual never exceeds approved cap without an approval record |
| Analytics | Seeded dataset with nulls; assert tile shows "—" not 0, and freshness label |
| Audit | Every state-changing sensitive command has a test asserting an `AuditEvent` |
| Recovery | Force each failure type; assert job visible, retryable, or manually resolvable |
| Security | Dependency scan, secret scan in CI, OWASP ASVS-lite checklist per phase; webhook signature tests |
| Synthetic visuals (5A) | Every AI-made image carries `IsSynthetic`; no code path shows it without the "AI-generated" label; image prompts are tested against "fake screenshot / fake gameplay / readable text / real person" requests |
| Visual cost (5A) | A plan's cost estimate vs the ledger; an image or Canva call is refused once the product's AI cap is reached; no provider call happens in tests |
| Client isolation (5A) | Authorization matrix extended with client users: a client sees, approves and downloads only its own products' content and files |
| Quality control (5A) | No path from `Draft` to `Approved` skips the QC checklist; automatic checklist items cannot be set by a person |
| Platform specs (5A) | Media outside a network's specs (aspect ratio, duration, size, count) is refused at submit and again at publish, with the reason |

## 9. Key domain state machines (define before coding)

- **Content version:** Draft → InReview → (Approved | ChangesRequested | Rejected) → Scheduled → Publishing → (Published | Failed) ; Cancelled from any pre-Publishing state. Editing an Approved/Scheduled version creates a **new** Draft version and cancels the schedule.
- **Publishing job:** Queued → Validating → Sending → AwaitingConfirmation → Confirmed | Failed(retryable?) → DeadLettered.
- **Lead:** New → Contacted → Qualified | Disqualified(reason) → Converted(opportunity).
- **Opportunity:** per-pipeline stages; terminal Won(revenue)/Lost(reason).
- **Booking:** Requested → Confirmed → (Rescheduled | Cancelled | Attended | NoShow).
- **Budget item:** Recommended → Approved → Committed → Actual(reconciled); Increase/Reallocation = new Recommended requiring approval.
- **Workflow run:** Pending → Running → Succeeded | Failed | Paused | Cancelled; each action idempotent.

## 10. Budget discipline in the build

- Media spend: **USD 0 until** Phase 4 exit **and** Firebase/retention data (Phase 5) are trusted for the game. Pilot with organic content.
- Track four cost buckets separately in docs and the app: infrastructure, AI usage, creative tools, media.
- Default AI controls: per-product monthly AI cap with 80% alert; use the cheaper model tier for drafts/classification and the stronger tier only for strategy and budget scenarios (model chosen via config, not code).
- Infra target: staging + prod on small instances; shut staging outside working hours if cost matters.

## 11. Non-engineering critical path (do not let this block engineering at Day 20)

| Item | Owner | Needed by |
|---|---|---|
| Verified fact sheets: Ludo Fight (genre, features, release status, monetization, markets, store link, package `com.devorbit.ludofight`) | Product Owner | Day 10 |
| Verified fact sheet: Pharmacy POS (features, workflows, pricing model, geography, integrations, differentiators, buyer persona) | Product Owner | Day 10 |
| Brand voice rules + prohibited claims, per product | Marketing Operator | Day 12 |
| Real screenshots/gameplay footage, logos with ownership/rights notes | Marketing Operator | Day 18 |
| Social test account(s) + developer app approval (Meta app review can take weeks — start day 1) | Technical Lead | Day 1 |
| Email domain + sender authentication (SPF/DKIM/DMARC) | Technical Lead | Phase 3 start |
| Privacy/consent text for target markets | Product Owner (+ legal advice) | Before first real lead capture |
| Pilot operators committed to weekly feedback sessions | Product Owner | Day 20 |

## 12. Decisions to confirm in Phase 0 (with defaults so work is not blocked)

| # | Decision | Default if no answer by Day 5 | Blocks |
|---|---|---|---|
| D1 | .NET target | .NET 10 LTS | WP1.1 |
| D2 | Hosting provider/budget | AWS single VM + Docker Compose (ADR-08) | WP1.1 |
| D3 | Identity | ASP.NET Core Identity behind seam (ADR-06) | WP1.2 |
| D4 | Queue | Postgres job table (ADR-05) | WP1.4 |
| D5 | Object storage / secrets | S3 + SSM (ADR-09/10) | WP1.7 |
| D6 | **First social channel** | **Facebook Page** via Meta Graph API (simplest eligible; owner can pick another) | WP0.2, 3.1 — **blocker, decide Day 1** |
| D7 | WhatsApp / email | Email only in MVP; WhatsApp deferred | WP3.10 |
| D8 | Appointment use case | Pharmacy POS demo booking, internal staff calendars as source of truth, no external calendar sync in MVP | WP3.4 |
| D9 | CRM scope | TriSoftic's own prospects only; `OrganizationId` retained for future | WP2.B2 |
| D10 | Retention / consent rules per market | Strictest common baseline (explicit opt-in, easy opt-out, 24-month inactive-lead review) pending legal input | WP2.B1 |
| D11 | Team size & who is Product Owner / Technical Lead | Needed to calibrate durations | Planning |
| D12 | UI library | MUI | WP1.12 |
| D13 | Ludo Fight market list for localization | Only verified markets; none assumed | WP1.11 |
| D14 | **Who the visual system serves** — decided 16 Nov 2026 | Own products **and client businesses**, as "agency-lite" inside one organization (ADR-21); full multi-tenancy deferred to the Phase 6 gate | 5A.8 |
| D15 | **Design route** — decided 16 Nov 2026, **revised same day (owner has no Canva plan)** | In-house open-source template renderer **plus** AI images via Bedrock Stability (ADR-22). Canva deferred; staff may still design in free Canva and upload the file by hand | 5A.2, 5A.5 |
| D16 | **Video route** — decided 16 Nov 2026 | A person edits in CapCut and uploads the finished video; the platform writes the brief and publishes (ADR-23) | 5A.1, 5A.6 |
| D17 | **Metricool / Meta Business Suite** — decided 16 Nov 2026 | Not integrated; native Facebook publishing and figures stay (ADR-23) | — |
| D18 | Provider accounts for 5A | **None new.** Bedrock Stability needs model access enabled on the existing AWS account in `us-west-2` (verified by spike 5A.0(b)). An OpenAI key is optional. Any provider secret is stored encrypted, never in source control | 5A.2 |

## 13. Risks and early-warning triggers

| Risk | Early warning | Action |
|---|---|---|
| Social API access delayed (app review) | No approved app by Day 15 | Switch to channel with fastest access; keep manual "mark as published" path so the calendar still delivers value |
| Product facts not supplied | Fact sheets empty by Day 12 | Use AI to *draft questions*, not facts; hold Phase 1 exit |
| Scope creep into "all channels / predictive" | Any request not in §7 | Change control |
| AI cost drift | >80% product AI cap | Alert, downgrade model tier, review prompts |
| n8n becomes the brain | Business rule found in a workflow | Move to .NET; n8n only does I/O |
| Consent shortcuts | Any send outside `ICanContact` | Architecture test fails the build |
| Solo/low capacity | Slip >20% of phase | Cut within phase per §7, never cut kernels (K1–K5) or quality gates |
| Game analytics untrustworthy | Event QA fails | Keep paid UA off; fix instrumentation first |
| Staff cannot design templates visually (no Canva) | Requests for new looks that need a developer | Add templates in code as needed; if it becomes the bottleneck, build the deferred Canva implementation (free-trial spike first) or embed an open-source canvas editor |
| AI image model unavailable, renamed or refused access (Bedrock Stability model access, or a retired OpenAI model) | Spike 5A.0(b) cannot confirm the model id or access | Keep model ids in configuration (ADR-11), never in code; fall back to the OpenAI implementation or to templates + library only |
| AI visuals misread as real product footage | Any asset shown without its "AI-generated" label, or a prompt that reproduces UI | Gate "Synthetic visuals"; stop generation for that product until fixed |
| Posts invisible to the public (Meta app still in Development mode) | A post is visible only to app role-holders | 5A.0(d): switch the app Live before relying on public reach; until then treat Facebook output as private |
| Client approval becomes a bottleneck | Posts wait >2 working days for a client | Reminder email via the consent-gated mailer; per-client auto-approval is **not** offered (it would defeat the approval) |
| Scope growth from "clients" into full SaaS | Requests for client self-signup, billing, per-client databases | Change control; ADR-21 and the Phase 6 gate |

## 14. First 30 days — day-by-day

| Days | Work | Deliverable |
|---|---|---|
| 1–2 | Confirm D1–D13; open Meta developer app; create repo, ADR docs; start fact collection (§11) | Decision log; app review requested |
| 3–5 | Spikes 0.2, 0.3; data dictionary; product profile schema; wireframes | Phase 0 exit |
| 6–8 | WP1.1 repo/CI/CD/staging; WP1.2 identity & roles | Login on staging |
| 9–10 | WP1.3 audit; WP1.4 job queue; WP1.5 pause flag | Kernels K1, K2, K4 live |
| 11–13 | WP1.6 products & profile versions; WP1.8 approval engine | Pilot products registered (real data) |
| 14–16 | WP1.7 asset library; WP1.12 shell & product UI | Admin self-serve |
| 17–20 | WP1.9 AI abstraction + usage ledger; WP1.10 grounded generation + golden tests | Grounded drafts for both pilots |
| 21–25 | WP1.11 strategy workspace; WP2.A1–A2 campaigns & content versions | **Phase 1 demo** |
| 26–30 | WP2.A3 approvals UI; WP2.A4 calendar v1; WP2.B1 consent service; Phase 2 backlog refined | **Internal alpha** |

## 15. Cadence and governance of the build

- **Weekly:** demo to operators (real data, staging); backlog regroom against this plan.
- **Per phase:** exit review against the phase's exit test and quality gates; no phase starts the next one's WPs unless dependencies are done (kernels excepted — they may start early).
- **Change control:** Product Owner approves scope changes; Technical Lead approves ADR changes (append a new ADR, never silently edit).
- **Metrics we track on ourselves:** WPs done vs. planned, gate pass rate, escaped defects, job failure rate on staging, AI cost per generated approved draft.

## 16. Immediate next actions (when you say "start")

1. Resolve D6 (first social channel) and D11 (team size) — only these two actually change the plan.
2. Scaffold the solution per §4 and stand up CI + staging (WP1.1).
3. Build kernels in order K1 → K2 → K4 → K3 → K5 (K5 can slide to Phase 2 start).
4. Begin product profile and fact collection in parallel — it is the real critical path.
