using DepotToolsGui.Resources;
using DepotToolsGui.ViewModels;
using Xunit;

namespace DepotTools.Tests;

public class SettingsAndModePresentationTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, "smm_existing", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "smm_existing", true)]
    public void DepotBoxStats_AreShownOnlyForASavedCustomApiKey(
        bool useApiKey, string? savedKey, bool expected)
    {
        Assert.Equal(expected, SettingsViewModel.ShouldShowDepotBoxStats(useApiKey, savedKey));
    }

    [Fact]
    public void CustomUnlockerDescription_EndsWithAPeriod()
    {
        Assert.EndsWith(".", Strings.Mode_Desc_Custom);
    }
}
