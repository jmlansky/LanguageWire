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
| 3 | Vendor call resilience (timeout, bounded backoff, transient vs terminal) | ✅ done |
| 4 | Assignment decision: priority, due date, tie-breaks | ⬜ pending |
| 5 | API layer: request validation | 🟡 endpoints and status codes done, input validation pending |
| 6 | Observability: structured logs, correlation id, metrics | ⬜ pending |
| 7 | Test suite (happy path, no vendor, replay, retry, concurrency) | ✅ all five covered |
| 8 | Design note + incident playbook | 🟡 design note in progress, playbook pending |

Fault simulation in `FakeVendorGateway` is deliberately deferred: resilience is proven by the unit
tests, and teaching the fake to fail is what will make it observable from Swagger.

Current suite: **18 tests, all passing**.

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
| `NoMatchingVendor` | `422` | No vendor handles this language pair — retrying will not help |
| `NoCapacityAvailable` | `503` | Every capable vendor declined — a capacity problem, retry later |
| `VendorUnavailable` | `503` | A vendor could not be reached after its retries — a technical problem |

The last two share a status code because the caller does the same thing either way, but they are
separate outcomes on purpose: one means capacity has to be bought, the other means someone should be
paged.

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

### Resilience lives above the gateway, not inside it

The starter kept retries, backoff and timeout inside `FakeVendorGateway` — a throwaway class. That
code would have been lost the day the fake is replaced by a real vendor client, and because the fake
never fails it never executed, so nothing proved it worked.

It now sits in `ResilientVendorGateway`, a decorator over `IVendorGateway`:

```
AssignmentEngine  ->  ResilientVendorGateway  ->  IVendorGateway (fake today, real client tomorrow)
                      per-attempt timeout,        one call, one answer,
                      bounded backoff, retries    no policy of its own
```

Each layer has one job: the gateway talks to the vendor, the decorator decides whether to insist, the
engine decides who to assign to. The engine resolves `IVendorGateway` and never learns that retries
exist, so the policy can be removed or replaced without touching it.

### A boolean cannot say "I don't know"

`ReserveCapacityAsync` used to return `bool`, which collapsed three different situations into one
`false`. `VendorReservation` replaces it:

| Status | Meaning | Retried? |
|--------|---------|----------|
| `Reserved` | Capacity is held | — |
| `Rejected` | The vendor answered and declined | **No** — its answer will not change |
| `TransientFailure` | The call failed before the vendor could act | Yes |
| `Uncertain` | Timed out, or an answer we could not interpret | Yes, and counted separately |

`Uncertain` exists because of the partial responses the brief warns about. It is retried like a
transient failure but tracked apart, because it is the dangerous one: the vendor may already have
reserved, so a retry risks double-booking. The real fix is to send the `jobId` as the vendor-side
idempotency key and let the vendor deduplicate — the same idea used at our own API boundary, one
level down. That depends on the vendor supporting it, so it is documented rather than implemented.

Rejections also carry a reason (`NoCapacity`, `PairNotSupported`, `QuotaExceeded`, `Unknown`). They
drive different responses: `NoCapacity` may clear on its own, `QuotaExceeded` is a commercial
conversation, and `PairNotSupported` should be impossible — we filter by language pair before
calling, so seeing it means our view of the vendor's catalogue is stale. That is worth knowing.

### The best vendor that accepts, not merely the best one

Candidates are ranked (least loaded, then cheapest, then best quality) and the engine now walks that
ranking instead of giving up on the first one. A vendor that declines is skipped immediately without
retries; one that could not be reached has already exhausted its retry policy by the time the engine
sees it.

If the list runs out, the outcome distinguishes **why**: `NoCapacityAvailable` when everyone simply
declined, `VendorUnavailable` when at least one vendor never answered. Under a boolean both were the
same failure, and the difference is exactly what tells an operator whether to buy capacity or wake
someone up.

### Backoff and testability

Waiting and randomness are injected into `ResilientVendorGateway` rather than called directly. In
production the wait is `Task.Delay`; in tests it is a function that records what was requested and
returns immediately. That makes the schedule assertable — the suite checks that delays grow
`100ms, 200ms, 300ms, 300ms` and stop at the ceiling — and keeps the whole suite under a second.

Jitter is a fraction of the delay (20% by default) rather than the starter's fixed 0–25ms, which
became negligible once the backoff grew and stopped spreading retries apart.

Timeouts are classified as `Uncertain`, never as clean failures. Caller cancellation is rethrown
instead of being swallowed: a client that walked away is not a vendor problem and must not be retried.

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

18 tests, no `sleep` anywhere except a single 50ms timeout probe.

**Assignment engine**

| Test | What it protects |
|------|------------------|
| `WithCapableVendor_AssignsAndPublishesOnce` | Happy path, and that success is persisted and the lease released |
| `WithNoCapableVendor_FailsWithoutCallingVendorOrPublishing` | No-vendor path leaves no trace and does not poison the job |
| `WhenBestVendorRejects_FallsBackToTheNextRankedVendor` | Failover follows the preference order |
| `WhenEveryVendorRejects_ReportsNoCapacityRatherThanAnOutage` | A capacity problem is not reported as an outage |
| `WhenAVendorCannotBeReached_ReportsVendorUnavailable` | An unreachable vendor outweighs a mere rejection, and stays retryable |
| `RepeatedAfterCompletion_ReplaysStoredResultWithoutReassigning` | Replay returns the same assignment; vendor and publisher are not called again |
| `WhileFirstRequestIsStillInFlight_ReportsAlreadyInProgress` | In-flight duplicate is rejected while the winner is still working |
| `ConcurrentRequestsForSameJob_ProduceExactlyOneAssignment` | 32 simultaneous requests produce exactly one reservation and one event |
| `AfterAFailedAttempt_CanBeRetriedSuccessfully` | A failure does not consume the idempotency key |

**Resilience layer**

| Test | What it protects |
|------|------------------|
| `WhenVendorReservesImmediately_DoesNotRetryOrWait` | The happy path costs nothing extra |
| `WhenVendorRejects_DoesNotRetry` | A definitive answer is never retried |
| `WhenTransientFailureThenSuccess_RetriesAndSucceeds` | Retry actually recovers |
| `WhenAlwaysFailing_StopsAtMaxAttemptsAndReportsTerminalFailure` | Retries are bounded, with no wait after the last attempt |
| `BacksOffExponentially_WithoutExceedingTheCeiling` | The exact schedule, and that it stops growing |
| `AddsJitterWithinTheConfiguredFraction` | Jitter stays proportional and bounded |
| `WhenAnAttemptTimesOut_ReportsUncertainRatherThanFailure` | A timeout is ambiguity, not a clean failure |
| `WhenGatewayThrows_TreatsItAsTransientAndRetries` | A throwing client is classified, not leaked |
| `WhenCallerCancels_PropagatesInsteadOfSwallowing` | Caller cancellation is not mistaken for a vendor fault |

Two details make the suite deterministic: the in-flight test uses a gateway that parks inside the call
until released, so the overlap does not depend on timing; and the resilience tests inject the wait, so
the backoff schedule is asserted rather than endured.

---

## Metrics

To be completed in step 6.

## Incident playbook

To be completed in step 8.
