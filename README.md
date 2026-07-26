# Vendor Scheduler Starter Repo

This is the take-home starter base for the Senior Backend challenge.

## Stack

- .NET 10
- ASP.NET Core Web API
- xUnit

## Project layout

- `src/VendorScheduler.Api`: HTTP entrypoint
- `src/VendorScheduler.Core`: domain and assignment engine
- `src/VendorScheduler.Infrastructure`: baseline in-memory infrastructure adapters
- `src/VendorScheduler.Tests`: unit tests for assignment logic

## Candidate tasks

Implement and improve:

0. Treat the current `AssignmentEngine` as a baseline: refactor and harden it rather than rewriting everything from scratch.
1. Assignment decision quality and tie-break logic.
2. Idempotent assignment behavior with deterministic replay for duplicate requests.
3. Resilient vendor call behavior (per-attempt timeout, bounded retries, terminal failure behavior).
4. Assignment event publication guarantees with duplicate-publish protection.
5. Tests for happy path, no-vendor path, duplicate replay, retry behavior, and one concurrency edge case.
6. Short design note on tradeoffs and deferred improvements.

## Run locally

```bash
dotnet restore
dotnet build
dotnet test
```

## API

`POST /api/assignments`

Request body:

```json
{
  "jobId": "00000000-0000-0000-0000-000000000000",
  "sourceLanguage": "en",
  "targetLanguage": "de",
  "priority": "normal",
  "dueAtUtc": "2026-07-01T12:00:00Z"
}
```

Response body:

```json
{
  "success": true,
  "jobId": "...",
  "vendorId": "...",
  "reason": "Assigned"
}
```
