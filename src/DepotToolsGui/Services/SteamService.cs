using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DepotToolsGui.Services;

/// <summary>
/// Resolves the Steam install location: auto-detected from the registry, or a user override.
/// Detection confirms the folder actually contains steam.exe.
/// </summary>
public class SteamService(SettingsService settings)
{
    // Known 64-bit Steam registry locations, in priority order.
    private static readonly (RegistryHive Hive, RegistryView View, string SubKey, string Value)[] RegistryLocations =
    [
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "SteamPath"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
    ];

    /// <summary>Steam path detected from the registry (confirmed via steam.exe), or null.</summary>
    public string? AutoDetectedPath => DetectFromRegistry();

    /// <summary>The effective path: user override if set, otherwise the auto-detected one.</summary>
    public string? EffectivePath
    {
        get
        {
            string? overridePath = settings.SteamPathOverride;
            return !string.IsNullOrWhiteSpace(overridePath) ? Normalize(overridePath) : AutoDetectedPath;
        }
    }

    public bool IsOverridden => !string.IsNullOrWhiteSpace(settings.SteamPathOverride);

    /// <summary>True when the effective path exists and contains steam.exe.</summary>
    public bool IsValid => EffectivePath is not null && File.Exists(SteamExePathFor(EffectivePath));

    public static string SteamExePathFor(string steamPath) => Path.Combine(steamPath, "steam.exe");

    /// <summary>Full path to config\stplug-in, or null if Steam isn't located.</summary>
    public string? StPlugInDir =>
        EffectivePath is { } p ? Path.Combine(p, "config", "stplug-in") : null;

    /// <summary>Full path to config\depotcache (where .manifest files go), or null if Steam isn't located.</summary>
    public string? DepotCacheDir =>
        EffectivePath is { } p ? Path.Combine(p, "config", "depotcache") : null;

    /// <summary>Open a store/steam URL or file path with the shell (browser, Steam client, Explorer).</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Open Explorer with the given file selected.</summary>
    public static void RevealInExplorer(string filePath) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });

    /// <summary>Steam's configured UI language from the registry, lowercased. These values match a
    /// depot's <c>config.language</c> verbatim (the registry "english" equals the depot "english"), so
    /// they can be compared directly with no mapping table. Read fresh each call: the user can change
    /// Steam's language without restarting this app. Null when Steam or the value can't be read.</summary>
    public static string? SteamLanguage
    {
        get
        {
            try
            {
                using var key = RegistryKey
                    .OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Valve\Steam");
                return key?.GetValue("Language") is string s && s.Length > 0
                    ? s.Trim().ToLowerInvariant()
                    : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Show a path in Explorer. A file is selected inside its folder; a folder is opened (the
    /// <c>/select</c> gesture <see cref="RevealInExplorer"/> uses would otherwise highlight it in its
    /// parent, wrong for a depot output folder). Returns false when the path is missing or Explorer refuses.</summary>
    public static bool ShowInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (File.Exists(path)) { RevealInExplorer(path); return true; }
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            return false; // deleted since the row was created
        }
        catch { return false; }
    }

    /// <summary>Put text on the clipboard. Returns false instead of throwing (another process can hold the
    /// clipboard open — common with remote-desktop and clipboard-manager tools).</summary>
    public static bool CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try { System.Windows.Clipboard.SetText(text); return true; }
        catch { return false; }
    }

    /// <summary>Kill any running steam.exe (and its tree) and wait for it to exit. Safe to call when
    /// Steam isn't running. Use before changing Steam's files so they aren't locked.</summary>
    /// <summary>True while a Steam client process is running. Appinfo.vdf can't be edited under it.</summary>
    public static bool IsSteamRunning()
    {
        var procs = Process.GetProcessesByName("steam");
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    public void StopSteam()
    {
        foreach (var proc in Process.GetProcessesByName("steam"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(8000); }
            catch { /* already gone / access denied */ }
            finally { proc.Dispose(); }
        }
    }

    /// <summary>Launch Steam from the effective path. Returns false if it can't be located/launched.</summary>
    public bool StartSteam()
    {
        string? path = EffectivePath;
        if (path is null) return false;
        string exe = SteamExePathFor(path);
        if (!File.Exists(exe)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Kill any running steam.exe and relaunch it from the effective path. lua changes only take
    /// effect after a Steam restart. Returns false if Steam can't be located/launched.
    /// </summary>
    public bool RestartSteam()
    {
        StopSteam();
        return StartSteam();
    }

    private static string? DetectFromRegistry()
    {
        foreach (var (hive, view, subKey, value) in RegistryLocations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue(value) is not string raw || string.IsNullOrWhiteSpace(raw)) continue;

                string path = Normalize(raw);
                if (File.Exists(SteamExePathFor(path))) return path;
            }
            catch
            {
                // Inaccessible key: try the next one
            }
        }
        return null;
    }

    /// <summary>Canonicalize separators and restore the casing stored on disk for every directory component.</summary>
    public static string NormalizePathCasing(string path)
    {
        string full;
        try { full = Path.GetFullPath(path.Trim().Replace('/', '\\')); }
        catch { return path.Trim().Replace('/', '\\'); }

        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        string remainder = full[root.Length..];
        foreach (string part in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string actual = part;
            try
            {
                var match = Directory.EnumerateFileSystemEntries(current)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(n => string.Equals(n, part, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match)) actual = match;
            }
            catch { /* inaccessible component: retain the supplied spelling */ }
            current = Path.Combine(current, actual);
        }
        return current;
    }

    private static string Normalize(string path) => NormalizePathCasing(path);
}
