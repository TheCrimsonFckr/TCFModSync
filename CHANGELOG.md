# TCF-ModSync v2.0.0

Renamed from **SPT Mod Sync** to **TCF-ModSync**. This release is the rebrand — sync engine,
client UI, server behavior, and updater logic are unchanged from v1.1.1-beta.

## ⚠️ Breaking: upgrade is not in-place

Both mod GUIDs changed as part of the rename:

- BepInEx client plugin: `com.thecrimsonfuckr.sptmodsync.client` → `com.thecrimsonfuckr.tcfmodsync.client`
- SPT server mod: `com.thecrimsonfuckr.sptmodsync` → `com.thecrimsonfuckr.tcfmodsync`

SPT and BepInEx will treat this as a brand-new mod, not an update to the old one. If you're
upgrading from any 1.x release, **delete the old SptModSync plugin folder** (client and server)
before installing v2.0.0, or the two will run side by side.

## Changed

- Display name: "SPT Mod Sync" → "TCF-ModSync" (shown in-game, logs, and the server mod list)
- All C# namespaces, project files, and assemblies renamed `SptModSync.*` → `TCFModSync.*`
- Solution file: `SptModSync.sln` → `TCFModSync.sln`
- Docs (`README.md`, `BUILD.md`, `functionality.md`, `installation.md`, `troubleshooting.md`)
  updated to match the new name
- Client log file: `SptModSync.Client.log` → `TCFModSync.Client.log`

## Internal / repo cleanup

- Removed the `Tests/TCFModSync.Shared.Tests` project (unit tests only, not a build dependency)
- Fixed a same-day `.gitignore` regression that had accidentally dropped
  `build/Directory.Build.props` and `build/NuGet.config` from version control
- No effect on the shipped mod

## Not changed

- Sync/diff logic, headless detection, staged-download handling, updater behavior — identical to
  v1.1.1-beta
