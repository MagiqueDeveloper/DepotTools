using System.Text.Json;
using DepotToolsGui.Models;
using Xunit;

namespace DepotTools.Tests;

public class DepotBoxFixModelTests
{
    [Fact]
    public void LiveDepotBoxFixSchemaDeserializesStringTags()
    {
        const string json = """
        {
          "success": true,
          "tags": ["bypass", "online"],
          "games": [
            {
              "appid": "3768760",
              "name": "007 First Light",
              "headerImage": "https://example.test/header.jpg",
              "fixes": [
                {
                  "id": "200a334a2dfa5a1c",
                  "downloadName": "007_First_Light_bypass.zip",
                  "filename": "007_First_Light_bypass.zip",
                  "size": "210.5 MB",
                  "badges": ["Bypass"],
                  "tags": ["bypass"]
                }
              ]
            }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<DenuvoListingsResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(result);
        Assert.Single(result!.Games);
        Assert.Single(result.Games[0].Fixes);
        Assert.Equal("bypass", result.Games[0].Fixes[0].Tags[0]);
        Assert.Equal("bypass", result.Games[0].Tags[0].Id);
    }
}
