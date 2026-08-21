using Velopack.Sources;

namespace DepotToolsGui.Services;

/// <summary>
/// An <see cref="IFileDownloader"/> for Velopack's auto-updater that makes its GitHub requests resilient
/// in blocked/throttled regions (e.g. China). Velopack calls this for BOTH the release feed (the API, via
/// DownloadString/DownloadBytes) and the package download (the .nupkg, via DownloadFile). Each call tries
/// the DIRECT GitHub URL first, then falls through the same mirrors as <see cref="GithubProxy"/>
/// (<see cref="GithubProxy.Candidates"/>), so the in-app self-update works where github.com is blocked.
/// Wraps Velopack's stock <see cref="HttpClientFileDownloader"/>, only swapping the URL per attempt.
/// </summary>
public class ProxiedFileDownloader : IFileDownloader
{
    private readonly HttpClientFileDownloader _inner = new();

    public async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        Exception? last = null;
        foreach (var candidate in GithubProxy.Candidates(url))
        {
            try
            {
                await _inner.DownloadFile(candidate, targetFile, progress, headers, timeout, cancelToken);
                return;
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception($"Failed to download {url} from GitHub or any mirror.");
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Exception? last = null;
        foreach (var candidate in GithubProxy.Candidates(url))
        {
            try { return await _inner.DownloadBytes(candidate, headers, timeout); }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception($"Failed to download {url} from GitHub or any mirror.");
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Exception? last = null;
        foreach (var candidate in GithubProxy.Candidates(url))
        {
            try { return await _inner.DownloadString(candidate, headers, timeout); }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception($"Failed to download {url} from GitHub or any mirror.");
    }
}
