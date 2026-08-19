# State Machine v1.0

## Photo/Job main flow

```text
RECEIVED
→ ANALYZING
→ APPROVED / REVIEW_PRE / REJECTED_PRE

APPROVED
→ QUEUED
→ PROCESSING
→ QA
→ COMPLETED / REVIEW_FINAL / ERROR
```

## Additional terminal states

```text
CANCELLED
REJECTED_FINAL
```

## Auxiliary states

```text
WAITING_FOR_FILE
CANCEL_REQUESTED
RETRYING
INTERRUPTED
```

## Project states

```text
RUNNING
PAUSE_REQUESTED
PAUSED
STOP_REQUESTED
STOPPED
BLOCKED_STORAGE
COMPONENT_UNHEALTHY
```

## Transition invariants

- `COMPLETED` requires `OUTPUT_PUBLISHED`.
- `OUTPUT_PUBLISHED` requires validated JPEG and persisted history.
- `CANCELLED` never deletes original/history.
- `QA_REPROCESS` creates a new attempt/Job relationship; it does not overwrite history.
- `INTERRUPTED` resumes only from a validated checkpoint.
- `REVIEW_FINAL` does not block the queue.
