# Episodes 22–28 Production Reliability Mini-Season

- Generated: 2026-09-05
- Repository: CSharpFeatureLab
- Branch: `main`
- Mode: Builder
- Status: APPROVED
- Decision authority: Ivan delegated the selection on 2026-09-05 and explicitly approved the completed arc on 2026-09-06.

## Problem statement

Episode 21 deliberately ends with a protected transactional outbox that is safe for one process but still provides at-least-once delivery. A provider can accept an invitation immediately before the app crashes, causing the same delivery to be attempted again. The series now needs to teach the reliability layers that close that gap without hiding several independent ideas inside one oversized lesson.

## What makes this mini-season compelling

The learner will watch a delivery survive the kinds of failures that normally make background work feel mysterious: provider outage, process restart, two competing workers, a crash after provider acceptance, retry exhaustion, and human redrive. The final proof is visible and falsifiable: two workers may make multiple attempts, but the provider records one logical effect, secrets never appear in telemetry or UI, and exhausted work can be recovered deliberately.

## Constraints and premises

- Keep the established 6–10 minute, one-primary-concept episode rhythm.
- Preserve Episode 21's database outbox and protected payload as the source of truth.
- Continue the existing invitation-delivery story so every reliability layer has a concrete failure to solve.
- Treat delivery as at-least-once attempts plus an idempotent provider effect, not as universal exactly-once delivery.
- In the invitation-delivery path, never place invitation codes, recipients, protected payloads, raw exceptions, tenant IDs, or user IDs in logs, metrics tags, traces, Hangfire arguments, or operator projections. Unrelated existing jobs retain their established opaque-ID contracts.
- Make time, provider behavior, and worker races deterministic enough for automated tests.
- Keep each episode independently demonstrable and leave the code in a valid state.
- Do not add curriculum or implementation work until Episode 22 receives an engineering plan.

## Approaches considered

### A. Granular reliability ladder — selected

Give idempotency, durable retry timing, multi-worker coordination, terminal handling, observability, scheduling, and UI projection one episode each.

Why it wins: the dependency order is honest, every lesson has one memorable model, tests can prove one new guarantee at a time, and the capstone combines concepts the learner already understands.

### B. Compress reliability into Episode 22, then return to membership administration

This would reach member listing, removal, ownership transfer, and audit history sooner. It was rejected because a single lesson would have to introduce stable delivery keys, backoff, leases, stale-worker fencing, and metrics at once. The learner would see machinery without learning which failure each piece prevents.

### C. Build the operations dashboard first, then backfill reliability

This creates an earlier visual payoff. It was rejected because the UI would imply delivery states and guarantees that the domain model cannot yet support truthfully.

## Season through-line

> Attempts may repeat. The logical effect must not, retry state must survive, workers must coordinate, failure must become visible, and recovery must remain safe.

The dependency chain is:

`stable effect identity → durable retry state → worker lease → terminal state → safe signals → durable wake-up → safe operations UI`

Episode 21 used “Episode 22” as an umbrella for the next reliability step. This mini-season decomposes that promise: Episode 22 closes the duplicate-effect gap first, and Episodes 23–28 complete the remaining retry, coordination, failure-handling, observability, scheduling, and operations deferrals.

## Shared delivery-state contract

The episode code may introduce these fields incrementally, but the end-state transitions are fixed before implementation:

| Action | From → to | Required predicate | Atomic field changes |
| --- | --- | --- | --- |
| Claim due work | `Pending → Claimed` | Due, below retry budget, unclaimed, and either last outcome is known-no-effect/null or `databaseNow < DedupeGuaranteedUntil` | Set a fresh opaque `ClaimToken`, bounded `ClaimedUntil`, and `DispatchStartedAt = null`; do not increment `AttemptCount` |
| Reclaim abandoned work | `Claimed(expired) → Claimed(new token)` | Lease expired, below retry budget, and either `DispatchStartedAt` is null or `LastOutcome = Unknown` while `databaseNow < DedupeGuaranteedUntil` | Replace the token and lease atomically; reset `DispatchStartedAt = null`; preserve attempt history |
| Begin provider call | `Claimed → Claimed` | Current, unexpired claim token and null `DispatchStartedAt` | Increment `AttemptCount` exactly once; set `DispatchStartedAt = databaseNow` and `LastOutcome = Unknown` immediately before provider I/O; on the first attempt set `FirstAttemptAt` and conservative `DedupeGuaranteedUntil = FirstAttemptAt + provider minimum retention` |
| Release before dispatch | `Claimed → Pending` | Current token and cooperative cancellation before begin-attempt | Preserve `AttemptCount`; set `NextAttemptAt` to now; clear claim fields |
| Acknowledge accepted or deduplicated effect | `Claimed → Succeeded/delete` | Current claim token; exactly one affected row | Delete the protected outbox row; the active operations view removes it and emits a success signal |
| Schedule known transient retry | `Claimed → Pending` | Current claim token and `AttemptCount < MaxAttempts` | Set `LastOutcome = KnownNoEffect`, `NextAttemptAt`, allow-listed `FailureCode`, and clear claim fields |
| Schedule unknown retry | `Claimed → Pending` | Current claim token, `AttemptCount < MaxAttempts`, and computed `NextAttemptAt < DedupeGuaranteedUntil` | Preserve `LastOutcome = Unknown`, set due time and sanitized code, and clear claim fields |
| Stop automatic retry | `Claimed → DeadLettered` | Current claim token and permanent rejection, or known-no-effect failure with `AttemptCount >= MaxAttempts` | Set `LastOutcome = KnownNoEffect`, `DeadLetteredAt`, sanitized reason, and clear claim fields |
| Require reconciliation | `Pending` or `Claimed(expired) → ReconciliationRequired` | `LastOutcome = Unknown` and either retry budget or `DedupeGuaranteedUntil` is exhausted | Clear claim fields and block provider I/O because the provider may already have accepted the effect |
| Discard poison or obsolete work | `Pending` or current `Claimed → Unrecoverable` | Invitation cancelled/expired, envelope invalid/corrupt, or invariant mismatch | Clear claim fields, irreversibly erase the protected payload, and preserve only an allow-listed reason; before Episode 25 retain Episode 21's delete-and-close behavior |
| Redrive eligible work | `DeadLettered → Pending` | Authorized actor, expected delivery version, valid invitation and envelope, and provider-deduplication safety established | Preserve `InvitationId`; reset `AttemptCount` to 0; set `NextAttemptAt` to now; clear failure and claim fields; increment `RedriveCount` and delivery version; append an immutable audit event |
| Reject unrecoverable work | `DeadLettered → Unrecoverable` | Invitation/envelope expired or cannot be decrypted | Record an allow-listed reason; require a newly issued invitation |
| Require reconciliation before redrive | `DeadLettered → ReconciliationRequired` | Provider outcome is ambiguous and its idempotency-retention window has elapsed | Block ordinary redrive; never offer a blind UI override |
| Reconcile accepted effect | `ReconciliationRequired → Succeeded/delete` | Provider evidence confirms acceptance; expected delivery version | Delete the protected row and append an immutable, sanitized reconciliation audit event |
| Reconcile absent effect | `ReconciliationRequired → Pending` | Provider evidence confirms no effect; authorized actor and expected delivery version | Preserve `InvitationId`; reset retry state; increment version; append immutable reconciliation evidence; schedule now |
| Keep unresolved | `ReconciliationRequired → ReconciliationRequired` | Provider cannot establish accepted or absent | Make no delivery mutation and keep all automatic/UI redrive blocked |

Episode 25 activates `MaxAttempts = 5`, meaning at most five provider calls: a claim is allowed while `AttemptCount < 5`, the count is incremented before I/O, and a failure on attempt five dead-letters the item. Before Episode 25, the educational implementation has no terminal budget. Cancellation before the begin-attempt mutation consumes no attempt; cancellation or process loss after it does, because the provider outcome may be unknown. Every acknowledge, retry, dead-letter, and release operation is conditional on the current token and must affect exactly one row. No database transaction remains open across provider I/O.

The provider adapter normalizes outcomes to `Accepted`, `DuplicateAccepted`, `TransientFailure(code)`, `PermanentFailure(code)`, or `Unknown(code)`; raw exceptions never become state. The row persists `LastOutcome = KnownNoEffect|Unknown` so a later worker can distinguish a confirmed rejection from an ambiguous handoff. Accepted outcomes acknowledge. Before Episode 25, non-success remains pending only while replay is inside the provider guarantee; Episode 25 makes permanent failures terminal immediately, applies the attempt budget to known-no-effect failures, and routes exhausted or out-of-window unknown outcomes to reconciliation.

Episode 22 reuses the existing immutable `InvitationId`—already the outbox primary key and one-to-one delivery identity—as the provider idempotency key, so it needs no schema migration. If a future feature permits multiple independent delivery effects for one invitation, that future design must add a separate operation ID rather than prebuilding one here. Later migrations introduce only their episode's state: Episode 23 defaults existing rows to `Pending`, `AttemptCount = 0`, `NextAttemptAt = CreatedAt`, and null failure/first-attempt/dedupe-window values; Episode 24 adds null claim fields; Episode 25 adds null terminal fields, delivery version, and `RedriveCount = 0`. Deploy notes must state that attempts made before Episode 22 were not sent to an idempotency-aware provider and therefore cannot be deduplicated retroactively.

## Episode contracts

### Episode 22 — Make Retried Delivery Idempotent at the Provider Boundary

**Playlist:** Production .NET  
**One-sentence outcome:** Reuse the invitation's stable, non-secret `InvitationId` as the provider idempotency key on every attempt so a crash-window replay creates one provider-visible effect.

**Hook:** Episode 21 can crash after the provider accepts an invitation but before the outbox row is removed. On restart, the same protected message is sent again.

**Build:**

- Use the existing immutable `InvitationId`, already the outbox primary key, because the current model has exactly one logical delivery effect per invitation. A new invitation gets a new key; retries and redrives retain the same key.
- Do not add a speculative `DeliveryId`. Add one only when a later requirement introduces multiple independent effects for the same invitation.
- Pass that ID through `ITenantInvitationDelivery` to an idempotency-aware provider adapter.
- Define the provider contract as `InvitationId + immutable request → original result`: identical concurrent replays converge on one atomically stored result, while the same key with different request content returns an idempotency-conflict error.
- Extend the fake provider to bind accepted invitation IDs to a private canonical-request fingerprint and original result. The fingerprint is never persisted, logged, or exported.
- Keep fake-provider acceptance state outside the worker/app lifetime so a recreated host can replay against the same provider truth.
- Keep the delivery key distinct from invitation code, recipient, tenant, or payload data.

**Mental model:** At-least-once attempts plus an idempotent effect.

**Gotcha:** A database `Sent` flag written after the external call cannot close the crash window. Provider idempotency is also bounded by the provider's contract and retention window, so this is not an unlimited exactly-once guarantee.

**Proof:**

- Simulate provider acceptance followed by database acknowledgement failure.
- Retry with the same `InvitationId`; observe two attempts and one logical provider delivery.
- Race identical calls with the same key and prove they converge on the same result.
- Reuse the same key with different content and prove the provider rejects the mismatch.
- Prove a different invitation ID is not deduplicated.
- Prove logs and test diagnostics contain neither invitation code nor recipient.

**Out of scope:** backoff, leases, dead letters, Hangfire, metrics, and UI.

**Closing line:** Replays are safe now, but a failing provider can still create a hot retry loop.

### Episode 23 — Persist Retry Backoff Instead of Sleeping in Memory

**Playlist:** Production .NET  
**One-sentence outcome:** Persist retry progress and due time so a transient failure backs off predictably across process restarts.

**Hook:** `Task.Delay` forgets everything when the process stops and ties up the worker that should be doing other work.

**Build:**

- Add `AttemptCount`, `NextAttemptAt`, and an allow-listed `FailureCode` to delivery state.
- Calculate exponential backoff with a maximum delay and deterministic jitter from the logical delivery ID plus attempt number.
- Query only due work and inject `TimeProvider` so tests control the clock.
- Persist the next attempt atomically with the failed attempt outcome.

**Backoff contract:** Encode the input as lower-case UTF-8 `InvitationId:D + ":" + AttemptCount`. Take the first unsigned 16-bit big-endian value from SHA-256 and divide by 65,535 to get `u`. Compute `base = min(30 seconds × 2^(AttemptCount-1), 30 minutes)`, then `delay = min(base × (0.8 + 0.4u), 30 minutes)`. For `00000000-0000-0000-0000-000000000001`, attempts 1–3 produce 27.880 s, 53.390 s, and 131.690 s. These values are fixed test vectors.

**Mental model:** Retry is durable state, not a sleeping thread.

**Gotcha:** Identical schedules cause a thundering herd after an outage. Raw exception text is not durable domain state and may contain secrets.

**Proof:**

- Not-due work is ignored; due work is attempted.
- Restart the worker and show the persisted due time is honored.
- Demonstrate increasing, jittered, capped delays with a controlled clock.
- Verify only an allow-listed failure code is stored.

**Out of scope:** multiple workers and terminal retry exhaustion.

**Closing line:** Durable time solves restarts; it does not stop two workers from selecting the same row.

### Episode 24 — Let Multiple Workers Compete Without Double-Sending

**Playlist:** Production .NET  
**One-sentence outcome:** Atomically claim due deliveries with a bounded lease and fence stale workers from changing work they no longer own.

**Hook:** Start two worker instances against the same database; both see the same due delivery before either finishes.

**Build:**

- Add an opaque `ClaimToken` and `ClaimedUntil` lease.
- Support SQL Server with one conditional compare-and-swap `UPDATE … OUTPUT` operation rather than a read-then-write race; an affected-row count of one wins the claim and zero loses it.
- Set the lease TTL above the bounded provider-call timeout plus acknowledgement margin; the initial contract is a 15-second provider timeout and a 45-second lease, with no renewal protocol in this lesson.
- Use SQL Server `SYSUTCDATETIME()` for due/expiry comparisons and `ClaimedUntil`; the application never compares leases with a worker-local clock.
- Require the current claim token for acknowledge, retry scheduling, and terminal transitions.
- Allow an expired lease to be reclaimed after a worker disappears.

**Mental model:** Claim, work, acknowledge — with ownership checked at every mutation.

**Gotcha:** A provider can still return after its timeout and lease expiry. Token fencing protects database mutations; Episode 22 idempotency protects late external calls. A process-local lock or `[DisableConcurrentExecution]` is not the data guarantee.

**Proof:**

- Race two workers and show exactly one successful claim.
- Reject acknowledge and reschedule operations from a stale token.
- Advance time, reclaim an expired lease, and complete the delivery.
- Repeat the provider crash window and retain one logical effect.
- Assert that no transaction or connection lock is held while the gated provider is paused.

**Out of scope:** retry budgets, operator action, and telemetry.

**Closing line:** Work can now be recovered safely, but automatic retry still needs a stopping rule.

### Episode 25 — Dead-Letter Exhausted Deliveries and Redrive Them Deliberately

**Playlist:** Production .NET  
**One-sentence outcome:** Stop automatic retry at a defined budget, preserve a sanitized terminal record, and make eligible redrive an explicit, audited decision.

**Hook:** An infinite retry loop converts a permanent fault into invisible cost and noise.

**Build:**

- Define a retry budget and transition exhausted work to `DeadLetteredAt` with an allow-listed reason.
- Preserve protected delivery material and history rather than deleting the failed record.
- Record redrive actor, time, count, and reason without recording recipient or payload.
- Perform redrive as a compare-and-swap on the expected dead-letter version; a double-click or competing operator gets a stale conflict instead of a second reset.
- Append one immutable database-only audit event with actor ID, time, allow-listed reason, prior version/state, and reconciliation evidence; do not overwrite prior redrive history.
- Before redrive, verify the invitation and protected envelope remain valid and decryptable; otherwise move to `Unrecoverable` and require a new invitation.
- Reuse the same `InvitationId` for the same logical effect and reset its retry schedule only while the provider guarantee remains valid or reconciliation confirms that no effect occurred.
- Move an ambiguous item beyond the provider's idempotency-retention window to `ReconciliationRequired`; ordinary redrive stays blocked and the beginner UI has no risk override.
- Resolve reconciliation in exactly two versioned, audited ways: provider evidence of acceptance deletes the row as succeeded; evidence of no effect returns the same `InvitationId` to pending. Inconclusive evidence leaves the item blocked.

**Mental model:** Automatic retry has a budget; redrive is a new human decision.

**Gotcha:** “Same key” is not proof of safety after the provider's idempotency-retention window. A truthful workflow blocks and reconciles instead of silently accepting duplicate-effect risk.

**Proof:**

- Exhaustion stops further automatic attempts.
- The dead-letter record survives and contains no secret-bearing text.
- An unauthorized actor cannot redrive.
- An authorized redrive is audited, preserves `InvitationId`, and returns the item to scheduled work.
- Concurrent redrive commands produce one transition and one immutable audit event; the loser receives a stale conflict.
- Expired delivery material becomes unrecoverable, and an ambiguous out-of-window result requires reconciliation.
- Reconciliation-confirmed acceptance completes without another provider call; confirmed absence schedules one, while an inconclusive result remains blocked.

**Out of scope:** dashboard layout and alerting.

**Closing line:** The state is truthful now; operators still need safe signals to find it.

### Episode 26 — Measure Queue Health Without Logging Secrets

**Playlist:** Production .NET  
**One-sentence outcome:** Apply one telemetry allow-list to low-cardinality metrics, one representative trace span, and existing structured events without copying sensitive delivery data into telemetry.

**Hook:** A queue can be correct and still be impossible to operate if no one can see that it is aging or failing.

**Build:**

- Increment an untagged `invitation.delivery.attempts` (`Counter<long>`, unit `{attempt}`) immediately after the durable begin-attempt mutation.
- Add `invitation.delivery.outcomes` (`Counter<long>`, unit `{outcome}`) with finite `outcome = accepted|duplicate_accepted|retry_scheduled|dead_lettered|claim_lost|reconciliation_required` after a normalized result.
- Add `invitation.delivery.attempt.duration` (`Histogram<double>`, milliseconds) with only `outcome`, and `invitation.delivery.oldest_due.age` (`ObservableGauge<double>`, seconds) with no tags, defined as `max(0, now - earliest due NextAttemptAt)` or zero when nothing is due. Each bounded sweep refreshes a cached numeric observation from its due-work projection; the synchronous gauge callback never queries the database.
- Add one `invitation.delivery.attempt` `ActivitySource` span and reuse stable structured event IDs for attempt started/completed, retry scheduled, and dead-lettered.
- Allow only finite `failure.code` and `attempt.bucket = 1|2-3|4-5` values in logs/spans. The opaque `InvitationId` may appear only in access-controlled logs/spans and the operator reference, never as a metric dimension.
- Register the meter and activity source through vendor-neutral OpenTelemetry with an environment-selected OTLP or console exporter; exporters are configuration, not domain logic.
- Never export recipient, invitation code, protected payload, request fingerprint, raw exception, tenant ID, or user ID.

**Mental model:** Logs explain an event, metrics explain system behavior, and traces connect steps.

**Gotcha:** High-cardinality identifiers and PII-bearing tags make telemetry expensive, hard to aggregate, and dangerous to retain.

Metrics are operational observations, not a transactional ledger: process death can occur between a committed state change and metric emission. Database state and immutable audit events remain authoritative.

**Proof:**

- Use `MeterListener` and captured log assertions to verify instruments and safe tags.
- Use `ActivityListener` to assert the single span contract and positive correlation.
- Assert span tags, events, baggage, status, and exception fields against the same allow-list; the test listener samples all activity so `StartActivity` cannot silently return null.
- Show queue-age growth during a provider outage and success after recovery.
- Search captured telemetry for seeded recipient and invitation-code values and find none.

**Out of scope:** additional spans, vendor-specific observability products, dashboards, and alert-policy tuning. The on-camera slice teaches the allow-list and one signal per pillar; shared capture plumbing lives in the episode test fixture.

**Closing line:** We can see due work safely; the process still needs a durable way to wake up and scan for it.

### Episode 27 — Use Hangfire to Wake the Outbox, Not Replace It

**Playlist:** Production .NET  
**One-sentence outcome:** Use durable Hangfire jobs to trigger bounded due-work sweeps while the application database remains authoritative for delivery state and retry timing.

**Hook:** Putting the protected payload or retry policy into two systems creates two sources of truth and two places for secrets to leak.

**Build:**

- Reuse and verify the existing production Hangfire SQL storage/server registration and its fail-closed configuration.
- Register one stable recurring job ID, `tenant-invitation-outbox-sweep`, on a one-minute UTC cadence; keep its arguments empty.
- Put `[AutomaticRetry(Attempts = 0)]` on the sweep so Hangfire does not create a second retry clock.
- Bound each run to at most 25 claims and a 30-second runtime; the next recurring tick drains remaining work.
- Extract the current `ProcessBatchAsync` work into a scheduler-neutral sweep service and remove the one-second hosted-loop registration when Hangfire becomes authoritative.
- Add an index ordered by terminal state, `NextAttemptAt`, lease availability, and `InvitationId`; for a due backlog of at most 25, target dispatch start within 75 seconds and surface breaches through oldest-due age.
- Let the database query decide what is due and let the delivery state own retry timing.
- Make repeated or overlapping wake-ups harmless through claims and idempotency.

**Mental model:** The scheduler wakes work; the domain database defines truth.

**Gotcha:** If Hangfire and the outbox both own retry policy, their clocks conflict. If job arguments contain protected content, Hangfire becomes a second secret store.

**Proof:**

- Inspect persisted job arguments and find no recipient, code, or protected payload.
- Trigger two wake-ups and observe one claimed attempt and one logical effect.
- Restart the app and show the durable wake-up resumes due work.
- Demonstrate that missing production storage configuration prevents startup.
- Invoke the sweep service directly in the low-latency demo; a separate persistent-storage integration test proves restart behavior because recurring Hangfire scheduling is minute-granular.

**Out of scope:** recurring-job dashboard customization and provider-specific SDK templates.

**Closing line:** The mechanics are durable; now expose only the state an authorized operator needs.

### Episode 28 — Build a Blazor Delivery Operations Panel Without Exposing Recipients

**Playlist:** Blazor  
**One-sentence outcome:** Give an authorized tenant owner a sanitized status-and-redrive panel built from an explicit projection rather than the delivery entity.

**Hook:** An admin screen is still a data-exposure boundary; dumping the outbox row into a grid undoes the protections built into the worker.

**Build:**

- Project only opaque delivery reference, status, queued age, attempt count, next retry, and allow-listed failure category.
- Derive tenant scope and authorization on the server; never trust tenant context from the browser.
- Add a redrive action with busy state, stale-state handling, and a server-truth refresh.
- Return a server-computed `CanRedrive`; expired, unrecoverable, and reconciliation-required rows never expose the action.
- Keep recipient, invitation code, protected payload, raw provider response, and raw exception out of markup and serialized component state.

**Mental model:** An operations view is an allow-list projection, not an entity dump.

**Gotcha:** Authorization around the page is insufficient if the query or command can cross tenant boundaries. Every read and action must enforce scope server-side.

**Proof:**

- Two-tenant integration tests prove rows and commands remain isolated.
- Unauthorized and non-owner redrive attempts are denied.
- Direct endpoint tests deny anonymous, non-owner, stale-owner, cross-tenant-owner, and nonexistent-ID callers; component authorization is treated only as UX.
- Rendered markup and component state contain none of the seeded secret values.
- A stale row refreshes to server truth instead of pretending the command succeeded.

**Out of scope:** recipient display, payload inspection, editing provider responses, and bulk redrive.

**Closing line:** Show the capstone's three headline assertions, then return to tenant membership administration.

## Capstone live demo

The full capstone is a deterministic companion test/script, not extra Episode 28 implementation scope. Episode 28 shows only its three headline assertions in the final 60–90 seconds: two workers produce one logical effect, a restarted retry reaches a truthful terminal state, and every observable surface remains secret-free.

The companion scenario must be runnable without timing sleeps. It uses two independently constructed worker service providers sharing the SQL Server database and Data Protection key ring plus one fake-provider acceptance store external to both workers and preserved across their restarts. A barrier controls the claim race; the provider is gated; a failpoint injects the post-acceptance/pre-ack crash; the test invokes the sweep directly and captures `MeterListener`/`ActivityListener`/logs.

SQL Server is the lease clock authority: due/expiry comparisons and `ClaimedUntil` use `SYSUTCDATETIME()`, not worker clocks. A claim-store test double exposes a deterministic clock seam; the SQL Server integration proof backdates seeded lease values instead of sleeping. `TimeProvider` still controls invitation expiry and the Episode 23 pure backoff tests.

Its scripted steps are:

1. Start two delivery-worker instances against one database.
2. An owner queues an invitation while the provider is temporarily unavailable.
3. Show the persisted retry schedule backing off, restart both workers, and show the schedule survives.
4. Recover the provider and race both workers; one lease wins.
5. Simulate provider acceptance followed by an app crash before acknowledgement.
6. Replay the same stable `InvitationId`; the fake provider reports two attempts but one logical effect.
7. Keep a transient failure active for five attempts to exhaust the retry budget and enter dead-letter state with a sanitized reason; separately prove that a permanent rejection becomes terminal immediately.
8. In the authorized Blazor panel, show queue age, attempts, next action, and terminal state without recipient, code, payload, tenant ID, user ID, or raw exception.
9. Redrive an eligible known-safe failure after provider recovery and show the audited transition through to completion; the active row disappears and the success counter advances.
10. Inspect captured logs, metrics, traces, job arguments, markup, and state for seeded secret values; assert both the expected positive signals and that no seeded secret appears.

## Season success criteria

- Each episode adds exactly one primary mental model and one visible failure/proof pair.
- Every guarantee is covered by an automated test at the lowest practical layer plus one integration proof.
- The two-worker capstone is deterministic enough to run locally and in CI without timing sleeps.
- The shared transition table has an automated proof for every allowed edge and rejection proof for stale tokens, exhausted budgets, expired material, and unsafe redrive.
- Multiple attempts can be observed while one logical provider effect is asserted.
- Restart, lease expiry, exhaustion, and redrive are demonstrated with a controllable clock and provider.
- All persisted and emitted operational data follows explicit allow lists.
- Existing Episodes 1–21 remain behaviorally intact.

## Distribution plan

Each episode follows the established short-form structure:

- 30–45 seconds: reproduce one concrete failure from the previous state.
- 60–90 seconds: name the mental model and boundary.
- 3–5 minutes: make the smallest code and schema change that supplies the guarantee.
- 1–2 minutes: run the focused automated proof and visible demo.
- 20–30 seconds: state the limitation and hand off the next failure.

The complete capstone ships as a companion script/test. Episode 28 includes only a 60–90 second summary run so the UI lesson stays within the normal runtime.

Episode titles and descriptions should lead with the learner-visible guarantee, not the framework or package name. The episode package should include the same source checkpoint, focused tests, transcript, captions, and secret-scan evidence used by the existing release process.

## Cross-review perspective

Two independent curriculum reads agreed that Episode 22 must honor Episode 21's explicit promise by solving provider-boundary idempotency. One recommended compressing the remaining reliability work and returning immediately to tenant administration; the other recommended the granular ladder selected here. The granular arc wins because it protects the series' one-concept pacing and makes the final multi-worker proof earned rather than magical.

## Later arc, deliberately not specified yet

After Episode 28, return to the tenant lifecycle with a safe member list, member removal plus stale-token rejection, atomic ownership transfer with an exactly-one-owner invariant, and an append-only workspace audit trail. Those lessons should be designed only after the reliability capstone is implemented and reviewed.

## Decision record

Selected the seven-episode reliability ladder. The visible destination is a two-worker failure-and-recovery demo in which an invitation survives restarts and provider outages, produces one logical provider effect across a crash-window replay, reaches a truthful terminal state when retries are exhausted, exposes no secret data, and allows an authorized owner to redrive only eligible work.

## Assignment

Use this approved arc as the input to `/plan-eng-review` for Episode 22 only. Then add only Episode 22 to the curriculum and implement the idempotency slice before scheduling or producing any new release. Do not pre-build Episodes 23–28; let each episode's completed proof establish the starting state for the next.

## Episode 22 engineering plan

- Reviewed: 2026-09-06
- Review target: Episode 22 only
- Status: CLEARED FOR IMPLEMENTATION
- Scope decision: Ivan's explicit `publish` instruction accepts the complete Episode 22 checkpoint and release workflow. Later reliability layers remain deferred.

### Scope challenge

The existing delivery boundary already carries the immutable `InvitationId`, the dispatcher already forwards it, and the outbox already uses it as the primary key. The smallest complete implementation therefore changes the development fake provider and its focused tests; it does not add a database column, migration, second delivery identifier, receipt type, or new scheduler.

The source checkpoint stays within seven intentional files: the provider implementation, one dispatcher comment, focused tests, the about text, README, ordered curriculum, and this design. The local production contract and release ledger are packaging inputs rather than checkpoint architecture. No new infrastructure or distributable artifact type is introduced.

The fake provider's idempotency guarantee is explicitly **24 hours from first acceptance**, matching the application's maximum invitation lifetime. The one-time raw-code pickup remains available for only five minutes. After pickup or five minutes, the fake retains only the private request fingerprint, first-acceptance metadata, and attempt count until the idempotency window expires. A real adapter must document an equal-or-longer provider guarantee; attempts made before this checkpoint cannot be deduplicated retroactively.

### What already exists

| Existing boundary | Reuse decision |
| --- | --- |
| `ITenantInvitationDelivery.DeliverAsync(Guid invitationId, ...)` | Reuse unchanged; successful task completion remains the command result. |
| `TenantInvitationOutboxMessage.InvitationId` primary key | Reuse as the provider idempotency key; no migration. |
| Dispatcher post-provider delete order | Preserve so delivery remains recoverable; the existing injected delete failure becomes the crash-window proof. |
| `RecordingTenantInvitationDelivery` | Evolve into the idempotency-aware fake provider and its externally shareable acceptance truth. |
| Persisted Data Protection key-ring restart harness | Reuse to prove a recreated worker can replay against the same external fake-provider instance. |
| Sanitized dispatcher logging | Preserve and extend assertions so neither conflicts nor retries reveal recipient or code. |

### Architecture review

```text
TenantInvitationOutbox row (InvitationId K)
                |
                v
dispatcher validates protected immutable request
                |
                v
ITenantInvitationDelivery.DeliverAsync(K, request)
                |
                v
fake provider computes private SHA-256 fingerprint outside lock
                |
                v
      +--------- one short lock ----------+
      | K missing      -> store first result, fingerprint, attempt=1
      | K + same hash  -> increment attempt, return original success
      | K + other hash -> increment attempt, throw sanitized conflict
      +-----------------------------------+
                |
                v
dispatcher deletes outbox row; failed delete leaves K for safe replay
```

The fingerprint encodes the normalized recipient, code, and UTC expiry with unambiguous length-prefixed fields. It is private, never persisted, logged, serialized, or exported, and is zeroed when retention expires or the provider is disposed. Hashing happens outside the lock; the lock contains only bounded in-memory state checks and mutations. The implementation deliberately avoids a side-effecting `ConcurrentDictionary.GetOrAdd` factory because .NET documents that concurrent calls may invoke the factory more than once even though only one value is stored.

No architecture issues remain after setting the retention window and choosing the explicit critical section. The provider command has no meaningful receipt today, so changing the interface return type would add a breaking contract without improving the Episode 22 guarantee.

### Code quality review

Keep the acceptance ledger and one-time secret pickup as separate concerns inside the existing fake. Consuming a code must not erase the provider's idempotency decision. Preserve the existing capacity behavior by limiting only unconsumed secret-bearing deliveries; retained fingerprints do not block later test deliveries. Use a named safe conflict exception whose message contains neither request content nor raw exception data.

No additional service abstraction is justified. A single provider class with a private entry type keeps the teaching diff explicit and avoids pre-building Episode 25 outcome normalization.

### Test review

```text
CODE PATHS                                      INTEGRATION FLOW
[+] first key                                   [+] outbox crash window [-> integration]
    +-- accept and retain original result           +-- provider accepts attempt 1
    +-- capacity reached -> safe failure             +-- database delete fails
[+] retained key                                     +-- recreated dispatcher retries K
    +-- identical sequential -> original success     +-- provider attempts=2, effects=1
    +-- identical concurrent -> one effect            +-- retry deletes outbox row
    +-- changed recipient -> safe conflict
    +-- changed code -> safe conflict              SECURITY ASSERTIONS
    +-- changed expiry -> safe conflict           [+] exception/log/ToString omit code
[+] different key -> independent effect           [+] exception/log/ToString omit recipient
[+] pickup -> secret removed, ledger retained     [+] fingerprint has no public accessor
[+] 5-minute access expiry -> secret removed
[+] 24-hour retention expiry -> replay may create a new effect
```

Focused xUnit coverage must prove:

1. Identical sequential calls produce two attempts and one logical effect, returning the first recorded result.
2. A barrier-released concurrent race produces N successful attempts and one logical effect.
3. Reusing a key with a changed recipient, code, or expiry throws the safe conflict and creates no second effect.
4. A different `InvitationId` is independent.
5. One-time pickup and five-minute cleanup remove raw delivery material without erasing the 24-hour fingerprint ledger.
6. After the documented 24-hour window, replay is no longer promised to deduplicate.
7. The existing injected post-acceptance/delete failure now proves two dispatcher attempts and one provider effect, including across recreated service providers sharing one fake-provider instance.
8. Existing capacity, cancellation, expiry, poison-envelope, batching, scope, startup, API, and UI tests remain green.

### Performance review

The fake is capped at 100 unconsumed secret-bearing results. Each call hashes one small request and performs one bounded dictionary operation under a lock; no database, logging, timer callback, or provider I/O occurs inside the critical section. Cleanup is linear in the retained fake-provider entries and runs every 30 seconds, which is acceptable for development/test infrastructure. Production throughput, distributed storage, and provider SDK retry behavior are intentionally not modeled here.

### Failure modes

| Failure | Test | Handling | User-visible behavior |
| --- | --- | --- | --- |
| App stops after provider acceptance but before outbox delete | Integration | Same key replays to original success | No duplicate provider effect; queued row clears on retry. |
| Concurrent identical requests reach provider | Unit concurrency | One locked acceptance decision | Every caller succeeds; one logical effect. |
| Same key carries changed immutable content | Parameterized unit | Sanitized idempotency-conflict exception | Dispatcher retains durable work; later retry/backoff policy is Episode 23. |
| Raw pickup is consumed before replay | Unit | Fingerprint ledger survives separately | Replay stays safe without re-exposing the code. |
| Provider retention expires | Controlled-clock unit | Entry and fingerprint are erased | Guarantee ends explicitly; this is not universal exactly-once. |
| Capacity is full | Existing integration | New effect rejects safely; duplicates still deduplicate | Outbox stays queued for a later pass. |

No critical failure is both silent and untested.

### NOT in scope

- Durable backoff or failure codes: Episode 23 owns retry timing.
- Multi-worker claims, leases, or stale-token fencing: Episode 24 owns coordination.
- Retry budgets, terminal states, reconciliation, redrive, or normalized provider outcomes: Episode 25 owns terminal handling.
- Metrics, traces, additional logs, Hangfire changes, or operator UI: Episodes 26-28 own those layers.
- A real email-provider SDK: the lesson proves the provider contract with a deterministic fake and states the retention requirement adapters must meet.
- Retrofactive deduplication of attempts before Episode 22: the provider never received stable-key semantics for those historical calls.

### Parallelization

Sequential implementation, no parallelization opportunity. The provider behavior and its integration proof share the same production and test modules; splitting them across worktrees would add merge risk without reducing the critical path. Release-media work starts only after the immutable source checkpoint passes.

## Implementation Tasks

- [ ] **T1 (P1, human: ~2h / Codex: ~20m)** - Provider fake - Make `InvitationId` replays idempotent for 24 hours while keeping secret pickup short-lived.
  - Surfaced by: Architecture review - provider truth must survive pickup, concurrency, and worker restart.
  - Files: `src/FeatureLab.Web/Tenancy/TenantInvitationDelivery.cs`
  - Verify: focused sequential, concurrent, conflict, capacity, pickup, and retention tests.
- [ ] **T2 (P1, human: ~2h / Codex: ~25m)** - Dispatcher proof - Turn the existing post-handoff delete failure into the two-attempt/one-effect integration test and add restart coverage.
  - Surfaced by: Test review - the learner-visible guarantee must cross the real outbox boundary.
  - Files: `tests/FeatureLab.Web.Tests/TenantInvitationDeliveryTests.cs`, `src/FeatureLab.Web/Tenancy/TenantInvitationOutboxDispatcher.cs`
  - Verify: `dotnet test CSharpFeatureLab.slnx --filter FullyQualifiedName~TenantInvitationDeliveryTests`.
- [ ] **T3 (P1, human: ~1h / Codex: ~15m)** - Checkpoint contract - Add Episode 22 only and document the new guarantee and its limit.
  - Surfaced by: Distribution review - the ordered curriculum and public checkpoint must match the executable proof.
  - Files: `content/curriculum.json`, `README.md`, `src/FeatureLab.Web/Program.cs`, this design document.
  - Verify: full format, build, test, migration-drift, vulnerability, and confidentiality gates.

## What I noticed

- Episodes 13–21 have strong continuity, so changing domains immediately after the first outbox lesson would leave the most important delivery failure unresolved.
- The outbox's existing crash-window test is the ideal cold open for Episode 22 because the test already proves the problem honestly.
- Episode 25 needs both failure classes: a prolonged transient failure proves budget exhaustion, while a permanent rejection proves the immediate-terminal path.
- The UI belongs last. Its projection becomes simple and truthful only after retry, lease, terminal, and observability states exist in the domain.

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
| --- | --- | --- | ---: | --- | --- |
| CEO Review | `/plan-ceo-review` | Scope and strategy | 0 | - | The approved builder design supplied the scope decision. |
| Codex Review | `/codex review` | Independent second opinion | 0 | - | Not required for this plan-stage slice. |
| Eng Review | `/plan-eng-review` | Architecture and tests (required) | 1 | CLEAR | One retention ambiguity resolved; 0 open issues and 0 critical gaps. |
| Design Review | `/plan-design-review` | UI/UX gaps | 0 | - | No UI change in Episode 22. |
| DX Review | `/plan-devex-review` | Developer experience gaps | 0 | - | Existing run/test workflow is unchanged. |

**OUTSIDE VOICE:** A read-only engineering audit agreed that the existing key and interface are sufficient, no migration is needed, and provider retention must be explicit.

**VERDICT:** ENG CLEARED - ready to implement Episode 22 only.

NO UNRESOLVED DECISIONS
