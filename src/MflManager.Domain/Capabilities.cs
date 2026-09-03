namespace MflManager.Domain;

public enum CapabilityAccess { Public, Owner, Commissioner }

public sealed record MflCapability(
    string RequestType,
    string Category,
    bool IsMutation,
    CapabilityAccess Access,
    IReadOnlyList<string> RequiredParameters,
    string ResponseFormat,
    string UiArea,
    string? AgentTool,
    bool IsImplemented,
    string? UnsupportedReason = null);

public sealed record CapabilitySet(IReadOnlyList<MflCapability> Items)
{
    public bool Can(string requestType) => Items.Any(x =>
        x.IsImplemented && string.Equals(x.RequestType, requestType, StringComparison.OrdinalIgnoreCase));
}
