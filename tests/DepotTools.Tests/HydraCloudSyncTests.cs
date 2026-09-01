using System.IO;
using DepotToolsGui.Services;
using Xunit;

namespace DepotTools.Tests;

public sealed class HydraCloudSyncTests
{
    [Fact]
    public void AggregateHash_IsIndependentOfFileOrder()
    {
        const string a = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string b = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var first = HydraCloudSyncService.AggregateHashForTests([
            ("two/save.dat", b, 2), ("one/save.dat", a, 1)]);
        var second = HydraCloudSyncService.AggregateHashForTests([
            ("one/save.dat", a, 1), ("two/save.dat", b, 2)]);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("../escape.dat")]
    [InlineData("folder/../escape.dat")]
    [InlineData("/absolute.dat")]
    [InlineData("C:/absolute.dat")]
    [InlineData("folder//file.dat")]
    [InlineData("..\\escape.dat")] // backslash variant: guards the fix-zip entry use in ApplyFix
    public void RestorePath_RejectsUnsafeRelativePaths(string relative)
    {
        Assert.Throws<InvalidDataException>(() =>
            HydraCloudSyncService.SafeCombineForTests(Path.GetTempPath(), relative));
    }

    [Fact]
    public void RestorePath_AllowsSafeRelativePathsInsideRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "DepotToolsHydraCloudTests");
        string result = HydraCloudSyncService.SafeCombineForTests(root, "game/save.dat");
        Assert.StartsWith(Path.GetFullPath(root), result, StringComparison.OrdinalIgnoreCase);
    }
}