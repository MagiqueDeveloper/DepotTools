<h1>
  <img src="assets/depottools-icon.png" alt="DepotTools icon" width="48" height="48" align="absmiddle" />
  DepotTools
</h1>

A Windows desktop client for managing Steam manifests, Lua files, fixes, and game save backups.

DepotTools can search Steam games through DepotBox, manage local manifest and Lua configuration files, switch supported Steam tool modes, and sync supported game saves with Hydra Cloud.

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- A local Steam installation
- A DepotBox API key (only when using an API Key)
- Microsoft Edge WebView2 Runtime for Hydra Cloud sign-in
- An active Hydra Cloud subscription for Hydra Cloud sync

## Installation

Download the latest build from the [releases](https://github.com/MagiqueDeveloper/DepotTools/releases/latest) page. The release includes a setup installer and a portable ZIP.

### Nightly builds

The rolling [DepotTools Nightly](https://github.com/MagiqueDeveloper/DepotTools/releases/tag/nightly) prerelease is built from `development` after every commit. It is intended for testing changes before a stable release.

## Features

- Search Steam games and view DepotBox details, manifests, availability, DLC, and fixes
- Install and manage local Lua files
- Manage Steam manifest files and DLC configuration
- Download game depot content from Steam's CDN and launch SteamAutoCrack (fetched at runtime)
- Choose between supported Steam tool modes or use a custom unlocker
- Sync supported Steam game saves with Hydra Cloud using Ludusavi
- Drag and drop supported files into the app
- 29-language UI

## Credits / Adjacent software

- [LuaTools](https://github.com/madoiscool/LuaTools): original Windows application and codebase that DepotTools was reworked from
- [DepotBox](https://depotbox.org/): external service for Steam game data, manifests, availability, and fixes
- [Hydra Launcher](https://github.com/hydralauncher/hydra): Hydra Cloud client integration reference
- [Ludusavi](https://github.com/mtkennerly/ludusavi): save-game backup engine
- [DepotDownloaderMod](https://github.com/SteamAutoCracks/DepotDownloaderMod): depot content downloader
- [SteamAutoCrack](https://github.com/SteamAutoCracks/Steam-auto-crack): launched from the Downloads page

DepotTools is not affiliated with LuaTools, DepotBox, or Hydra.

## Licence

MIT. See [LICENSE](LICENSE). Review the included notices for third-party software and source code.
