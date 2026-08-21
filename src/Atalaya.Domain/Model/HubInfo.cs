namespace Atalaya.Domain.Model;

/// <summary>Root hub descriptor (§2), stored as <c>hub.json</c>.</summary>
public sealed class HubInfo
{
    public int SchemaVersion { get; set; } = 1;

    public required string OrganizationName { get; set; }
}
