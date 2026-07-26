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
| 2 | Idempotency: deterministic replay + in-progress rejection | ✅ done |
| 3 | Vendor call resilience (timeout, bounded backoff, transient vs terminal) | ⬜ pending |
| 4 | Assignment decision: priority, due date, tie-breaks | ⬜ pending |
| 5 | API layer: request validation | 🟡 endpoints and status codes done, input validation pending |
| 6 | Observability: structured logs, correlation id, metrics | ⬜ pending |
| 7 | Test suite (happy path, no vendor, replay, retry, concurrency) | 🟡 4 of 5 — retry lands with step 3 |
| 8 | Design note + incident playbook | 🟡 design note in progress, playbook pending |

Current suite: **6 tests, all passing**.

---

## Stack

- .NET 10 / ASP.NET Core Minimal API
- xUnit

## Project layout

```
src/VendorScheduler.Api             HTTP entrypoint
src/VendorScheduler.Core            Domain model, contracts and assignment engine
src/VendorScheduler.Infrastructure  In-memory adapters (idempotency store, vendor gateway, publisher, vendor directory)
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

Then open **http://localhost:5080/swagger** to exercise every endpoint interactively.

> The default ports of the original starter (`57082`/`57083`) fall inside a TCP range that Windows
> reserves for Hyper-V/Docker, which makes the host fail at startup with
> `An attempt was made to access a socket in a way forbidden by its access permissions`. They were
> moved to `5080`/`7080`. On Windows the reserved ranges can be listed with
> `netsh interface ipv4 show excludedportrange protocol=tcp`.

---

## API

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/api/assignments` | Assign a translation job to a vendor |
| `GET`  | `/api/assignments/{jobId}` | Look up the recorded assignment for a job |
| `GET`  | `/api/vendors` | List the vendor roster used for assignment decisions |
| `GET`  | `/health` | Liveness probe |

The read endpoints exist so the idempotency behaviour can be observed directly from Swagger: assign a
job, then re-post the same `jobId` and query it, without needing to read the service logs.

### `POST /api/assignments`

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
  "jobId": "...",
  "outcome": "Assigned",
  "idempotencyKey": "assign:...",
  "vendorId": "...",
  "isReplay": false,
  "success": true,
  "reason": "Assigned"
}
```

`jobId` is the idempotency key of the request. Outcomes map to HTTP status codes so a caller can act
on the response without parsing the body:

| Outcome | Status | Meaning for the caller |
|---------|--------|------------------------|
| `Assigned` | `200` | Assigned. A replay of a previous assignment also returns `200`, plus the header `Idempotent-Replay: true` |
| `AlreadyInProgress` | `409` | The same `jobId` is being processed right now — retry shortly |
| `NoMatchingVendor` | `422` | The request is well-formed but cannot be fulfilled as-is |
| `VendorReservationFailed` | `503` | The vendor side failed — retrying is worthwhile |

---

## Design note

### The baseline did not compile

`AssignmentEngine` called `idempotencyStore.CompleteAsync(...)`, which does not exist on
`IIdempotencyStore`. This was treated as a design question rather than a typo: the interface exposes
`SaveCompletedAsync` **and** `ReleaseInFlightAsync` precisely because completing an assignment and
releasing an in-flight lease are two different outcomes, and collapsing them into one call is what
made the baseline unsound.

### Success is durable, failure is retryable

- **Success** → the result is persisted via `SaveCompletedAsync`, then the event is published.
- **Failure** (no vendor, reservation rejected, exception) → nothing is persisted, so the job stays
  legitimately retryable.
- **Always** → the in-flight lease is released in a `finally`, so an exception inside the critical
  section cannot leave the key held for the lifetime of the process.

Persisting **before** publishing is deliberate: if the publisher throws, the assignment is not lost.
The residual gap — persisted but never published — is what the outbox proposal below addresses.

### Replay is the same assignment, not a new answer

A repeated `jobId` whose assignment already completed returns the **stored result verbatim**: same
vendor, same outcome, same idempotency key. The only difference is the `isReplay` marker (and the
`Idempotent-Replay` response header), which tells the caller which of the two happened without
changing how the assignment itself is read. A duplicate never reaches the vendor and never publishes
a second event — both are asserted in the test suite by call counters, not by inspection.

The alternative — answering duplicates with an error — was rejected: the most common cause of a
duplicate is a client that never received the first response, and returning an error to that client
turns a successful assignment into an apparent failure.

### Concurrency

`TryStartAsync` is backed by an atomic compare-and-set (`ConcurrentDictionary.TryAdd`). Two requests
carrying the same `jobId` microseconds apart cannot both enter the critical section, so a duplicate
can never produce a second vendor reservation or a second event.

There is a narrow window between "check whether it is already completed" and "claim the key": the
winner can finish in between, which would make a legitimate replay look like a conflict. The engine
therefore **re-checks for a stored result after losing the race** (`ResolveLostRaceAsync`) and only
reports `AlreadyInProgress` when there is genuinely still work in flight.

An in-flight duplicate is rejected rather than parked waiting for the winner. It is the simpler and
more predictable contract: no request is held open on a lock it does not own, and the client already
needs a retry path for transient vendor failures, so that path is reused instead of introducing a
second waiting mechanism.

### Outcomes are a closed set, not free text

`AssignmentOutcome` is an enum and the human-readable `Reason` is derived from it, so the text shown
to callers cannot drift from the category used for counting. This is what makes the metrics in step 6
("failure reasons") and the incident playbook possible: alerts key off `NoMatchingVendor` or
`VendorReservationFailed`, never off a parsed string.

### Tradeoffs and known limitations

These are inherent to an in-memory implementation and are deliberately **not** solved in code:

| Limitation | Consequence | Production answer |
|------------|-------------|-------------------|
| Orphaned in-flight key if the process dies mid-assignment | That `jobId` stays blocked | TTL / lease expiry on the in-flight key (Redis `SET NX PX`) |
| Vendor call times out but the reservation actually landed | Capacity consumed without a recorded assignment | Send `jobId` as the vendor-side idempotency key so the vendor deduplicates |
| Process restart clears the store | All replay history lost | Durable store (Redis or the service database) |
| Persisted but not published | Event silently missing | Outbox (below) |
| Single-process guarantees | Two instances would each hold their own store | The same durable store turns the in-memory CAS into a distributed one |

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

## Tests

| Test | What it protects |
|------|------------------|
| `WithCapableVendor_AssignsAndPublishesOnce` | Happy path, and that success is persisted and the lease released |
| `WithNoCapableVendor_FailsWithoutCallingVendorOrPublishing` | No-vendor path leaves no trace and does not poison the job |
| `RepeatedAfterCompletion_ReplaysStoredResultWithoutReassigning` | Replay returns the same assignment; vendor and publisher are not called again |
| `WhileFirstRequestIsStillInFlight_ReportsAlreadyInProgress` | In-flight duplicate is rejected while the winner is still working |
| `ConcurrentRequestsForSameJob_ProduceExactlyOneAssignment` | 32 simultaneous requests produce exactly one reservation and one event |
| `AfterAFailedAttempt_CanBeRetriedSuccessfully` | A failure does not consume the idempotency key |

The in-flight test uses a vendor gateway that parks inside the call until the test releases it, so the
overlap is deterministic rather than timing-dependent.

---

## Metrics

To be completed in step 6.

## Incident playbook

To be completed in step 8.
