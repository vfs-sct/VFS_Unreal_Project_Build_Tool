# CLAUDE.md — Unreal Project Renamer

Notes for the next Claude Code session working on this tool. Written 4 Nov 2026.

**Location:** `C:\Users\Public\Repos\VFS_Unreal_Project_Renamer`, a clone of
github.com/vfs-sct/VFS_Unreal_Project_Renamer (shared with the user's team). It was
developed in `C:\Users\skearney\Documents\Unreal Projects\Claude\Tools\UnrealProjectRenamer`
and moved here on 4 Nov. Teammates get `UnrealProjectRenamer.exe` + `README.md`
(user-facing guide); this file is for developers.

## Rules (from the user)

1. **Push and commit only when the user asks.** Never push on your own.
   **Every time the user asks to push, bump the version first** (`Program.Version` in
   `App/RenamerApp.cs`, `major.minor.patch`), judged on everything since the last push
   (`git log origin/main..HEAD` plus uncommitted changes):
   - **Major** (X.0.0): only with the user's **approval** — ask first. If they don't
     approve, bump minor instead.
   - **Minor** (0.X.0): new features or behaviour changes.
   - **Patch** (0.0.X): hotfixes.
   Bumping minor resets patch to 0; bumping major resets both. Then rebuild with
   `App\build.bat`, commit the bump with the rest, and push. Tell the user the old and new
   version and why.
2. This PC runs **SentinelOne**. On 4 Nov it flagged a Claude Code session, killed it and
   rolled back files the session had touched (IT cleared it the same day). The likely
   trigger: copying `cmd.exe` as `UnrealEditor.exe` to fake an open editor. **Never** copy
   or rename system binaries to fake other programs, and avoid other malware-looking test
   patterns (hidden processes with odd command lines, mass process killing, injecting
   input into other apps). Test by calling the app's code in-process instead (see the
   test harness below).
3. Teammates use git, not Perforce — don't mention Perforce in the README or app.
4. Work on scratch copies of projects, never on the user's own projects in
   `C:\Users\Public\Repos\`.

## What the tool is

Renames an Unreal Engine C++ project correctly: files, modules, `_API` macros, config,
`.uproject`, with `[CoreRedirects]` so existing Blueprints/levels keep working; then
regenerates project files, builds, resaves assets, cleans up redirects, and verifies
plugins (incl. FMOD).

| File | Purpose |
|---|---|
| `rename_unreal_project.py` | All rename logic (CLI). Single source of truth. |
| `App/RenamerApp.cs` | Windows GUI (WinForms, .NET Framework 4.8, C# 7.3). |
| `App/build.bat` | Compiles the GUI with Visual Studio's Roslyn `csc.exe`, embedding the .py as a resource. |
| `UnrealProjectRenamer.exe` | Built GUI (~95 KB). Unsigned. |
| `RenameUnrealProject.bat` | Runs the .py with system Python or Unreal's bundled Python. |
| `screenshot.png` | Latest screenshot of the GUI. |

There is no system Python on this PC; use Unreal's:
`C:\Program Files\Epic Games\UE_5.8\Engine\Binaries\ThirdParty\Python3\Win64\python.exe`.
The GUI extracts the embedded script to `%TEMP%\UnrealProjectRenamer\rename_<guid>.py`
(unique per run, deleted afterwards) and runs it with `-u -I`.

**After editing the .py or .cs, rebuild with `App\build.bat`** (the .exe embeds the .py).
**Version:** `Program.Version` in `RenamerApp.cs` (started at 0.9.0). It's shown in the window
title and the .exe's Properties > Details. Bump it for each build handed out.
**Icon:** `App/RenamerApp.ico`, built in by `build.bat` (`-win32icon`). It's UE 5.8's
`Engine/Build/Windows/Resources/Default.ico` recoloured: the black circle is red (204,24,30)
and the U is still white. Every size is a PNG frame. The window reuses the .exe's icon.
References: System.Windows.Forms, System.Drawing, System.Web.Extensions (JSON),
System.Management (WMI).

## Script (`rename_unreal_project.py`) — CLI

`rename_unreal_project.py <.uproject or folder> <NewName> [options]`

- Default: makes a renamed **copy** (`-o` to choose the folder). `--in-place` renames where
  it is (deletes Binaries/Intermediate/.sln, renames the folder unless `--keep-folder-name`).
- `--dry-run`, `--yes`, `--engine-association 5.8`
- Follow-ups: `--generate-project-files`, `--build`, `--resave-assets`,
  `--remove-unused-redirects`, `--verify-plugins`, `--open-editor`
- `--visual-studio 2026 --visual-studio-path <install> --visual-studio-msvc <toolset folder>`
- `--allow-common-name` (see below).
- Exit code 0 = all good; 1 = refused, nothing written (clash, path too long, downgrade);
  2 = renamed but a follow-up step failed; 3 = old name is a common word and needs
  `--allow-common-name` (nothing written — the GUI asks the user and re-runs).

How it works:
- Replaces **whole identifiers** containing the old name in Source/, Config/, the
  .uproject and the root README; `OLDNAME_API` macros; BOMs and line endings preserved.
  Never touched: `/Game/...` paths, `http(s)://` links, `TargetType.X`.
- **Protected names** (fix 7): header, module (`.Build.cs`) and plugin (`.uplugin`) names
  from `Engine/Source`, `Engine/Plugins` and the project's `Plugins/` that contain the old
  name are left alone, also with an A/U/F/I/E/T/S prefix (`UTundraToolsLibrary`). The
  project's own names (`defined`: modules, files, reflected types and members, plain
  class/struct/enum/namespace declarations incl. `.cs`) always win. The scan takes ~5 s.
  Names the project's **plugins declare** (`plugin_declared_names`: reflected types, members,
  delegates, enum values, plain declarations, file names) are protected too, so a plugin's
  own `FTundraPluginData` / `TundraHelperFn` stays as it is in the plugin and in the game (added 6 Nov).
- **Clashes** (fix 7) are errors: a renamed module equal to an engine/plugin module or
  plugin (case-insensitive), or a renamed class/struct equal to an engine header name.
  Paths ≥ 250 chars are errors, > 200 warnings.
- **Common old names** (fix 4: in `RISKY_NAMES`, < 3 chars, or contained in any engine
  name): only the project's own names (`own_map`) are renamed, nothing else. Real rename
  needs `--allow-common-name` (exit 3 otherwise). Tested: a project called `Game` →
  `Arena` builds (before this, `TargetType.Game`, `FDefaultGameModuleImpl`, and
  `GameFramework/` got renamed).
- **Plugins that use the project** (fix 5): a project plugin whose `.Build.cs` names a
  project module, or any of whose code/config files mention `/Script/<project module>`
  (`plugin_mentions_modules`, added 6 Nov, e.g. a config-only reference), gets the project's identifiers (`plugin_map`) renamed in its source,
  `.Build.cs`, `.uplugin` and `.ini` — nothing else, so its own names stay.
- **Downgrades refused**: `--engine-association` older than the current engine (version
  from `5.x` or a source build's `Engine/Build/Build.version`).
- Existing redirect lines: only their `New...=` side is rewritten, so old chains collapse
  to point straight at the newest name.
- Adds `[CoreRedirects]` to `Config/DefaultEngine.ini`: PackageRedirects per module;
  Class/Struct/Enum redirects for renamed reflected types (enums with `ValueChanges` for
  renamed values); and (fix 1) Property/Function redirects for renamed UPROPERTY/UFUNCTION
  members, written with the owner's new path (what Unreal looks up) plus the old path,
  and FunctionRedirects for renamed dynamic delegates (`X__DelegateSignature`).
  Header scanning blanks comments, skips strings, handles parens in meta, bit fields.
- Resave: scans `.uasset/.umap` bytes for `/Script/<OldModule>`, loads each in a headless
  editor first and **only saves assets that load with no warnings** (others are skipped so
  nothing is saved broken), then saves.
- Remove redirects: only Package/Class/Struct/Enum lines for old modules that no asset
  references any more. Property/Function redirects are **always kept** (Blueprint nodes may
  still store the old member names).
- Verify plugins: every enabled plugin (from .uproject + project `Plugins/`) must be enabled,
  mounted **if it can hold content** (`CanContainContent` or a `Content/` folder;
  code-only plugins never mount), its editor-loadable modules loaded (`/Script/<Module>`
  exists, **or** `UnrealEditor-<Module>[-Win64-DebugGame].dll` is loaded, for modules with no
  UObjects), and assets using it must load. Reports OK / WARN (loads with warnings) / FAIL.
  Fixed 6 Nov: before, healthy code-only plugins failed both checks.

## GUI (`UnrealProjectRenamer.exe`) — current behaviour and user's choices

The user asked for these specifically — keep them:
- **Dark theme only** (Claude-app colours, orange accent), no theme picker. Custom-drawn
  check boxes/radios (`DarkCheck`/`DarkRadio`), dark dialogs (`DarkDialog`) instead of MessageBox.
- **Always renames in place and keeps the folder name.** No mode / copy-to / rename-folder
  options in the UI.
- **Always runs every follow-up step**: generate project files, build, resave assets,
  remove unused redirects (on), verify plugins. No checkboxes for these.
- Only option left: **"Open in Unreal Editor when done"**, default **on** (changed 6 Nov at the user's request).
- **New name** is capped at 20 characters (`MaxLength`) and shows a grey "Maximum 20 characters"
  hint while empty (`EM_SETCUEBANNER`; needs visual styles, which `Main` enables). While a preview or rename runs,
  `SetInputsLocked` greys out Project, Browse, New name, Engine version, Visual Studio and
  the Open-in-editor box, and ignores dropped projects (added 6 Nov, tested in-process).
- **Sounds** (`Sfx`, added 6 Nov): synthesized in code as in-memory .wav (no sound files),
  played with `SoundPlayer` through the `MainForm.PlaySound` hook. Start = real rename begins
  (not Preview, not the common-name re-run); Success = "Renamed and built"; Failure = not
  renamed, or a follow-up step failed. Cancel and the common-name question are silent.
  Tested in-process: refused rename (`Water`) → Start, Failure; full real rename of the
  Tundra test project (4 code-only plugins, after the plugin-check fix) → Start, Success.
- **Save Output Log** button (added 6 Nov), after Open folder: Save dialog (.txt, default
  `<Project>_rename_log_<yyyy-MM-dd_HHmm>.txt` in the project folder), writes the log as UTF-8.
  Disabled while running and while the log is empty (`UpdateSaveLogButton`, also on
  `log.TextChanged`). `MainForm.AskSavePath` hook replaces the dialog in tests. Tested.
- The log is cleared at the start of every run. Output lines carry their run number
  (`runId`, `AppendFromRun`), so late lines from a stopped run can't reach the new log.
  Tested: cancel a preview, start again → only the new run's output.
- **Engine version** dropdown: "Keep current" + installed engines; pre-selects the newest
  engine if the project's association isn't registered on this PC (unless that's a
  downgrade). Picking an **older** engine shows an "Engine downgrade blocked" dialog with
  the reason and puts the choice back (user's request). `Run` checks it again.
- **Common old name**: when the script exits 3, the app shows "Common project name" with
  the script's COMMON NAME warning and "Rename anyway" / "Cancel"; Rename anyway re-runs
  with `--allow-common-name`. Dialogs go through `MainForm.Notify` / `MainForm.AskUser`
  so tests can answer them.
- **Visual Studio** dropdown under Engine version: all installs (vswhere), newest first,
  default = newest one usable by the chosen engine.

Fixes added at the end of the session (from the "app edge cases" list):
1. Refuses to rename while the project is open in Unreal Editor / Visual Studio / Rider
   (WMI command lines + IDE window titles), with "Check again". **Tested — works.**
3. **Cancel** button, enabled only when safe: not while files are renamed (before
   `Done:`), not while assets are saved, not while redirects are removed, not after the
   editor is launched. Kills the process tree with `taskkill /T /F`. Closing the window
   follows the same rules. **Tested** (real rename + build, stopped mid-compile: button
   greyed during rename, enabled after, all child processes gone, status/log correct).
   The "Stop the remaining steps?" dialog itself wasn't clicked through.
5. Checks the chosen Visual Studio against the engine's
   `Engine\Config\Windows\Windows_SDK.json` (min VS version per year, min/banned MSVC,
   Windows SDK range) and UBT-supported years; shows a note and disables Rename (Preview
   still works). **Tested** (real installs + fake failure cases + window state). Testing
   found and fixed a bug: cl.exe reports 19.xx, Unreal's lists use 14.xx.
6. Blocks Rename when the project's engine isn't installed / no Visual Studio / no
   Windows SDK. **Tested.** The "no Unreal Engine installed at all" message wasn't (it reads
   the registry directly and can't be faked in-process).
8. Unique temp script per run. **Tested.**

Test harness (from 4 Nov, in that session's scratchpad, may be gone; modes `logic`, `ui`,
`cancel`, `downgrade`, `common`): a `Harness.cs`
compiled together with `App/RenamerApp.cs` via `csc -main:UnrealProjectRenamer.Harness`,
which calls the app's private methods/fields by reflection. No input injection, no fake
programs. Rebuild it that way if more tests are needed.

## Hard-won technical notes

- UBT 5.8 accepts `VisualStudio2022` and `VisualStudio2026`. Project files:
  `-ProjectFileFormat=VisualStudio<year> -<year>`. Build: `-Compiler=VisualStudio<year>`
  **plus `-CompilerVersion=<toolset folder>`** — without it UBT still picks its preferred
  toolset (14.44 from VS 2022) even when 2026 is requested.
- VS 2026 (MSVC 14.51) builds fine; UBT warns it's newer than the preferred 14.50.
- Generating for VS 2022 with UE 5.8 leaves the .NET 10 C# tool projects out of the .sln
  (expected; UBT prints a message).
- Banned/minimum MSVC versions must be compared against the **real compiler version**,
  not the toolset folder name (14.44.35207, which falls inside a banned range while the
  real compiler doesn't). `cl.exe`'s file version is **19**.44.35228; Unreal (and UBT)
  write it as **14**.44.35228 — same minor/build, major 14.
- Headless editor jobs: `UnrealEditor-Cmd.exe <uproject> -run=pythonscript
  -script=<file>.py -EnablePlugins=PythonScriptPlugin -unattended -nosplash -nop4
  -nullrhi -stdout -AllowStdOutLogVerbosity`.
  - **`-AllowStdOutLogVerbosity` is required** or `unreal.log` lines never reach stdout.
  - Pass the script by name and put its folder on `UE_PYTHONPATH`; paths with spaces or
    `\6`-style sequences break `-script=` arguments.
  - Call `unreal.AssetRegistryHelpers.get_asset_registry().search_all_assets(True)` first,
    or plugin content isn't found in a commandlet.
  - `unreal.PluginBlueprintLibrary` has `get_enabled_plugin_names` / `is_plugin_mounted`.
  - `Blueprint.parent_class` isn't exposed to Python; use
    `unreal.MathLibrary.class_is_child_of`. `Actor.add_component_by_class` isn't exposed.
- FMOD's own `/FMODStudio/AnimNotify_FMODPlayEvent` always logs "saved with empty engine
  version" (also in the original project) → reported as WARN, not a rename problem.
- PowerShell 5.1 gotchas when testing: `$null` passed to a P/Invoke string becomes `""`
  (use `[NullString]::Value`); unquoted `-script=a.py` gets split; `2>&1` on native exes
  gives fake failure exit codes; `h` and `r` are aliases (don't name functions `H`/`R`).
- A game module's headers must be under `Source/<Module>/Public/` for a plugin to include them.
- Python property names in the editor: `TundraScore` → `tundra_score`; bool `bTundraFlag`
  → `tundra_flag`; enum `ETundraMode::TundraActive` → `unreal.TundraMode.TUNDRA_ACTIVE`.
- WinForms controls show up in UI Automation as generic "Pane"s; drive them via their
  window handles (WM_SETTEXT, BM_CLICK, CB_SETCURSEL) — but see the safety notes above.

## What was verified (before the incident)

- Copy and in-place renames of UnrealProjectBase-derived projects build with UE 5.8.
- Blueprints/levels made before a rename load after it via redirects (values intact);
  with redirects disabled they fail (control test). After resave + redirect removal they
  load with no redirects at all.
- Full GUI run on a project with FMOD: generate (VS 2026), build, resave, remove
  redirects, verify plugins (FMODStudio WARN as above, FMODStudioNiagara/Water/
  ModelingToolsEditorMode OK).
- VS dropdown: 2026 → `.sln` "Version 18" + MSVC 14.51; 2022 → "Version 17" + MSVC 14.44.
- 4 Nov, after the incident (test project "Tundra" with a `TundraTools` plugin the game
  uses and a `Helper` plugin that uses the game): Tundra → Glacier in place builds (both
  plugins); a Blueprint saved before the rename keeps TundraScore=42, TundraSpeed (struct)
  =7.5, bTundraFlag=true and enum value TundraActive under the new names with **no resave**;
  control without the Property redirects/ValueChanges → defaults (0 / 1.0 / false / enum
  unreadable).
- Blueprint graph nodes (a test-only editor plugin `BPTestKit` added a `TundraDouble` call
  node and a `TundraScore` get node): after the rename both resolve to `GlacierDouble` /
  `GlacierScore`, no errors. Without FunctionRedirects the call node resolves to nothing
  and the Blueprint still says "up to date" (silent breakage); without PropertyRedirects
  the get node breaks the same way. The new-owner form of each redirect alone is enough.
  (`UBlueprint::Status`: 2 = error, 3 = up to date.)
- Event dispatchers: a "Bind Event to OnTundraEvent" node with a custom event handler
  (`BPTestKit::AddDelegateNodes`) works after the rename (OnGlacierEvent, new signature,
  handler still linked, no errors). Without the **PropertyRedirects** for OnTundraEvent it
  fails to compile ("Could not find an event-dispatcher named OnTundraEvent"). Without the
  `__DelegateSignature` FunctionRedirect it still works (the signature comes from the
  property); that redirect is kept for delegate-typed function parameters (not tested).
- Clash (`Water`, `Engine`), downgrade (5.8 → 5.6), path length and common-name refusals;
  GUI downgrade dialog and common-name confirm (Cancel and Rename anyway) via the harness.

## Still to do

- **Done 4 Nov:** property/function/delegate/enum-value redirects; common-name handling;
  plugins that use the game module; protection of engine/plugin names; clash and path
  checks; root README updated; engine downgrades blocked (script + GUI dialog).
- Edge cases still **not fixed** (worst first):
  1. No backup, and no check for uncommitted git/Perforce changes, before an in-place rename.
  2. A rename that dies halfway (crash, power loss, killed) has no resume or undo.
  3. Perforce read-only files, or files locked by OneDrive/sync tools/antivirus → rename
     fails partway.
  4. Redirect removal is always on in the GUI → assets on other branches/unsynced
     machines and shipped save games lose the old class paths.
  5. Blueprint-only projects: build step reports failure instead of skipping.
  6. Other files (.github workflows, build scripts, .gitignore, docs other than the root
     README) aren't updated; `/Game/<OldName>` content folders aren't renamed; other
     casings of the name (`tundra`, `TUNDRA` outside `_API`) aren't replaced.
  7. Common-name mode leaves the project's own local variables/plain functions that
     contain the name unrenamed (consistent, compiles, but not renamed).
- App edge cases not done: folder with several .uproject files picks the first silently; very long logs
  may hit the TextBox limit; keyboard focus isn't visible on the custom check boxes; the
  .exe is unsigned (SmartScreen / security tools).
- The user wanted the app usable by teammates: consider code-signing the .exe.
- Read-only-file handling, if added, should be described generically (no Perforce).
