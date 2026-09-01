# Releases and builds

This document is the release-process source of truth. It covers the branch roles, GitHub Actions workflows, release formats, and the in-app updater that consumes stable release assets.

## Branches

| Branch | Role | Release result |
| --- | --- | --- |
| `main` | Stable, releasable history. | Pushes and stable release publications run **Build DepotTools**. |
| `development` | Integration branch for the next build. Merge current `main` before beginning development work. | Every push runs **Publish DepotTools Nightly** and replaces the rolling nightly prerelease. |

Keep `development` based on current `main`; otherwise its nightly package can be built from an older application version. The nightly workflow serializes runs rather than cancelling them, so every pushed commit is built in order.

## GitHub Actions

### Build DepotTools

`.github/workflows/build.yml` runs on pushes and pull requests targeting `main` or `master`, manual dispatches, and published stable releases whose tags start with `v`.

It restores dependencies, builds the Release configuration, runs the test suite, and publishes a Windows artifact. On a published stable release it additionally packages Velopack output and replaces that release's assets with the portable ZIP, setup executable, `RELEASES`, `releases.win.json`, `assets.win.json`, and the package files referenced by the update manifest.

The workflow intentionally skips non-`v` release events. This prevents the rolling `nightly` prerelease from entering the stable packaging path.

### Publish DepotTools Nightly

`.github/workflows/nightly.yml` runs only for pushes to `development`.

For each commit it:

1. Restores, builds, tests, and publishes the Windows application on `windows-latest`.
2. Reads the project version from `DepotToolsGui.csproj` and packages it as `<project-version>-nightly.<run-number>`.
3. Force-moves the `nightly` tag to the pushed commit.
4. Creates or edits the single `nightly` prerelease titled **DepotTools Nightly**, removes its prior assets, and uploads the newly packaged assets.

The release remains one prerelease rather than accumulating one release per commit. GitHub does not support pinning a prerelease above the latest stable release; `DepotTools Nightly` therefore stays in GitHub's prerelease section. Its tag URL is:

```text
https://github.com/MagiqueDeveloper/DepotTools/releases/tag/nightly
```

Do not manually retag `nightly`, delete its assets, or edit its target while a nightly run is active. Re-run or repair the workflow instead.

## Release formats

### Stable release

| Field | Required value |
| --- | --- |
| Branch | `main` |
| Tag | `vX.Y.Z` |
| Title | `DepotTools vX.Y.Z` |
| Type | Published, non-prerelease |
| Target | The intended `main` commit |
| Version | Matches `<Version>` in `src/DepotToolsGui/DepotToolsGui.csproj` |

Write release notes with these sections where applicable:

- `## Highlights`
- `## Fixes and reliability` or a feature-specific equivalent
- `## Included commits`
- `**Full Changelog**: <compare URL>`

Do not publish a stable release until the push build for its target commit is green. After publishing, wait for the release-triggered **Build DepotTools** run to complete; it is responsible for the installer, portable ZIP, and update-feed assets.

### Nightly release

| Field | Required value |
| --- | --- |
| Branch | `development` |
| Tag | `nightly` |
| Title | `DepotTools Nightly` |
| Type | Prerelease |
| Target | Current `development` commit |
| Package version | `<project-version>-nightly.<run-number>` |

Nightly assets are for development testing. The in-app updater uses the stable Velopack feed (`prerelease: false`), so normal installations do not silently move to nightly builds.

## In-app updates

The application checks the configured GitHub/Velopack feeds only while it is running. A normal visible startup checks after the main window opens; when a newer stable release exists it presents a modal update choice. Hourly checks are controlled by **Settings → Check for app updates**, which defaults on. An available update also creates a persistent, non-dismissible Settings banner with **Update now**.

An update is downloaded and applied only after the user explicitly chooses **Update now**. Skipping a version suppresses future prompts for that exact version but leaves the Settings banner actionable. The updater tries configured repositories in order and remains silent when every feed is unreachable.

## Operational checks

Use GitHub Actions rather than local macOS builds for WPF release proof:

```text
gh run list --branch development
gh run watch <run-id> --exit-status
gh release view nightly
```

For a stable release, inspect the release-triggered run and its asset list:

```text
gh run list --commit <stable-commit>
gh release view vX.Y.Z --json tagName,name,targetCommitish,assets,url
```

A successful run must show restore, Release build, tests, Windows publish, Velopack packaging, and release-asset replacement as applicable.
