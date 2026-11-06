# CLAUDE.md — VFSUnrealProjectBuildTool (formerly Unreal Project Renamer)

Notes for the next Claude Code session working on this tool. Written 4 Nov 2026.

**Location:** `C:\Users\Public\Repos\VFS_Unreal_Project_Renamer`, a clone of
github.com/vfs-sct/VFS_Unreal_Project_Renamer (shared with the user's team). It was
developed in `C:\Users\skearney\Documents\Unreal Projects\Claude\Tools\UnrealProjectRenamer`
and moved here on 4 Nov. Renamed to **VFSUnrealProjectBuildTool** on 6 Nov (file names; the
display name in the window title, file properties and README heading is "VFS Unreal Project Build
Tool", title "VFS Unreal Project Build Tool v<version>"), including the script (was
`rename_unreal_project.py`). The repo, the `App/RenamerApp.*` files and the C# namespace keep their old
names). Teammates get `VFSUnrealProjectBuildTool.exe` + `README.md`
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
| `VFSUnrealProjectBuildTool.py` | All rename/build/package logic (CLI). Single source of truth. Was `rename_unreal_project.py`; the redirect clean-up still recognises the old `; Added by rename_unreal_project.py` comment in projects renamed before. |
| `App/RenamerApp.cs` | Windows GUI (WinForms, .NET Framework 4.8, C# 7.3). |
| `App/build.bat` | Compiles the GUI with Visual Studio's Roslyn `csc.exe`, embedding the .py as a resource. |
| `VFSUnrealProjectBuildTool.exe` | Built GUI (~150 KB). Unsigned. |
| `screenshot.png` | Latest screenshot of the GUI. |

There is no system Python on this PC; use Unreal's:
`C:\Program Files\Epic Games\UE_5.8\Engine\Binaries\ThirdParty\Python3\Win64\python.exe`.
The GUI extracts the embedded script to `%TEMP%\VFSUnrealProjectBuildTool\rename_<guid>.py`
(unique per run, deleted afterwards) and runs it with `-u -I`.

**After editing the .py or .cs, rebuild with `App\build.bat`** (the .exe embeds the .py).
**Version:** `Program.Version` in `RenamerApp.cs` (started at 0.9.0). It's shown in the window
title and the .exe's Properties > Details. Bump it for each build handed out.
**Icon:** `App/RenamerApp.ico`, built in by `build.bat` (`-win32icon`). It's UE 5.8's
`Engine/Build/Windows/Resources/Default.ico` recoloured: the black circle is red (204,24,30)
and the U is still white. Every size is a PNG frame. The window reuses the .exe's icon.
References: System.Windows.Forms, System.Drawing, System.Web.Extensions (JSON),
System.Management (WMI).

## Script (`VFSUnrealProjectBuildTool.py`) — CLI

`VFSUnrealProjectBuildTool.py <.uproject or folder> <NewName> [options]`, or
`VFSUnrealProjectBuildTool.py <.uproject or folder> --action build|rebuild|package [options]`

- Default: makes a renamed **copy** (`-o` to choose the folder). `--in-place` renames where
  it is (deletes every root `.sln`/`.slnx`/`.code-workspace` first, then Binaries, Intermediate, `.vs` and `.idea`, via `delete_paths`: clears read-only flags, carries on past locked files and prints a warning for each leftover instead of crashing; renames the folder unless `--keep-folder-name`).
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
  nothing is saved broken), then saves. Each SKIPPED / FAILED asset gets up to two "why:" lines
  (`print_problems` / `readable_problem`: timestamp stripped, repeats counted, "(in X.uasset)" when
  the warning comes from another asset it loads); the raw editor lines are no longer printed.
  A save that succeeds but logs a warning counts as **saved** ("saved ... (with warnings ...)"), not
  FAILED; only a save that didn't succeed is FAILED (and keeps the old redirects). Project-wide
  messages logged by whichever asset loads first (`_PROJECT_WIDE_RE`, e.g. GAS "No
  GameplayCueNotifyPaths were specified") don't count against that asset. Found on GD82FP, 6 Nov.
- Remove redirects: only Package/Class/Struct/Enum lines for old modules that no asset
  references any more. Property/Function redirects are **always kept** (Blueprint nodes may
  still store the old member names).
- Verify plugins: every enabled plugin (from .uproject + project `Plugins/`) must be enabled,
  mounted **only if its .uplugin says `CanContainContent: true`** (a Content folder alone doesn't
  count: GameplayAbilities has one but sets it false), its editor-loadable modules loaded
  (`/Script/<Module>` exists, **or** `UnrealEditor-<Module>[-Win64-DebugGame].dll` is loaded, for
  modules with no UObjects; modules with `LoadingPhase: None` are skipped, the plugin loads them
  itself, e.g. Streamline's D3D RHI modules), and assets using it must load. Reports OK / WARN
  (loads with warnings) / FAIL. Fixed 6 Nov: healthy code-only plugins failed both checks; then, on
  a real project (GD82FP), GameplayAbilities and StreamlineCore were false FAILs.

## GUI (`VFSUnrealProjectBuildTool.exe`) — current behaviour and user's choices

The user asked for these specifically — keep them:
- **Dark theme only** (Claude-app colours, orange accent), no theme picker. Custom-drawn
  check boxes/radios (`DarkCheck`/`DarkRadio`), dark dialogs (`DarkDialog`) instead of MessageBox.
- **Always renames in place and keeps the folder name.** No mode / copy-to / rename-folder
  options in the UI.
- **Always runs every follow-up step**: generate project files, build, resave assets,
  remove unused redirects (on), verify plugins. No checkboxes for these.
- **Action** dropdown at the top (`actionBox`, `enum Act`, added 6 Nov): Build Solution,
  **Regenerate Visual Studio project files (default since 6 Nov; app reopens on the last action)**, Package Build, Upgrade Project Version, Rename
  Project. Settings store the enum names, so the order can change.
  **Upgrade Project Version** (`--action upgrade --engine-association X`, added 6 Nov): the **Engine
  version row shows only for this action** (`engineLabel` + its row; every other action uses the
  project's own engine — `SelectedEngine` ignores the dropdown for them, Run passes no
  `--engine-association`, `PickActionEngine` sets Keep current; their "engine isn't installed"
  blocker points to Upgrade). It defaults to the newest installed engine that's an upgrade; Keep
  current or the same engine greys Upgrade out; downgrades stay blocked. A confirm dialog (via
  `AskUser`) warns it can't be undone. The script refuses a missing / same / uninstalled engine
  (exit 1, "Nothing was changed"), switches EngineAssociation, does the Regenerate clean-up, then
  generates project files and builds. On Finish Success: Do nothing / Open in Editor (default
  Open). After a run the app re-reads the project's engine. **Regenerate** (`--action regenerate`, added 6 Nov):
  `run_action` deletes whichever of `.vs`, `Binaries`, `DerivedDataCache`, `Intermediate`,
  `.vsconfig`, `*.sln`, `*.slnx` exist next to the .uproject (missing ones are skipped; `delete_paths`
  carries on past locked files and the run then exits 2 with "warning: couldn't delete ..."), then
  generates project files and builds. On Finish Success: Do nothing / Open in Editor; Rebuild
  Solution hidden (the build is from scratch anyway); button "Regenerate". (Rebuild Solution was an action until 6 Nov; it's now
  the **Rebuild Solution** checkbox under Action, `rebuildBox`, unticked by default, shown for Build
  Solution and Package Build — a rename always builds from scratch.) `ActionChanged` shows New name
  and Preview changes only for Rename, relabels the main button (Build — "Rebuild" when ticked —
  / Package / Rename) and swaps the On Finish Success list. Engine version and Visual Studio show
  for every action. Non-rename actions run the script with `--action build|package
  [--rebuild] --generate-project-files --build` (`run_action`: optional engine switch with the
  downgrade check, then `after_rename`'s steps; no resave/redirect/plugin steps); `--rebuild` adds
  UBT `-Rebuild` (what Rebuild.bat does) and, when packaging, UAT `-clean`. (`--action rebuild`
  still works on the command line.) They're
  cancellable at any time without a question; `FinishedAction` / `ActionFailureMessage` handle the
  end ("Build failed" popup etc.). The "project is open" check runs for every action.
- **"On Finish Success"** dropdown (labelled "When done" until 6 Nov) (`whenDoneBox`; replaced the "Open in Unreal Editor when done"
  checkbox on 6 Nov). Rename / Build: Do nothing; Open in Unreal Editor when done
  (**default**). Package Build: Do nothing; Open in Unreal Editor when done; **Launch .exe
  (default)** — only these three (the user cut the cook/package variants on 6 Nov). Package Build
  always packages the **Build** field's configuration (`buildConfigBox` under Action, Package Build
  only, Development (default) / Shipping). Under it, the **Clean cook content** checkbox
  (`cookContentBox`, Package Build only, **unticked by default**): ticked = clean cook
  (`--clean-cook`), unticked = incremental cook (UAT `-cookincremental`); content is cooked either way.
  Thin separator lines (`AddSeparator`) sit under Project, under the Action group, above and under
  On Finish Success, and under the button row. The buttons start at the left edge, in line with the
  field labels, centred between their two lines. All text boxes and dropdowns are as tall as the
  buttons (`MatchFieldHeights`: text boxes sit in `Frame` panels, dropdowns in `Host` panels, because
  the table never re-measures a combo grown through ItemHeight and combos ignore MinimumSize).
- **Remembered inputs, per action** (added 6 Nov): `%APPDATA%\VFSUnrealProjectBuildTool\settings.json`
  holds `LastAction` and, for each action, Project, Engine, VisualStudio (install path),
  OnFinishSuccess, Rebuild, Build, CleanCook, BuildLocation. `ActionChanged` saves the action being
  left **before** touching the screen, resets the action options to their defaults (On Finish
  Success: Open in Editor, or Launch .exe for Package; Rebuild/Clean cook off; Development;
  `<project>\Builds`) — Project / Engine / VS carry over — then restores the new action's saved
  values (`SaveFields` / `RestoreFields`). Also saved on closing and when a run starts. `LoadSettings`
  restores the start action's own fields before switching to the last-used action, so start-up
  doesn't overwrite them with empty values. Two bugs fixed 6 Nov: start-up wiped Build Solution's
  saved inputs, and switching saved the new action's default On Finish Success into the old action.
  Tested in-process with a scratch settings file (`MainForm.SettingsFile` is replaceable for that):
  each action keeps its own On Finish Success / Rebuild / Build across switches and restarts.
  An empty saved Project never clears the project on screen (it carries over). New name is never
  saved (the user's choice; an old saved NewName is ignored).
- **Open folder** (`OpenFolderTarget`, set in `UpdateState`): greyed out with no project and during a
  run; opens the packaged build after a successful Package (reset when the project changes),
  otherwise the project's folder.
  Rebuild Solution and Clean cook content have hover tooltips explaining them
  (`Theme.DarkToolTip`: owner-drawn in the dark theme, wrapped at 360 px, 30 s display). `WhenDoneArgs` → `--package <config>
  [--clean-cook]` (+ `--open-editor` or `--launch`). The script still supports `--cook` (cook
  only); the app no longer offers it. `--clean-cook` deletes `Saved/Cooked` and does a full cook; `--launch` (starts
  `<dir>/Windows/<Project>.exe` detached; Cancel is disabled from "Launching" on, since killing the
  process tree would close the game). The output folder is the **Build location** row in the
  Action group (`buildLocationBox` + Browse via the `AskFolder` hook, Package Build only; filled
  with `<project>\Builds` when a project is picked unless the user changed it; empty greys out
  Package (the user's choice, 6 Nov; grey hint "Choose a folder for the packaged game"); the old `<project>\Packaged` suggestion, if found in saved
  settings, is swapped for Builds (`IsOldDefaultLocation`); the script's own fallback is also
  `<project>/Builds/<config>`), passed as `--package-dir`; Open folder then opens the build. (Until 6 Nov a folder
  picker popped up on clicking Package instead.) Script: `cook_and_package` runs `RunUAT.bat BuildCookRun -platform=Win64 -cook
  -nocompileeditor [-clientconfig -build -stage -pak -archive -archivedirectory]` after the build
  (and the rename's checks), only if the editor build succeeded; prints `  Packaged: <dir>` /
  `  Cooked: <dir>` or `  cook FAILED` / `  packaging FAILED`. The game build uses UBT's default
  compiler (no `-Compiler`/`-CompilerVersion` passed through).
  **None of the Action / On Finish Success / cook / package / launch work is tested yet** (the user asked
  to hold testing until they've sent all their changes). Test plan: Build, Build with Rebuild ticked, Package (with and without Rebuild)
  Development and Shipping (each finish choice, especially Launch .exe), engine switch on a build
  action, Cancel during a cook, failure popups, the folder picker's cancel.
- **New name** is capped at 20 characters (`MaxLength`) and shows a grey "Maximum 20 characters"
  hint while empty (`EM_SETCUEBANNER`; needs visual styles, which `Main` enables). While a preview or rename runs,
  `SetInputsLocked` greys out Project, Browse, New name, Engine version, Visual Studio and
  the On Finish Success dropdown, and ignores dropped projects (added 6 Nov, tested in-process).
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
- **Failure popup** (added 6 Nov): after a real rename that fails on its own, `ShowFailure`
  (through the `AskUser` hook) shows "Rename failed" (not renamed: the script's `error:` lines,
  "Nothing was changed"; a `Traceback` instead warns files may have changed) or "Rename needs
  attention" (renamed, exit 2: which of engine/project files/build/resave/plugin check failed,
  from `FailureMessage`). Buttons: Save Output Log / OK. Never after Cancel or closing the
  window (both set `cancelled`), nor for Preview. `CancelRun`'s "Stop" question now also goes
  through `AskUser`.
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
