# Design: Episode 21 Protected Transactional Outbox

Generated from the approved Episode 21 engineering review on 2026-09-04
Branch: main
Repo: CSharpFeatureLab
Status: APPROVED
Mode: Builder

## Problem Statement

Episode 20 keeps the raw invitation code out of Blazor, but it still commits the
invitation before calling an external delivery adapter. Those two writes cannot share
one transaction. Compensation makes failure visible, yet a crash can still leave an
open invitation with no durable delivery work. Episode 21 must make the database commit
complete: the invitation and a protected delivery intent are written atomically, then a
server-side dispatcher performs the external handoff after the request has finished.

## Settled Slice

This episode delivers the complete vertical slice selected in the engineering review:

- create the invitation and its outbox row in one EF Core transaction;
- protect a full, versioned, time-limited envelope rather than only the raw code;
- return a truthful `queued` result to HTTP and Blazor after the commit;
- dispatch in one application process with at-least-once semantics;
- delete successful work only after the adapter returns normally;
- fail closed when a protected envelope is expired, corrupt or mismatched; and
- prove atomicity, confidentiality, restart recovery and honest UI behaviour.

The episode intentionally does not promise exactly-once delivery.

## Trust and Data Boundaries

The invitation table continues to store only a SHA-256 hash of the acceptance code.
The raw code exists briefly while the trusted server builds this envelope:

```csharp
public sealed record TenantInvitationOutboxEnvelope(
    int Version,
    Guid InvitationId,
    Guid TenantId,
    string NormalizedRecipient,
    string Code,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        "TenantInvitationOutboxEnvelope { NormalizedRecipient = [REDACTED], Code = [REDACTED] }";
}
```

The envelope binds the capability to the invitation identifier, trusted workspace,
server-normalized recipient and expiry. It is serialized and protected with ASP.NET
Core Data Protection using one explicit, versioned purpose string. The outbox stores
only the protected payload, its invitation identifier and non-secret scheduling
metadata. Neither the unprotected envelope nor a generated secret-bearing record is
logged, returned by HTTP, placed in browser state or passed as a job argument.

Protection is time limited to the invitation expiry. Dispatch must unprotect with the
same purpose and then compare every envelope field with authoritative database state.
Data Protection is encryption at rest for this application boundary, not a substitute
for database access control or durable key-ring management.

## Persistence Model

Add a `TenantInvitationOutboxMessage` entity keyed by invitation identifier with:

- `InvitationId`, also a unique foreign key to the invitation;
- `TenantId`, used only for server-side consistency and query scoping;
- `ProtectedPayload`, required with an explicit maximum length;
- `CreatedAt`, supplied by `TimeProvider`.

There is deliberately no retry counter, lease, provider message identifier, terminal
failure status or operator payload view in Episode 21. A unique invitation key prevents
two live delivery intents for the same invitation. An enqueue-time-and-invitation index
supports deterministic queue reads. Deleting an invitation must not expose or orphan a
delivery payload; cancellation closes the invitation, and the dispatcher removes
obsolete work.

## Atomic Command

Replace persistence-then-delivery with one store operation. Under the existing trusted
owner and workspace checks, it:

1. validates and normalizes the requested recipient;
2. creates the raw code and stores only its hash on the invitation;
3. builds the full envelope from trusted, persisted values;
4. time-protects the serialized envelope until the invitation expiry;
5. adds the invitation and outbox row to the same `DbContext`; and
6. persists both through the same final `SaveChangesAsync` and transaction.

EF Core wraps that save in one transaction. A constraint or simulated save failure
persists neither row. A successful commit persists both rows. The raw code does not
escape the store result; it is available later only by unprotecting the outbox payload
inside the dispatcher.

The endpoint returns `202 Accepted` with only the invitation identifier, expiry and
exact delivery status `queued`. `queued` means durable work exists in this database. It
does not mean an adapter accepted it, a message reached a recipient, or the invitation
was accepted.

## Truthful Blazor State

The client maps only the exact safe `queued` response. The panel reports “Invitation
queued for delivery” and refreshes the existing code-free pending list. It contains no
code field, delivery-provider detail or polling claim. A failed list refresh does not
reverse the successful enqueue; it warns that the management view may be stale.

Validation, authorization, stale-owner, active-member and duplicate-conflict outcomes
remain code-free and enqueue no work. The browser never selects the trusted workspace
or constructs the protected envelope.

## Single-Process Dispatcher

Register one hosted dispatcher in the application process. Its loop pauses through the
injected `TimeProvider` after every pass, and each pass reads a small, deterministic
batch ordered by enqueue time and invitation identifier. A process-local page offset
advances past only the rows retained from a full batch, then wraps at the end. This
lets later work progress without pretending to provide durable retry scheduling. Each
item is processed with a fresh dependency-injection scope and database context:

1. load the outbox row and its invitation;
2. unprotect the envelope inside the server process;
3. validate its version and compare identifier, workspace, normalized recipient and
   expiry with the invitation row;
4. if the invitation is still open and unexpired, call the existing
   `ITenantInvitationDelivery` adapter;
5. after normal adapter completion, delete the outbox row and save; and
6. on adapter exception or host cancellation, keep the row for a later pass.

This is intentionally single-process. It has no distributed lease, skip-locked query
or competing-consumer protocol. The page offset resets on process restart; persistent
backoff and fair multi-worker retry scheduling remain Episode 22 concerns. A configured
batch limit prevents an unbounded scan. The loop honours host shutdown, but request
cancellation never owns durable work.

The semantics are at least once. If the adapter accepts a handoff and the process stops
before the delete commits, the next pass may hand off the same invitation again. The
lesson and UI must state this explicitly. Episode 22 will add idempotent Hangfire retry
rules and safe observability; Episode 21 does not disguise duplicate possibility.

## Poison Rows Fail Closed

An expired, corrupt, unsupported-version or database-mismatched envelope is not a
transient delivery failure. In one database save, the dispatcher closes the still-open
invitation and deletes the poison outbox row. If the invitation was already accepted,
cancelled or closed, it only deletes the obsolete outbox row.

The dispatcher emits a sanitized structured event containing a stable event
identifier and the non-secret invitation identifier. It never logs
the recipient, protected payload, raw code, unprotect exception text or provider body.
Failing closed prevents a hot loop and ensures a tampered payload cannot be delivered
to a different recipient or workspace.

## Recovery and Key Lifetime

Outbox rows survive request completion and process restart. The host must use a durable
Data Protection key ring whose lifetime covers live invitations; losing required keys
turns affected rows into poison and closes those invitations safely. Development and
tests may use isolated ephemeral keys, but restart tests must explicitly preserve the
test key ring.

The outbox is not a permanent secret archive. Successful, obsolete and poison rows are
deleted. Invitation expiry is the upper bound for decryptability and delivery value.

## Test Plan

1. A current owner receives `202 Accepted`, safe identifier and expiry, and `queued`.
2. One save creates exactly one invitation and one outbox row; a failed save creates
   neither.
3. The invitation stores only the code hash, and the outbox payload is not plaintext.
4. Unprotecting through the server service yields the expected full envelope before
   expiry, while an unrelated purpose cannot unprotect it.
5. The dispatcher passes the server-normalized recipient and raw code to the adapter,
   then removes the outbox row after normal completion.
6. The delivered code hashes to the invitation value and accepts exactly once.
7. Adapter failure and host cancellation retain the outbox row for another pass.
8. A fully retained batch does not starve a later queued invitation in the same process.
9. A simulated crash window can deliver twice, documenting at-least-once behaviour
   without claiming idempotency.
10. A restart with the same key ring processes committed work that survived the first
   host instance.
11. Expired, corrupt, unsupported-version and field-mismatched payloads close an open
    invitation, delete the row and emit only sanitized identifiers.
12. Accepted or cancelled invitations cause obsolete outbox work to be deleted without
    changing their terminal state.
13. Anonymous, non-owner, stale-owner, invalid, active-member and conflicting requests
    enqueue nothing.
14. HTTP bodies, pending-list responses, logs, rendered Blazor markup and persistence
    never expose the raw code or unprotected envelope.
15. Existing acceptance, cancellation, listing and component refresh tests stay green.

## Lesson Timing

- 0:00-0:45: show the truthful queued result and later delivery.
- 0:45-1:35: revisit the dual-write crash window.
- 1:35-2:45: define and time-protect the full envelope.
- 2:45-4:10: save invitation and outbox atomically.
- 4:10-5:05: return `202 queued` to Blazor.
- 5:05-6:35: process a bounded batch in the hosted dispatcher.
- 6:35-7:35: fail closed for poison rows.
- 7:35-8:45: prove recovery and at-least-once behaviour.
- 8:45-9:20: recap and defer idempotent retries to Episode 22.

## Explicit Episode 22 Deferrals

Episode 22 owns idempotent Hangfire retries, multi-worker coordination, backoff and
jitter, provider idempotency keys, dead-letter retention, operator dashboards, metrics,
alert thresholds and safe delivery observability. Episode 21 adds no provider-specific
SDK, production message template or promise of exactly-once delivery.

## Success Criteria

- Invitation state and durable delivery intent commit atomically.
- The complete delivery envelope is protected, time limited and validated before use.
- The endpoint and Blazor report only the truthful `queued` state.
- The single-process dispatcher recovers committed work after request completion and
  restart, while documenting at-least-once delivery.
- Poison work fails closed without leaking the capability or recipient.
- The lesson remains understandable in 6-10 minutes and all repository tests pass.

## Distribution Plan

The public companion repository, ordered checkpoint, renderer and existing release
automation distribute this lesson. The implementation must create
`episode/21-protected-transactional-outbox` before any release package is rendered.
YouTube metadata uses the exact `Multi-tenancy` playlist. No real provider credential,
recipient address or production key material belongs in the companion or artifacts.
