# ADR-019 — Native Transformers artifact for Florence-2 Large

**Status:** Accepted
**Date:** 2026-08-19

## Context

Phase 3 requires Florence-2 Large in `STANDARD` and `FULL` semantic modes. The
original Microsoft checkpoint pinned during Phase 0 uses the legacy repository
representation and is not load-compatible with the native Florence-2 implementation
in the installed Transformers 5.15 runtime. It fails without executing inference,
and attempting to force the native class exposes incompatible state-dict names and
shapes.

The `florence-community/Florence-2-large` repository identifies its artifact as the
official Transformers-converted checkpoint of Microsoft's Florence-2 Large. It is
the same model family and size, not a replacement VLM.

## Decision

Use the native Transformers-converted Florence-2 Large artifact with:

- repository: `florence-community/Florence-2-large`;
- immutable revision: `4271c66b88cdbc05735372ec13b2360108de5317`;
- `model.safetensors` SHA-256:
  `7715423d6549bf1e71188bdd84f4ac960cc0597886af24a5ef7b66f128660685`;
- weight size: `1,553,541,016` bytes;
- license metadata: MIT;
- provenance: native Transformers conversion of Microsoft Florence-2 Large;
- loader: `Florence2ForConditionalGeneration` plus `AutoProcessor`;
- CUDA dtype on the reference GPU: BF16;
- production loading: local-only, with `trust_remote_code=False` and exact hash
  validation before model construction.

The active physical directory is revision-qualified while the logical model ID remains
`florence-2-large`. Model execution history records the immutable revision and primary
weight hash. The original Microsoft artifact remains retained under its historical
directory and is no longer the active Phase 3 artifact.

## Compatibility evidence

On the RTX 4060 Ti 8 GB reference PC with Python 3.12.12, Torch 2.13.0+cu130 and
Transformers 5.15.0:

- native config, processor and full model load passed;
- 917 tensors loaded with zero missing, unexpected or mismatched keys;
- `<CAPTION>`, `<DETAILED_CAPTION>`, `<OD>` and `<DENSE_REGION_CAPTION>` passed on a
  managed test copy;
- parsed bounding boxes remained inside the 7008×4672 image bounds;
- deterministic repeated outputs matched exactly;
- five load/inference/release cycles completed without OOM;
- peak CUDA allocation was approximately 1.91 GB;
- post-release allocation/reservation remained stable at approximately 8.1/20 MiB;
- the product `STANDARD` pipeline executed Florence without Qwen, and `FULL` executed
  Florence then Qwen sequentially with a combined peak allocation of approximately
  3.79 GB;
- missing, partial and wrong-hash artifacts produced structured, non-retryable failures;
- Worker crash/restart, malformed response, timeout, cancellation and durable
  `STANDARD`/`FULL` replay completed without an unbounded retry or duplicate row.

## Consequences

- No legacy Microsoft modeling code, remote code, dependency downgrade, alternate
  Florence size, quantization or mismatched-weight fallback is used.
- Missing or wrong weights fail structurally and do not trigger a silent download or
  substitution.
- The model remains `BASELINE` and pending project quality benchmarks; this artifact
  gate does not make it `APPROVED`.
- The converted repository does not embed a standalone LICENSE file in the snapshot;
  its model card declares MIT and links the upstream Microsoft LICENSE. That provenance
  link and this ADR must be retained with the inventory.
- The model card also describes this exact checkpoint as a continued-pretrained
  Florence-2 Large using 0.1B samples and warns that it may not be trained well. This
  does not change the model family or native-loader result, but it is not assumed to be
  byte-equivalent to the legacy Microsoft weights and requires project-dataset quality
  benchmarking before approval.
- Acceptance covers the exact artifact and runtime decision only. Florence remains
  `BASELINE`, not `APPROVED`, until quality and performance are benchmarked on the
  PHOTO AI FACTORY dataset.
