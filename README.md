# Vendor Scheduler

Backend service that assigns incoming translation jobs to vendor partners, reliably and safely under
duplicate, concurrent and partially-failing conditions.

Submission for the Senior Backend take-home challenge. The original brief is kept at
[`docs/tech-challenge.md`](docs/tech-challenge.md) for reference.

---

## Status

> This section is the honest picture of what is implemented right now. It is updated as work
> progresses, so a reviewer never has to guess whether a documented behaviour is real code or intent.

| # | Area | Status |
|---|------|--------|
| 1 | Baseline fixed and building, first test in place | ✅ done |
| 2 | Idempotency: deterministic replay + in-progress rejection | ⬜ pending |
| 3 | Vendor call resilience (timeout, bounded backoff, transient vs terminal) | ⬜ pending |
| 4 | Assignment decision: priority, due date, tie-breaks | ⬜ pending |
| 5 | API layer: validation, status codes, vendor repository | ⬜ pending |
| 6 | Observability: structured logs, correlation id, metrics | ⬜ pending |
| 7 | Full test suite (happy path, no vendor, replay, retry, concurrency) | 🟡 1 of 5 |
| 8 | Design note + incident playbook | 🟡 in progress |

---

## Stack

- .NET 10 / ASP.NET Core Minimal API
- xUnit

## Project layout

```
src/VendorScheduler.Api             HTTP entrypoint
src/VendorScheduler.Core            Domain model, contracts and assignment engine
src/VendorScheduler.Infrastructure  In-memory adapters (idempotency store, vendor gateway, publisher)
src/VendorScheduler.Tests           Unit tests for the assignment engine
```

Dependencies point inwards: `Core` has no reference to `Infrastructure` or `Api`, and the test project
targets `Core` only, using its own in-file test doubles.

---

## Run instructions

Requires the .NET 10 SDK. `global.json` pins `10.0.203` with `rollForward: latestFeature`, so any
10.0.x feature band builds the solution.

```bash
dotnet restore
dotnet build
dotnet test
```

Run the API:

```bash
dotnet run --project src/VendorScheduler.Api
```

Swagger UI is served at `/swagger` in the Development environment.

---

## API

### `POST /api/assignments`

Request:

```json
{
  "jobId": "00000000-0000-0000-0000-000000000000",
  "sourceLanguage": "en",
  "targetLanguage": "de",
  "priority": "normal",
  "dueAtUtc": "2026-07-01T12:00:00Z"
}
```

Response:

```json
{
  "success": true,
  "jobId": "...",
  "vendorId": "...",
  "reason": "Assigned",
  "idempotencyKey": "assign:..."
}
```

The `jobId` is the idempotency key of the request. Repeat submissions of the same `jobId` are
answered according to the replay rules below.

---

## Design note

### The baseline did not compile

`AssignmentEngine` called `idempotencyStore.CompleteAsync(...)`, which does not exist on
`IIdempotencyStore`. This was treated as a design question rather than a typo: the interface exposes
`SaveCompletedAsync` **and** `ReleaseInFlightAsync` precisely because completing an assignment and
releasing an in-flight lease are two different outcomes, and collapsing them into one call is what
made the baseline unsound.

### Success is durable, failure is retryable

The engine now distinguishes the two explicitly:

- **Success** → the result is persisted via `SaveCompletedAsync`, then the event is published.
- **Failure** (no vendor, reservation rejected, exception) → nothing is persisted, so the job stays
  legitimately retryable.
- **Always** → the in-flight lease is released in a `finally`, so a crash inside the critical section
  cannot leave the key held for the lifetime of the process.

Persisting **before** publishing is deliberate: if the publisher throws, the assignment is not lost.
The residual gap — persisted but never published — is what the outbox proposal below addresses.

### Concurrency and duplicate handling

`TryStartAsync` is backed by an atomic compare-and-set (`ConcurrentDictionary.TryAdd`). Two requests
carrying the same `jobId` microseconds apart cannot both enter the critical section, so a duplicate
can never produce a second vendor reservation or a second event.

The chosen response semantics (pending implementation, step 2):

| Situation | Response |
|-----------|----------|
| First request | `200` with the assignment result |
| Duplicate while the original is **still in flight** | `409 Conflict` — "already in progress", client retries |
| Duplicate after the original **completed** | `200` replaying the original stored result verbatim |
| Retry after a **transient** failure | executes a genuine new assignment attempt |

The in-flight duplicate is rejected rather than parked waiting for the winner. It is the simpler and
more predictable contract: no request is held open on a lock it does not own, and the client already
has to implement retries for transient vendor failures, so the retry path is reused instead of
introducing a second waiting mechanism.

### Tradeoffs and known limitations

These are inherent to an in-memory implementation and are deliberately **not** solved in code:

| Limitation | Consequence | Production answer |
|------------|-------------|-------------------|
| Orphaned in-flight key after a process crash mid-assignment | That `jobId` stays blocked | TTL / lease expiry on the in-flight key (Redis `SET NX PX`) |
| Vendor call times out but the reservation actually landed | Capacity consumed without a recorded assignment | Send `jobId` as the vendor-side idempotency key so the vendor deduplicates |
| Process restart clears the store | All replay history lost | Durable store (Redis or the service database) |
| Persisted but not published | Event silently missing | Outbox (below) |

### Outbox proposal (design only)

The brief accepts a design-only answer here, so it is described rather than built.

Instead of publishing inline, the assignment result and its event row would be written in the **same
local transaction**, making "assignment recorded" and "event queued" atomic. A `BackgroundService`
inside the same process then polls pending rows, publishes them, and marks them dispatched, retrying
on failure. This yields at-least-once delivery; the consumer deduplicates on the `idempotencyKey`
already carried in the payload, which is what makes redelivery harmless.

It is not implemented because there is no transactional store in this codebase — an outbox on top of
an in-memory dictionary would demonstrate the shape while providing none of the actual guarantee.

### Intentionally out of scope

Durable persistence, authentication/authorization, a real vendor HTTP client, rate limiting, and
horizontal-scale coordination. The brief's timebox is spent on assignment correctness, idempotency
and operability instead.

---

## Metrics

To be completed in step 6.

## Incident playbook

To be completed in step 8.
