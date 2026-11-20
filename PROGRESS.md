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

Tests at last run: 136 unit + 17 architecture + 138 integration = **291 passing**. Web typecheck and build pass; the web app was driven in headless Edge (see Production deployment).

## Next steps when work resumes (in order)

1. Phase 5-6 per IMPLEMENTATION_PLAN.md (needs real data first). Also the outstanding gaps: real Facebook/SMTP/n8n/Bedrock-5.x verification, S3 storage, CI run, merge undo, saved segments, contact/company create forms, a deeper browser pass over the CRM, content and automation screens.
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

- Browser check (headless Edge against the compose stack): signed in, all 10 pages render with no page errors; created a product and a budget, requested launch, approved it, recorded spend, saw an over-cap spend refused, generated a planner learning experiment. Scripts were throwaway (not committed). CRM, content, automation and publishing screens were only loaded, not exercised.

- Second browser pass (same method): CRM end to end (form lead -> contacted -> qualified -> deal -> stage moves -> won with revenue; consent, notes, setup/messages/appointments tabs); content (create, edit as v2, submit, approve, schedule, calendar) and the **worker really publishing a scheduled post to the sandbox channel**; automation (build a workflow in the UI, turn it on, a public lead fires it, the run log shows the step). All without errors. Findings fixed: a stale "arrives in Phase 3" message on the content page; the automation task step's assignee box now explains owner/none/user id (a form lead has no owner, so "owner" tasks are skipped and the log says so). Rough edges left: the automation pickers show raw ids (lead.created, waituntil). Compose gained an optional PUBLISHING_ALLOW_SANDBOX (demo channel, keep false in production).
- Third browser pass (product screens). **Bugs found and fixed:** (1) uploading any asset failed with a 500 in the container because the uploads volume was owned by root (Dockerfile now creates /data/objects owned by the app user); (2) clicking a product tab (e.g. AI studio) while on another tab went to a blank page such as /profile/generate, because the tab links were relative (now absolute; same for the strategy link from AI studio). Also: automation pickers now show plain-language names (ids unchanged); compose passes AI_PROVIDER / ANTHROPIC_API_KEY / OPENAI_API_KEY. Verified: profile draft -> submit -> approve, asset upload, campaigns, strategies tab, and the AI studio shows a clear 'provider not configured' message. **Not verified: an actual AI generation (needs a real key or Bedrock access)**.


## Real Facebook post and the growth loop (16 Nov 2026)

- **First real post published** to the connected Facebook Page through the whole path (AI draft from the approved profile -> approval -> schedule -> worker -> Facebook). "Published" was set only after Facebook returned the post id. Seven daily posts for Ludo Fight are scheduled (17-22 Nov, 18:00 Pakistan time).
- **AI via Bedrock** (us-east-1) works from the compose stack: grounded drafts cite verified facts; the Play Store link is a profile fact (category Platform), so posts carry the real URL instead of a placeholder.
- **Post results (Publishing):** `PostMetricSnapshot` per published post, pulled every 3 hours (`publishing.pull-post-metrics`), plus "refresh now". Each figure (reactions, comments, shares, views, reach, clicks) is requested on its own; a refused figure stays **null with the reason noted** and never becomes zero. Per-day sums go to analytics only when every post that day has the figure. Verified live: shares came back (0); reactions/comments need `pages_read_user_content` and views/reach/clicks need `read_insights`, which the current Facebook connection was not granted. **Reconnecting Facebook after adding both permissions to the Meta app is required** (the connect URL now requests them).
- **Growth plans (new `Planning` module, own schema):** create a 3-day / week / month plan from a goal; the planner (AI, grounded in the approved profile) proposes angles, a rationale and how to judge the plan while **code** works out how many posts and when. "Write remaining posts" drafts each slot (one call per slot) as ordinary content submitted for approval (placeholders in a draft are refused); "Schedule approved posts" schedules only approved posts at their planned times; "Review results" analyses the figures and proposes the next plan, which can be created with the learnings carried in. The review quotes only figures it was given (anything else is sent back once, then refused), marks missing figures "not reported", forces low confidence under 5 measured posts, and **does not call the model at all when there is nothing to analyse**. Modules talk through new BuildingBlocks contracts (`IContentDrafter`, `IContentPlanner`, `IPostPerformanceReader`).
- Tests: 149 unit, 18 architecture, 142 integration (planning loop with scripted model and network). Content tests now measure "in the future" on the server's fake clock (they used wall-clock time and broke once the suite's clock drifted).
- **Not built yet:** automatic review when a plan ends (the review is a button); auto-scheduling without per-post approval; images/video/stories; competitor/market research; experiments and attribution (Phase 5 remainder); Facebook account-level insights (page followers/reach).

## Media posts: photos, video and stories (16 Nov 2026)

- **Files on a post:** a content version carries a list of asset ids (`ContentVersion.AssetIdsJson`), so **approval covers exactly those files**; editing without mentioning files keeps them. Formats: **Post** = text, 1-10 photos, or one video (not mixed); **Story** = exactly one image (a draft may start without one; submit and publish require it). Video stories are not supported yet.
- **Rules (`MediaRules`, shared by content and publishing so they cannot disagree):** other product's file, retired file, `InternalOnly` rights, expired rights, non-image/video types are refused when attaching, again on submit, and **again at the moment of publishing** (a file retired after approval fails the post instead of going out). A Post with media still needs its text: it is how a retry recognises the post.
- **Facebook adapter:** text -> `/feed`; one photo -> `/photos` (published, text as caption); several -> unpublished uploads attached to one `/feed` post; one video -> `/videos` (long-timeout client, streamed from storage, not held in memory); story -> unpublished photo then `/photo_stories`. After an uncertain failure a story is **not** retried blindly (Facebook offers no reliable lookup): the failure says to check the Page and mark it published or retry. **Not yet exercised against real Facebook** (needs a real image post approved by the Page owner).
- **Plumbing:** new `IAssetCatalog` contract (assets module), content and publishing never reference the assets module; the worker now registers the assets module (same `objects` volume) so it can read the files.
- **Bug found by checking the live system and fixed:** the first version of the migration filled existing rows with `{}`, so every pre-existing post returned 500 when opened. A data-fix migration sets them to `[]`, the column default is `[]`, and reading is tolerant. Automated tests missed it because they start from an empty database.
- Web: photo/video picker (thumbnails, selection order = posting order) in New content and in the editor; a Story format.
- Tests: 155 unit, 18 architecture, 144 integration.
- **Not built:** AI-generated images, video stories, reels, a plan that attaches images to its posts, Instagram.

## Plan updated for visual content production (16 Nov 2026)

Owner supplied a five-step "AI Content Production System" and a tool list (ChatGPT, Canva, CapCut, Metricool, Meta Business Suite). `IMPLEMENTATION_PLAN.md` now has **Phase 5A** (work packages 5A.0-5A.9), ADR-21 to ADR-24, decisions D14-D18, five new quality gates and six new risks. **Change control:** 5A is post-MVP and runs before the old 5.1-5.7, which move later.
- Decided by the owner: serve own products **and client businesses** (built as "agency-lite" inside one organization, not as separate tenants); **Canva templates + OpenAI images**; **a person edits video in CapCut** and uploads it; **Metricool and Meta Business Suite are not integrated**.
- Findings behind it: CapCut has no official public API; Metricool's API needs a paid Advanced plan; Meta Business Suite has no API; Canva's docs disagree on which plans may use autofill, so a spike with the owner's real plan comes first (5A.0); no text-to-image model is available on the current Bedrock account; the OpenAI image model lifecycle is reported inconsistently.
- First build step is 5A.0 (spikes), then 5A.1 (Reels and video stories), which needs nothing external.
- **Revised the same day (owner has no Canva plan and prefers open source / lower cost):** Canva is **deferred**. Design production is an **in-house open-source template renderer** (HTML/SVG brand templates rendered to PNG with the Chromium already in the toolchain); AI images default to **Stability models on Bedrock in us-west-2** (SD3.5 Large, Stable Image Core/Ultra appear in the account's region catalogue; model access still has to be enabled and priced by spike 5A.0(b)); OpenAI is an optional second implementation. No paid subscription is required. Plan sections changed: ADR-22, D15, D18, 5A.0, 5A.2, 5A.5, the not-in-5A list and two risks.
- **Bedrock image access verified (16 Nov 2026):** the console's "Model access" page is retired (serverless models enable on first use). One real call to `stability.stable-image-core-v1:1` in `us-west-2` returned a 1:1 PNG (a dice pattern, no text). AI imagery shows small artefacts (malformed dice faces), which is why 5A.7 keeps a human QC step. The per-image price still has to be read from the AWS bill.

## 5A.1 Reels and video stories (16 Nov 2026)

- **Formats:** `Reel` (exactly one vertical video, text is the description) and `Story` now takes one image **or** one vertical video. Rules (`MediaRules`): 9:16 within 2%, at least 540x960, MP4; Reel 3-90 s (Meta's published Reels spec), video story 3-60 s (**Meta publishes no length limit for video stories: this is a cautious assumption to confirm on the real Page**). A video whose size or length cannot be read is refused for a Reel/Story (not guessed); a plain video post has no shape rules.
- **Files are measured, not trusted:** a built-in MP4 reader (`Mp4Probe`, no outside tool) reads length and picture size from the file's own index when it is uploaded (index at the start or the end; sideways phone video reported as seen). Verified against four real MP4s: exact dimensions and length within 0.25 s of what a browser reports. Stored on the asset as nullable `DurationSeconds/Width/Height` (unknown is null, never 0).
- **Facebook adapter:** reel and video story use the same three-step upload (open session, stream the file to the upload address, finish), then a ~30 s status check to catch an immediate processing rejection. Reels are looked up through the Page's videos after an uncertain failure; video stories cannot be looked up, so they are not retried blindly (same as photo stories). The flow follows Meta's published description and **has not run against real Facebook**; Reels may also need an extra permission that only the live test will reveal.
- Web: videos show length and size in the picker; each format shows its rules.
- Tests: 163 unit, 18 architecture, 145 integration.

## 5A.2 AI pictures (19 Nov 2026)

- **What it does:** `POST /products/{id}/ai/images` (permission `ai.generate`) makes one picture from a short description and a shape. Default model `stability.stable-image-core-v1:1` on Bedrock in us-west-2, using the same AWS credentials as Bedrock text. The result is filed in Assets as an AI-generated, synthetic file with the date, model, profile version and the description in its rights notes, and can be attached like any other file.
- **What an AI picture may not be (ADR-24), checked before anything is sent or paid for:** a screenshot, app screen or UI; gameplay; readable text; a logo or other brand; a famous or real person; **and any scene with people**. The last rule was added after two live tries: even with "illustration" wording and a negative prompt, the model returned photo-realistic strangers, which is misleading in marketing. The reply says why and suggests objects, places or a cartoon character. The model's own safety filter also counts as a refusal and costs nothing.
- **Cost:** recorded in the existing AI ledger (purpose `image.generate`), counted against the per-product and global monthly budgets and the pause switch, plus a hard cap of 40 pictures per product per month. The price (about $0.04 per picture for Core, estimate from AWS list prices) is configurable under `Ai:Images:PricePerImageUsd` and has not been checked against a real AWS bill yet.
- **Live check:** two real pictures were made for Ludo Fight (about $0.08); both were filed correctly. `style_preset` is not accepted by the Core model on Bedrock.
- Web: "Make a picture with AI" in the media picker for Posts and Stories; AI files carry an "AI" tag.
- Tests: 177 unit, 18 architecture, integration suite passing (images: filing, cost, refusals, monthly cap, permissions, attach to a post).

## Plan update: Phase 5B, Facebook reach and followers (20 Nov 2026)

- A research agent produced a report of 14 growth ideas from the plan, the progress notes and Meta's current published rules. The owner reviewed it and answered the six open questions; the result is **Phase 5B (5B.0 to 5B.11)** in `IMPLEMENTATION_PLAN.md`, plus **ADR-25** (growth ethics: nothing Meta prohibits, no automatic comments, no like-gating).
- Owner answers: weekly original vertical video will be produced; USD 20-50 paid boost wanted within six months (research spike first); calendar may use any market or festival with a source and named reviewer; comment replies drafted by the platform and approved by a person (`pages_manage_engagement` request being prepared); Page is linked to Instagram and Instagram is wanted; installs cannot be attributed, so success is link clicks only.
- Build order: 5B.0 spike, 5B.1 Page-level figures, 5B.2 tags and leaderboard, 5B.3 hook tests, 5B.5 weekly auto-review; 5B.6 comments inbox starts early because Meta's review takes weeks.

## 5B.0 spike and 5B.1 Page health (20 Nov 2026)

- **Spike result (real Facebook, Ludo Fight Page, API v26.0):** Facebook returned all five Page figures with the current metric names: `followers_count` (Page field), and daily insights `page_media_view` (views), `page_total_media_view_unique` (reach), `page_daily_follows_unique` (new followers) and `page_daily_unfollows_unique` (unfollows). First readings: 8 followers; 6 new followers, 66 views and 8 reach on 19 Nov. Per-Reel figures (followers gained, watch time) are **not yet tried**: no Reel has been posted. The Instagram link and Marketing API boosting rules are **not yet checked** (5B.0 stays open for those).
- **What was built:** `PullPageFiguresHandler` (every 6 h and on demand) keeps one row per figure per day in `publishing.page_figures`, with which Facebook metric name answered; a figure Facebook refuses is stored as missing **with the reason**, and a later refusal never erases a figure that was reported. Metric names are in configuration (`Publishing:Facebook:PageMetrics:<figure>` = comma-separated names, tried in order) because Meta renames them. Followers are also recorded in analytics as a level. `GET /publishing/products/{id}/page-health` returns the latest value, the last 28 days and the follower change since the oldest reading of the last 7 days (null until there are two different days); `POST .../page-health/refresh` pulls now. Web: a **Page health** panel on Publishing, Published tab.
- **Caveats:** the newest day is a partial count that grows until Facebook closes it (each pull refreshes it); likes (`fan_count`) are never substituted for followers; the day of a daily value is taken as the day before the period's end time (Meta's convention, to be confirmed against Meta's reporting timezone).
- Tests: a new integration test covers refused figures (empty with reason), a real zero kept, a refusal not erasing a reported figure, and the follower change; whole suite passing.

## 5B.6 Comments inbox and reply assistant (20 Nov 2026)

- **Flow:** every 30 minutes (and on demand) the worker reads the newest top-level comments on each published post of the last 30 days into an inbox (`publishing.post_comments`, one row per comment; the Page's own comments are skipped). **Suggest a reply** (one comment, or up to 10 new ones per click) asks the AI to classify the comment (Question, Praise, Complaint, BugReport, Spam, Abuse, Other) and write a short reply from the verified product facts only. The comment is fenced as untrusted text; a reply that uses a prohibited claim or a figure not in the profile or the comment gets one fix attempt and is otherwise refused. Spam and abuse get **no reply**, only a suggestion to hide. When the profile lacks a needed fact the AI writes no reply and says which fact is missing. **A person edits the text and presses Approve and send** (publishing permission); nothing else ever posts a reply.
- **Safeguards:** one reply per comment (claimed atomically, so a double click cannot send twice); the same text cannot go to a second person within 24 hours; a ceiling of 20 replies per hour per Page (`Publishing:Comments:MaxRepliesPerHour`); the publishing pause switch holds replies; hiding needs its own confirmation; a hidden or ignored comment is never answered. An uncertain send (timeout) is not retried automatically: the screen says to check the comment on Facebook first. A permission refusal tells the owner to reconnect with `pages_manage_engagement`.
- **Permission:** the Facebook connect link now also asks for `pages_manage_engagement`. **The Page must be reconnected** before replying or hiding works. Reading comments already works with the permissions held: a live check on 20 Nov 2026 read the newest post's comments (0 so far).
- Web: Publishing, **Comments** tab (suggest, edit, approve and send, ignore, hide, test connection). API: `GET/POST /publishing/products/{id}/comments[...]`, `POST /publishing/comments/{id}/draft|send|hide|ignore`, `PUT .../draft`.
- **Not done:** support and bug comments do not yet create CRM tasks (needs a small CRM contract); Meta's Testing screen needs one real reply to turn the `pages_manage_engagement` test green; the review recording still has to be made.
- Tests: 8 new integration tests (pull idempotent and own-comments skipped; grounded draft, spam, missing fact; prohibited claim refused and nothing saved; exact text sent once; duplicate text refused; failed and uncertain sends; hide and ignore; permissions, pause and hourly ceiling; connection check). Whole suite passing: 177 unit, 18 architecture, 165 integration.

## 5B.2 Tags and leaderboard (20 Nov 2026)
- Every post now carries a **hook type** next to its format (fixed list in `HookTypes`: Question, Challenge, Scenario, Tip, Announcement, Behind the scenes, Fun fact, Direct call to action). It is a label, not approved text: `PUT /content/{id}/hook-type` changes it at any time without a new approval, and every change is audited. Plan slots carry a planned hook type onto the drafted post.
- **Leaderboard** (`GET /products/{id}/leaderboard`): published posts grouped by format, hook type, weekday and time slot (zone from `Planning:Leaderboard:UtcOffsetHours`, default UTC). Averages use only posts that have the figure (unreported is never zero). A group with fewer than 5 measured posts is labelled **too few** and never ranked above a solid one. Shown on Publishing, Published tab.
- **The planner must cite it**: the table is put in the planning prompt; a plan that quotes a figure not in the table (or in the verified profile), or any percentage, is sent back once and then refused (422). No table means no figure may be quoted.
- New contract `IContentCatalog` (content to planning); migrations `HookType` (content) and `SlotHookType` (planning). Tests: 8 unit (`LeaderboardTests`), 1 integration (tags to leaderboard to planner refusal). 369 tests pass. Live check on the real Page: 5 published posts, all untagged, every weekday and time-slot cell marked too few.
- Not done: the visual dimension of the leaderboard (waits for 5A.9); weekday and time slots use one configured zone for all products.

## Autopilot and the AI reviewer (20 Nov 2026) - ADR-26
Owner decision: no waiting for approval; one AI agent approves, the whole loop runs by itself. Chosen: posts fully automatic from day one; **comment replies stay manual** until Meta approves `pages_manage_engagement` (the review description says a person approves each reply); fully automatic from day one (no shadow week).
- **Autopilot** (Planning module, `Autopilot*.cs`, `PlanOps.cs`): per product, off by default. A loop every 5 minutes (`Autopilot:RunLoop`, `Autopilot:IntervalSeconds`) does: review finished plans, keep a plan covering the next days (built from the last review and the leaderboard), write posts due within the lookahead, have the reviewer decide, schedule what passed (with a per-day cap), publish via the existing pipeline. It touches only plans it created (`GrowthPlan.Managed`); a person's own posts and plans are never decided by the agent.
- **AI reviewer** (`AutopilotGates`, `AutopilotReviewer`): fixed code checks run first and cannot be overridden (placeholders, engagement bait, prohibited claims, guarantees/superlatives, figures not in the verified profile, links not in the profile, tagging, shouting/hashtags, near-duplicate of a post in the last 30 days). Only a post that passed all of them is shown to a second AI call with a strict rubric; it returns approve/reject/escalate, and any blocker it lists stops an approval. Unsure or unusable answer = escalate to a person, never approve. The reviewer decides under a fixed identity (`AgentIdentity.ReviewerId`) through the same version-checked handler (`IAgentApprovals`), audited.
- Rejected: reason is recorded, the slot is rewritten with the reason (max 3 attempts, then "Needs you"). Escalated posts wait for a person (Approvals) and are not re-asked. Every decision stores its checks (`autopilot_decisions`). Safeguards: pause switches (Ai, Publishing), the AI monthly budgets, daily post cap, one run per product at a time.
- UI: Autopilot page (on/off with confirm, run now, settings, Needs you, decision log with checks). Migration `Autopilot`; `IContentCatalog` gained day counts and recent bodies. 396 tests pass (205 unit, 173 integration, 18 architecture). Deployed live with the autopilot OFF.
- Limits: the reviewer is the same model family as the writer, so independence is partial; the fixed checks carry most of the safety. The loop runs inside the API container, so posts only go out while the PC/Docker is up. Not yet: automatic comment replies, tracked links, hook tests.
