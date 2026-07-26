# Take-Home Tech Challenge (Senior Backend)

## Candidate brief

Build a focused extension for a translation platform service.

You are given a starter backend service that receives translation jobs and must assign each job to a vendor. Extend and harden the provided baseline implementation (especially `AssignmentEngine`) and integrate your changes with the existing flow.

## Constraints

- Timebox: 4-6 hours
- Deadline: 7 days
- AI tools are allowed
- Prioritize clean design decisions and reliability over feature volume

## Difficulty level

You are expected to make clear tradeoff decisions under realistic production constraints.

## Scenario

Language jobs arrive with language pair, priority, and due date. Vendors have capabilities, capacity limits, and quality score. Your service must assign jobs reliably and safely under retry conditions.

Additional constraints:

- Assignment requests may be duplicated or arrive concurrently for the same `jobId`.
- Vendor API can return transient failures, timeouts, and partial responses.
- Assignment events must be publish-safe (no double publish for same logical assignment).
- Reviewers will probe your decisions around operability, not just code correctness.

## Required work

1. Refactor and extend the baseline `AssignmentEngine` implementation.
2. Add idempotent assignment handling with deterministic replay semantics for repeated requests.
3. Add resilient external vendor call logic with:
	- per-attempt timeout
	- bounded exponential backoff
	- transient failure retry policy
	- terminal failure behavior
4. Emit assignment event payload after successful assignment with duplicate-publish protection.
5. Add minimal observability hooks:
	- structured logs for assignment decision and retry attempts
	- counters/metrics list for success rate, retries, and failure reasons
6. Provide tests for:
	- critical happy path
	- no vendor path
	- duplicate request replay behavior
	- retry behavior (transient errors)
	- concurrency edge case for same `jobId`

## What we evaluate

- Decision quality and tradeoffs
- Reliability and idempotency thinking
- Code clarity and maintainability
- Test quality and realism
- Documentation and operational awareness
- Prioritization and scope management under hard constraints

## Deliverables

- Source code changes
- Test suite updates
- Short design note (why these choices, what was intentionally deferred)
- Run instructions
- One-page incident playbook for assignment failures (diagnose and mitigate)

## Optional bonus

- Structured logs with correlation id
- Basic metrics list for production monitoring
- Outbox-style event publication strategy proposal (design-only is acceptable)

## Reviewer notes

This challenge is intentionally realistic and scoped. We value practical engineering judgment and ownership thinking.

Strong submissions explicitly explain what was not implemented and why.
