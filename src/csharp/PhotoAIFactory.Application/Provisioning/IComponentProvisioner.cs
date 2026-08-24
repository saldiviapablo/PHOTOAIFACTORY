namespace PhotoAIFactory.Application.Provisioning;

public interface IComponentProvisioner
{
    Task<IReadOnlyList<ComponentState>> InspectAllAsync(CancellationToken cancellationToken = default);

    Task<ComponentState> InspectAsync(string componentId, CancellationToken cancellationToken = default);

    Task<ComponentState> ProvisionAsync(
        string componentId,
        IProgress<ComponentProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ComponentState> RepairAsync(
        string componentId,
        IProgress<ComponentProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ComponentState>> ProvisionRequiredAsync(
        IProgress<ComponentProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IReleaseManifestService
{
    Task<ReleaseManifest> LoadReleaseManifestAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ComponentDescriptor>> LoadComponentDescriptorsAsync(CancellationToken cancellationToken = default);

    Task<bool> ValidateProductionGuardsAsync(CancellationToken cancellationToken = default);
}
