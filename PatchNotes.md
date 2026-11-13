# VFS Unreal Project Build Tool — Patch Notes

Newest version first. Each pushed version gets its own section.

---

### 0.19.0

- Finds Unreal Engine versions that the Epic launcher installed but didn't register in Windows (e.g. UE 5.7 / 5.8 showing as "not installed"). The tool now also reads the launcher's own install list.
- Upgrade Project Version now also updates the project's `.Target.cs` files to the new engine's latest settings (e.g. for 5.8: `BuildSettingsVersion.V7` and `EngineIncludeOrderVersion.Unreal5_8`). Only older values are raised; `Latest` is left alone.
- The newer build settings are stricter. If the build after an upgrade fails, the log says which code to fix (e.g. a missing return value, unreachable code).
- Upgrade Project Version works even when the project's current engine isn't installed on this PC.
- When a build fails, the log and the failure popup name the project plugins the errors are in (e.g. an FMOD version made for an older engine) and say to get the plugin's version for the new engine.
- The README links to these patch notes.
- **Auto-update:** on opening, the app checks GitHub for a newer version. If there is one, a window shows "Updating to v..." with a progress bar, installs it and opens the new version.
- The app always opens, even offline: if GitHub can't be reached (no connection, no answer within 5 seconds, a dropped download) it opens the current version with no popup. Closing the update window skips the update.

---

### 0.18.1

- The README title shows the app version.
- README section headings are one size smaller, so GitHub draws no line under them.

### 0.18.0

- README rewritten as short steps for each action, with 16 screenshots.
- Rename refuses names that would break a project, before anything changes: Windows reserved names (CON, PRN, AUX, NUL, COM1-9, LPT1-9), C++ keywords, and changes that only alter capitalisation.

### 0.17.1

- Package Build ends with a short yellow "CONFIRM WORKING BUILD" note listing the packaged .exe.
- The "old C++ names load through redirects" note only appears after a rename.

### 0.17.0

- The log ends with a yellow "CHECK IN UNREAL EDITOR" list: what to open and why (skipped or failed assets with their reason, resaved maps, start-up maps, the packaged .exe).
- Save Output Log still writes plain text.

### 0.16.2

- Assets that saved with a warning count as saved, not FAILED.
- Project-wide warnings (e.g. Gameplay Ability System's "No GameplayCueNotifyPaths") no longer count against the asset that happened to load first.

### 0.16.1

- Plugin check: no more false FAILs for plugins with no content (GameplayAbilities) or self-loaded modules (Streamline).
- Skipped / failed assets show up to two "why:" lines with the actual warning.

### 0.16.0

- The app starts on "Regenerate Visual Studio project files" the first time; after that it reopens on the last action used.

### 0.15.0

- New action: **Upgrade Project Version**. Switches the project to a newer installed engine, cleans generated files, regenerates project files and builds. Downgrades are blocked.
- Engine version only shows for Upgrade; other actions use the project's own engine.

### 0.14.0

- New action: **Regenerate Visual Studio project files**. Deletes `.vs`, `Binaries`, `DerivedDataCache`, `Intermediate`, `.vsconfig` and `.sln`/`.slnx`, regenerates and builds.
- Open folder opens the packaged build after a successful Package, otherwise the project folder.

### 0.13.1

- Each action keeps its own saved inputs correctly when switching actions and on restart.
- Package is greyed out while Build location is empty.

### 0.13.0

- Fixed a crash when clicking Browse next to an empty Build location.
- Packaged builds default to `<project>\Builds`.

### 0.12.0

- Renamed to **VFS Unreal Project Build Tool**.
- New actions: **Build Solution** and **Package Build** (Development / Shipping, clean or incremental cook, Build location, Launch .exe when done), plus a Rebuild Solution checkbox.
- Remembers each action's last inputs.
- Rename also deletes Rider's `.idea` cache and skips locked files with a warning instead of stopping.

### 0.11.0

- A popup appears when a rename fails or needs attention, with the reason and a Save Output Log button.

### 0.10.0

- Sounds for start, success and failure.
- Save Output Log button.
- New name capped at 20 characters; inputs greyed out while a run is going.
- Fixed the plugin check failing healthy code-only plugins.

### 0.9.1

- Handles project plugins more safely when renaming: names a plugin declares itself stay unchanged, and plugins that reference the game only from config are updated too.

### 0.9.0

- First versioned release: renames an Unreal C++ project with redirects, then regenerates project files, builds, resaves assets, removes unused redirects and checks plugins.
- Version number and app icon added.
