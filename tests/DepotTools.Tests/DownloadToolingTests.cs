using System.IO;
using System.Security.Cryptography;
using DepotToolsGui.Services;
using DepotToolsGui.Services.Downloads;
using Xunit;

namespace DepotTools.Tests;

/// <summary>Unit tests for the pure logic ported with the depot-download feature (AssetHash, ByteFormat,
/// ManifestFile). The download queue itself is dispatcher/DI-bound and stays untested at the unit level.</summary>
public class AssetHashTests
{
    [Fact]
    public void ParseDigest_StripsSha256Prefix()
    {
        Assert.Equal("abc123", AssetHash.ParseDigest("sha256:ABC123"));
        Assert.Equal("abc123", AssetHash.ParseDigest("abc123"));
        Assert.Null(AssetHash.ParseDigest(""));
        Assert.Null(AssetHash.ParseDigest(null));
    }

    [Fact]
    public void Matches_NoAdvertisedDigestIsNotCorrupt()
    {
        string path = Path.GetTempFileName();
        try { Assert.True(AssetHash.Matches(path, null)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OfFile_ComputesLowercaseSha256()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "hello");
            string want = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Assert.Equal(want, AssetHash.OfFile(path));
        }
        finally { File.Delete(path); }
    }
}

public class ByteFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void Size_FormatsWithUnit(long bytes, string expected) => Assert.Equal(expected, ByteFormat.Size(bytes));

    [Fact]
    public void Size_NeverReturnsEmpty() => Assert.NotEqual("", ByteFormat.Size(0));

    [Fact]
    public void Duration_EmptyForNonPositive() => Assert.Equal("", ByteFormat.Duration(TimeSpan.Zero));

    [Fact]
    public void Rate_EmptyWhenNotMeasurable() => Assert.Equal("", ByteFormat.Rate(0));
}

public class ManifestFileTests
{
    [Fact]
    public void TryRead_MissingFileReturnsNull() => Assert.Null(ManifestFile.TryRead(null));

    [Fact]
    public void TryRead_NonexistentPathReturnsNull() =>
        Assert.Null(ManifestFile.TryRead(Path.Combine(Path.GetTempPath(), "no-such.manifest")));

    [Fact]
    public void KeyLooksValid_WithoutManifestIsNoObjection() =>
        Assert.True(ManifestFile.KeyLooksValid(null, new byte[32]));

    [Fact]
    public void KeyLooksValid_WrongKeyLengthIsNoObjection() =>
        Assert.True(ManifestFile.KeyLooksValid("some.manifest", new byte[16]));
}