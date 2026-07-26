# Incident Playbook — Assignment Failures

**Scope:** jobs are not getting assigned, or are failing more than usual.
**First stop:** `GET /metrics` — the outcome counters tell you which section of this page you are in.
**Every response carries `X-Correlation-Id`:** ask the reporter for it and grep the logs to see that
exact request end to end.

## Triage — first 60 seconds

| You see (in `/metrics`) | It means | Go to |
|---|---|---|
| `NoCapacityAvailable` climbing | Vendors are full, service is healthy | §1 |
| `VendorUnavailable` climbing | A vendor is not answering | §2 |
| `vendorAttemptsByStatus.Uncertain` > 0 | Possible double reservations | §3 |
| `assignmentsReplayed` climbing | Clients are re-sending requests | §4 |
| Callers report `409` repeatedly for one job | A jobId appears stuck | §5 |
| `successRate` down, none of the above | Look at request validity | §6 |

## §1 — No capacity (`NoCapacityAvailable`)

**Confirm:** `GET /api/vendors` — are all capable vendors at `currentLoad ≥ maxCapacity`?
**This is a business problem, not an outage.** The service is correctly refusing work nobody can take.
**Mitigate:** notify capacity/vendor management to raise limits or onboard vendors. Callers already
receive `503` and should keep retrying — jobs escalate to urgent on their own as deadlines approach,
so they route to the best vendor once capacity frees up.
**Do not** page engineering for this counter alone.

## §2 — Vendor unreachable (`VendorUnavailable`)

**Confirm:** logs at `Error` level: `Vendor {name} unreachable for job {id} after N attempt(s)`.
The retry policy (3 attempts, exponential backoff, 2s per-attempt timeout) has already been exhausted
by the time this outcome appears — do not re-run it manually.
**Mitigate:** escalate to the vendor's support with the timestamps and correlation ids from the logs.
Traffic fails over to the remaining ranked vendors automatically; if only one vendor serves the
affected language pair, treat as §1 until the vendor recovers.
**Page:** yes, if the counter keeps climbing for more than 5 minutes.

## §3 — Uncertain outcomes (possible double booking)

**What happened:** an attempt timed out or returned an uninterpretable answer. The vendor may have
reserved capacity we never recorded.
**Mitigate:** reconcile with the vendor — list our recorded assignments for the window
(`GET /api/assignments/{jobId}` per affected job, correlation ids from the logs) against the vendor's
records, and release anything they hold that we did not record.
**Why this is manual:** the gateway does not yet send `jobId` as a vendor-side idempotency key; until
a real vendor client supports that, dedup on their side cannot be assumed. This is the known-risky
path — treat any non-zero count as worth a look the same day.

## §4 — Replay rate climbing (`assignmentsReplayed`)

**What it means:** replays are *correct* behaviour (same result returned, vendor not called twice) —
but a climbing rate says clients did not get, or did not trust, their first response.
**Diagnose:** a symptom, not a cause. Check response latency and whether §2 backoffs are stretching
request times; check whether one caller dominates (correlation ids reveal the pattern).
**Mitigate:** fix the underlying slowness; if one integration is retry-storming, talk to that team —
the idempotency layer is absorbing the damage meanwhile.

## §5 — A jobId appears stuck (`409` with no stored result)

**Confirm:** `POST` for the job returns `409 AlreadyInProgress`, but `GET /api/assignments/{jobId}`
returns `404` — in-flight lease held, no completed result, and no active request working on it.
**Likely cause:** the process died mid-assignment and the in-flight key was orphaned (known
limitation: no TTL on in-memory leases).
**Mitigate:** restarting the service clears the in-memory store, releasing the lease (it also clears
replay history — acceptable while storage is in-memory; both move to a TTL'd durable store in
production). If the job's assignment is in doubt, reconcile with the vendor as in §3 first.

## §6 — Success rate down without a clear counter

**Check `400`s:** malformed requests never reach the engine (`ValidationErrorResponse` lists every
problem). A spike usually follows a client deploy.
**Check `NoMatchingVendor` (`422`):** a language pair nobody serves. If the pair *should* be served,
the roster's `SupportedPairs` is stale — that is vendor data, not code.

## What this design cannot mitigate (accepted, documented)

- **Orphaned leases** need a manual restart (§5) until leases live in a durable store with TTL.
- **Process restart loses all replay history** — clients re-executing afterwards get fresh (correct)
  assignments, but duplicate *events* are possible across the restart boundary; consumers dedupe on
  `idempotencyKey`.
- **Uncertain ≠ resolved:** true double-booking prevention requires the vendor to accept `jobId` as an
  idempotency key (§3).
- **Counters are per-instance** and reset on restart; trends across restarts need the production
  metrics backend (OpenTelemetry), not this snapshot.
