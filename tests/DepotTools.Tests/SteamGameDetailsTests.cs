using DepotToolsGui.Services;
using Xunit;

namespace DepotTools.Tests;

public sealed class SteamGameDetailsTests : IDisposable
{
    private const long AppId = 9_998_885_551;
    private readonly string _detailsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DepotToolsGui",
        "details",
        $"{AppId}.json");

    [Fact]
    public void GetGameDetails_MapsAddPageMetadataFromSteamDetails()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_detailsPath)!);
        File.WriteAllText(_detailsPath, """
            {
              "steam_appid": 9998885551,
              "name": "STAR WARS Zero Company",
              "type": "game",
              "header_image": "https://cdn.example.test/header.jpg",
              "genres": [{ "description": "Strategy" }, { "description": "Turn-Based" }],
              "release_date": { "date": "2026" }
            }
            """);

        var cache = new SteamAppInfoCache(new CacheService());
        var details = cache.GetGameDetails(AppId);

        Assert.NotNull(details);
        Assert.Equal("STAR WARS Zero Company", details.Name);
        Assert.Equal("game", details.Type);
        Assert.Equal("https://cdn.example.test/header.jpg", details.HeaderImage);
        Assert.Equal(["Strategy", "Turn-Based"], details.Genres);
        Assert.Equal("2026", details.ReleaseDate);
    }

    public void Dispose()
    {
        try { File.Delete(_detailsPath); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
