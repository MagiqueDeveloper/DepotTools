# DepotTools

DepotTools is a Windows desktop application for managing local Steam manifest and Lua configuration files with DepotBox integration.

It is a community-maintained rebrand and security-focused continuation of the original LuaTools codebase. DepotTools does **not** include the original LuaTools plugin/loader, Discord account system, analytics, automatic key donation, DRM-removal downloader, or CloudRedirect executable downloader.

> This project is intended for testing and development. Review the code and use it only with games and files you are authorized to modify.

## Features

- Search Steam games through DepotBox.
- Fetch game details, availability, manifests, and fixes using a user-supplied DepotBox API key.
- Install and manage local Steam `stplug-in` Lua files.
- Manage local manifest files and DLC-related configuration.
- Detect Steam installations and libraries while preserving the actual casing of Windows paths.
- Browse and apply DepotBox fixes from the Fixes page.
- Choose and manage supported local Steam tool modes.
- Drag and drop supported files into the application.
- Keep application settings in the user's local Windows application-data directory.
- Store the DepotBox API key locally using Windows-protected settings.
- Use a visible, user-consented Windows installer generated with Velopack.
- Run in multiple translated UI languages.

## Requirements

- Windows 10 or Windows 11.
- .NET 8 Desktop Runtime when using a released installer.
- A DepotBox API key for DepotBox-backed search, downloads, and fixes.
- A local Steam installation for Steam file-management features.

## Installation

Download the latest test or release installer from the repository's **Releases** page. The installer displays welcome, license, and readme information before installation.

For development, clone the repository and build it with the .NET 8 SDK:

```text
dotnet restore DepotToolsGui.sln
dotnet build DepotToolsGui.sln --configuration Release
dotnet test DepotToolsGui.sln --configuration Release
```

The Windows publish output can be produced with:

```text
dotnet publish src/DepotToolsGui/DepotToolsGui.csproj \
  --configuration Release \
  --framework net8.0-windows \
  --runtime win-x64 \
  --self-contained false \
  --output publish
```

## DepotBox setup

DepotTools supports two modes:

- **Use API Key enabled:** the user enters their own DepotBox key in Settings and the app connects directly to `https://depotbox.org`.
- **Use API Key disabled (default):** the app connects to `https://depot.magique.dev` without sending a DepotBox key from the client.

The broker project is maintained separately from the desktop app. It must be deployed behind HTTPS at `https://depot.magique.dev` before the default DepotTools API mode can be used by public users.

For Use API Key mode:

1. Open **Settings**.
2. Enter your own DepotBox API key.
3. Validate and save the key.
4. Use Search, Add, and Fixes with the configured key.

DepotTools does not contain a built-in DepotBox API key. Never commit a key to source control, issue reports, workflow files, or screenshots.

## Relationship to LuaTools

LuaTools is the original project and codebase from which this application was reworked. LuaTools provided the original Steam-oriented application structure and related tooling ideas. DepotTools is not the LuaTools application and does not ship the LuaTools plugin/loader or Discord account features.

Please credit and respect the LuaTools project and its contributors when using or extending code derived from that work. Review the repository history and license for the applicable terms.

## Relationship to DepotBox

DepotBox is the external service used by DepotTools for API-backed game search, game metadata, availability information, manifest downloads, and fixes. DepotTools is a client of DepotBox; it does not operate DepotBox or redistribute DepotBox credentials.

DepotBox service availability, rate limits, API behavior, and terms are controlled by DepotBox. See the official site and API documentation for current service information:

- [DepotBox](https://depotbox.org/)
- [DepotBox API documentation](https://depotbox.org/api-docs)

## Security and privacy

DepotTools is designed to avoid unnecessary external behavior:

- No Discord sign-in or account backend.
- No anonymous analytics or telemetry.
- No automatic donation of Steam decryption keys.
- No plugin injection or LuaTools loader support.
- No downloaded DRM-removal utilities.
- No downloaded CloudRedirect executables or DLLs.
- DepotBox requests are made only for requested application features and use the user's own API key.
- API keys are not compiled into the application or GitHub Actions workflow.

Always inspect third-party code, downloaded releases, and service terms before using them.

## Continuous integration

GitHub Actions builds and tests the project on Windows for pushes, pull requests, manual workflow runs, and published GitHub Releases. It publishes the framework-dependent `win-x64` build as an Actions artifact. When a Release is published, the workflow uses Velopack to build both `DepotTools-win-Portable.zip` and `DepotTools-win-Setup.exe`, then attaches both files directly to the triggering GitHub Release using the repository's workflow token.

Workflow file:

```text
.github/workflows/build.yml
```

## Credits

- **LuaTools contributors** — original application/codebase and Steam tooling heritage.
- **DepotBox** — external API and service for game data, manifests, availability, and fixes.
- **Velopack** — Windows packaging and installer framework.
- **WPF-UI**, **CommunityToolkit.Mvvm**, **Markdig**, and other dependency authors — application libraries.

## License

See [LICENSE](LICENSE). Also review any notices and licenses included with third-party dependencies and code derived from LuaTools.
