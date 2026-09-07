using System.Text.Json;
using RayLink.App.Models;

namespace RayLink.App.Models;

public sealed class StoredAgentProfile
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Description { get; set; } = "";
    public int AvatarIndex { get; set; }
}
