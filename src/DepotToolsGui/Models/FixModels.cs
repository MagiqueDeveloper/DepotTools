using System.Text.Json.Serialization;

namespace DepotToolsGui.Models;

/// <summary>DepotBox /api/game-fixes response.</summary>
public class DenuvoListingsResponse
{
    [JsonPropertyName("games")] public List<DenuvoGameListing> Games { get; set; } = [];
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
}

public class DenuvoGameListing
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("headerImage")] public string? HeaderImage { get; set; }
    [JsonPropertyName("fixes")] public List<DenuvoFix> Fixes { get; set; } = [];
    [JsonIgnore] public int FixCount => Fixes.Count;
    [JsonIgnore] public List<DenuvoTag> Tags => Fixes
        .SelectMany(f => f.Tags.Select(tag => new DenuvoTag { Id = tag, Name = FixTagNames.DisplayName(tag), Slug = tag }))
        .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .ToList();
}

public class DenuvoTag
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("color")] public string? Color { get; set; }
}

/// <summary>DepotBox returns the game's nested fixes directly, so this is an alias-shaped wrapper.</summary>
public class DenuvoFixesResponse
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("headerImage")] public string? HeaderImage { get; set; }
    [JsonPropertyName("fixes")] public List<DenuvoFix> Fixes { get; set; } = [];
}

public class DenuvoFix
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("downloadName")] public string Title { get; set; } = "";
    [JsonPropertyName("size")] public string? Description { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonIgnore] public bool HasManifest => false;
    [JsonIgnore] public bool HasFix => true;
    [JsonPropertyName("manifestFilename")] public string? ManifestFilename { get; set; }
    [JsonPropertyName("filename")] public string? FixFilename { get; set; }
    [JsonIgnore] public string? CreatedAt => null;
}

public class DenuvoDownloadResponse
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

/// <summary>Known fix tags and their display names. The API returns lowercase slugs ("online",
/// "bypass", "hypervisor"); this maps them to proper names and fixes the filter order.</summary>
public static class FixTagNames
{
    public const string Online = "online";
    public const string Bypass = "bypass";
    public const string Hypervisor = "hypervisor";

    /// <summary>The filter pills in display order: Bypass → Hypervisor → Online Fix.</summary>
    public static readonly (string Id, string Display)[] OrderedPills =
    [
        (Bypass, "Bypass"),
        (Hypervisor, "Hypervisor"),
        (Online, "Online Fix"),
    ];

    /// <summary>Display name for a raw tag id: "online" → "Online Fix", "bypass" → "Bypass". Unknown
    /// tags are title-cased as a fallback.</summary>
    public static string DisplayName(string id)
    {
        foreach (var (pid, display) in OrderedPills)
            if (string.Equals(pid, id, StringComparison.OrdinalIgnoreCase)) return display;
        return string.IsNullOrEmpty(id) ? id : char.ToUpperInvariant(id[0]) + id[1..];
    }

    /// <summary>Sort key matching <see cref="OrderedPills"/> (unknown tags sort last).</summary>
    public static int Order(string id)
    {
        for (var i = 0; i < OrderedPills.Length; i++)
            if (string.Equals(OrderedPills[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }
}
