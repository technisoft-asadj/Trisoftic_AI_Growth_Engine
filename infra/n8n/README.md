# n8n integration

n8n is used for **input and output only** (calling a provider, receiving a result). Decisions stay in the platform's workflow engine.
Both directions are signed with HMAC-SHA256 and a shared secret, one secret per webhook name.

## Configure a webhook (platform side)

```
Automation__Webhooks__crm-sync__Url    = https://n8n.example.com/webhook/crm-sync     # where the platform calls n8n
Automation__Webhooks__crm-sync__Secret = <long random string, same value in n8n>      # signs both directions
```

A webhook is only offered in the workflow builder once both values exist. Never commit real values.

## Platform -> n8n (the `webhook` step)

`POST <Url>` with a JSON body `{ runId, step, eventId, workflow, subject: {type, id}, context: {...} }` and headers:

| Header | Meaning |
|---|---|
| `X-TE-Timestamp` | Unix seconds when the call was made |
| `X-TE-Signature` | `sha256=` + hex(HMAC-SHA256(secret, `"{timestamp}.{raw body}"`)) |
| `X-TE-Idempotency-Key` | `{runId}:{step}`; the same step is never meant to act twice, so ignore a repeat |

In n8n, verify before doing anything (Code node):

```js
const crypto = require('crypto');
const secret = $env.TE_CRM_SYNC_SECRET;
const ts = $input.first().json.headers['x-te-timestamp'];
const sig = $input.first().json.headers['x-te-signature'];
const raw = JSON.stringify($input.first().json.body);            // use the raw body if your trigger exposes it
const expected = 'sha256=' + crypto.createHmac('sha256', secret).update(`${ts}.${raw}`).digest('hex');
const fresh = Math.abs(Date.now() / 1000 - Number(ts)) < 300;
if (!fresh || !crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(sig))) throw new Error('bad signature');
return $input.all();
```

The platform treats a 5xx or 429 answer as temporary (retried with backoff) and any other non-2xx as a permanent failure that needs a person.

## n8n -> platform (starts workflows)

`POST https://<platform>/api/v1/webhooks/automation/<name>` with body:

```json
{ "eventType": "sync-done", "subjectType": "lead", "subjectId": "<guid>", "productId": "<guid>", "data": { "any": "string facts" } }
```

signed the same way (`X-TE-Timestamp`, `X-TE-Signature`). It starts workflows whose trigger is `external.sync-done`.
The platform rejects: a wrong signature or changed body (401), a timestamp more than 5 minutes off (401), and an
identical request sent a second time (409, replay). Unknown names return 404. Sign with:

```js
const body = JSON.stringify(payload);
const ts = Math.floor(Date.now() / 1000);
const sig = 'sha256=' + crypto.createHmac('sha256', secret).update(`${ts}.${body}`).digest('hex');
```

Send exactly the bytes you signed.

## Status

The signing and verification are covered by automated tests on the platform side. **No n8n instance has been connected yet**, so the
snippets above are unexecuted examples. Export n8n workflows into this folder (`infra/n8n/*.json`) so they are versioned with the code.
