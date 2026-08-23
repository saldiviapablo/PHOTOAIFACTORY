# PHOTO AI FACTORY — PHASE 7 TEST EVIDENCE

**Date:** 2026-08-23
**Test Environment:** Windows 10/11, .NET 10.0 Release, Python 3.12.12

---

## 1. Test Suite Summary

| Suite | Tests Executed | Passed | Failed | Skipped | Duration |
|---|---|---|---|---|---|
| C# Foundation Tests | 112 | 112 | 0 | 0 | 4.0s |
| C# Simulation Tests | 124 | 124 | 0 | 0 | 55.0s |
| Python Integration Tests (`tests/python`) | 33 | 33 | 0 | 0 | 5.2s |
| Python Worker Tests (`src/python/ai-worker/tests`) | 4 | 4 | 0 | 0 | 1.8s |
| **Total** | **273** | **273** | **0** | **0** | **~66.0s** |

---

## 2. Lead Review Fix Validations

1. **`COMPLETED` iff `OUTPUT_PUBLISHED` Invariant**:
   - `QaOrchestrator` on `QA_REPROCESS` spawns child job and transitions parent to `ReviewFinal` (not `Completed`).
   - `ReviewService.ReprocessAsync` spawns child job, resolves review item as `REPROCESS`, and leaves parent in `ReviewFinal` (not `Completed`).
   - `GeneralInvariant_AllCompletedJobsMustHaveOutputPublishedCheckpoint` verifies across database that every completed job has `OUTPUT_PUBLISHED` checkpoint.
2. **Final History Conflict Handling**:
   - `FinalHistoryWriter_IdenticalReplaySucceeds_AndContentConflictThrows` demonstrates identical replay succeeds, and corrupted/mismatched history throws `InvalidOperationException`.
3. **Deterministic Publication Collision**:
   - `PublishService_DeterministicCollisionAndReplay_AndConflictFailsClosed` proves first collision writes to deterministic `{stem}_{jobId}{ext}`, replay returns same path, and conflicting content on alternate path fails closed.
4. **JPEG-Only Publication**:
   - `PublishService_RejectsNonJpegCandidates` proves non-JPEG candidates (`.png`) are rejected with `NotSupportedException`.
5. **`force_decision` Production Isolation**:
   - `test_phase7_qa_force_decision_ignored_in_production_mode` proves production mode ignores `force_decision` and uses real metrics.
   - `test_phase7_qa_forced_decisions_under_test_flag` proves test environment allows it when `PAF_ALLOW_TEST_FORCE_DECISION=1`.
6. **Security & Package Vulnerabilities**:
   - `dotnet list package --vulnerable --include-transitive` returned 0 vulnerable packages across all solution projects.
