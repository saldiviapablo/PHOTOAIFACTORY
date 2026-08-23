using PhotoAIFactory.Application.Processing;

namespace PhotoAIFactory.Infrastructure.Processing;

public sealed class ComfyWorkflowCatalog : IComfyWorkflowCatalog
{
    private static readonly IReadOnlyDictionary<string, ComfyTaskDescriptor> ById =
        new Dictionary<string, ComfyTaskDescriptor>(StringComparer.Ordinal)
        {
            ["DENOISE_RGB"] = Blocked(
                "DENOISE_RGB",
                "NAFNet candidate is not production-approved."),
            ["COLOR"] = Blocked(
                "COLOR",
                "3D LUT / LLF-LUT candidates require benchmark and license gate."),
            ["FACE_RETOUCH"] = Blocked(
                "FACE_RETOUCH",
                "V1 face retouch workflow requires conservative benchmark approval."),
            ["FACE_MASKS"] = Blocked(
                "FACE_MASKS",
                "BiSeNet workflow artifact is not production-approved."),
            ["LOW_LIGHT"] = Blocked(
                "LOW_LIGHT",
                "Retinexformer is optional and benchmark/license pending."),
            ["UPSCALE"] = Blocked(
                "UPSCALE",
                "RealPLKSR is optional and benchmark/license pending."),
            ["SHARPNESS"] = Blocked(
                "SHARPNESS",
                "No versioned production sharpening workflow is approved.")
        };

    public IReadOnlyCollection<ComfyTaskDescriptor> Tasks => ById.Values.ToArray();

    public string ValidationWorkflowId => "paf-validation-core-roundtrip-v1";

    public string ValidationWorkflowJson => """
        {
          "1": {
            "class_type": "EmptyImage",
            "inputs": {
              "width": 64,
              "height": 48,
              "batch_size": 1,
              "color": 3368601
            },
            "_meta": {
              "title": "PAF Phase 6 core runtime validation"
            }
          },
          "2": {
            "class_type": "SaveImage",
            "inputs": {
              "images": ["1", 0],
              "filename_prefix": "PAF_PHASE6_VALIDATION/core"
            },
            "_meta": {
              "title": "PAF Phase 6 validation output"
            }
          }
        }
        """;

    public ComfyTaskDescriptor Require(string taskId)
    {
        var normalized = taskId.Trim().ToUpperInvariant();
        return ById.TryGetValue(normalized, out var value)
            ? value
            : throw new InvalidDataException(
                $"Unknown ComfyUI task {taskId}.");
    }

    private static ComfyTaskDescriptor Blocked(
        string taskId,
        string reason) =>
        new(
            taskId,
            false,
            "BENCHMARK_AND_LICENSE_REQUIRED",
            null,
            reason);
}
