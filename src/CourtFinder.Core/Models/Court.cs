namespace CourtFinder.Core.Models;

public class Court
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Surface { get; set; } = string.Empty;
    public bool HasLights { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

