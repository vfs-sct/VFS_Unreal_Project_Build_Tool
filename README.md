# VFS Unreal Project Build Tool

Builds, rebuilds and packages Unreal Engine **C++ projects** without opening the editor, and
renames them safely, without breaking your Blueprints, levels or saved values.

Renaming a C++ project by hand means renaming the `.uproject`, the modules, the
`*.Target.cs` / `*.Build.cs` files, the `MYGAME_API` macros, config entries and class
names. Then you have to stop every Blueprint and level that points at the old C++ names
from breaking. **Rename Project** does all of that for you, and checks the result.

---

## What you need

- **Windows 10 or 11**
- **Unreal Engine 5** installed (Epic Games Launcher, or a registered source build)
- **Visual Studio 2022 or 2026** (depending on Unreal version) with the **"Game development with C++"** workload
  (this includes the C++ compiler and a Windows SDK)

There's nothing to install and nothing to build: **`VFSUnrealProjectBuildTool.exe` is all
you need** (this README is optional). It contains everything else, uses Unreal's own Python
behind the scenes, and runs on the .NET Framework that comes with Windows.

## Build, rebuild or package

1. **Close the project** in Unreal Editor, Visual Studio and Rider. The tool checks this
   and won't start while the project is open.
2. Double-click **`VFSUnrealProjectBuildTool.exe`**.
   Windows may warn about an unrecognised app, because the .exe isn't signed. Choose
   **More info → Run anyway**.
3. **Project (.uproject):** click **Browse...** and pick the project's `.uproject`, or drag
   the `.uproject` (or its folder) onto the window.
4. **Action:** pick **Build Solution** (the default) or **Package Build**. Tick **Rebuild
   Solution** under it to clean and then build everything (slower; use it when a normal
   build acts up). For Package Build, also pick the **Build**
   configuration: **Development** (the default) or **Shipping**. Content is always cooked;
   by default only what changed is recooked (faster). Tick **Clean cook content** to delete
   old cooked data and cook everything from scratch (slower, safest, e.g. after a rename).
   **Build location** is where the packaged game is saved. It's filled in with the project's
   `Packaged` folder; click **Browse...** to choose another.
5. **Engine version** and **Visual Studio:** leave them as they are, or pick a newer engine
   to upgrade the project before building (an older one is blocked).
6. **On Finish Success:** what happens once it worked: **Do nothing**, or **Open in Unreal
   Editor when done** (the default for Build Solution). Package Build adds **Launch .exe**
   (its default), which starts the packaged game. Package Build keeps the editor closed.
7. Click **Build** (it reads **Rebuild** when Rebuild Solution is ticked) or **Package**.
8. **Wait for it to finish.** A sound plays at the start and the end, and a popup explains
   anything that failed. You can press **Cancel** at any time; nothing is lost. When it's done,
   **Open folder** opens the project, or the packaged build.

## Rename a project

1. **Commit or back up the project.** The rename changes files in place. Do the rename on
   **one machine** only; your teammates pick it up from the commit (step 13).
2. **Close the project** in Unreal Editor, Visual Studio and Rider. The tool checks this
   and won't start while the project is open.
3. Double-click **`VFSUnrealProjectBuildTool.exe`**.
4. **Project (.uproject):** click **Browse...** and pick the project's `.uproject`, or drag
   the `.uproject` (or its folder) onto the window. Then set **Action** to **Rename Project**.
5. **New name:** type the new project name. It must start with a letter and use only
   letters, digits and underscores, for example `SpaceRacer` or `Project_X2`. It can be
   up to 20 characters long (Unreal's recommended limit).
6. **Engine version:** leave it on **Keep current**, or pick a newer installed engine to
   upgrade the project at the same time. Picking an **older** engine is blocked
   (see [Messages you might see](#messages-you-might-see)).
7. **Visual Studio:** the newest Visual Studio that works with the chosen engine is picked
   for you. Change it only if you need a specific version.
8. **On Finish Success:** **Open in Unreal Editor when done** (the default) or **Do nothing**.
9. Click **Preview changes**. This changes nothing. It lists every file that will be
   renamed or edited, the redirects that will be added, and any warnings. Read it.
10. Click **Rename** and confirm.
11. **Wait for it to finish.** The rename itself takes a few seconds. The app then
    **builds the project, updates the assets and checks the plugins**, which can take
    several minutes. You can press **Cancel** while it's building or checking. It's greyed
    out at the moments when stopping could leave files half-written. If you cancel, the
    project is already renamed; just build it in Visual Studio or Unreal to finish.
    A short sound plays when the rename starts, and another when it ends: a rising chime
    if everything worked, two low notes if something needs your attention. If it fails, a
    popup says which step went wrong and whether anything was changed, with a **Save Output
    Log** button. Cancelling never shows this popup.
12. When it says **"Renamed and built. All steps finished."**, click **Open folder** or open
    the new `.uproject`, and check the project works. To keep a record of what happened (or
    to share it when asking for help), click **Save Output Log** and choose where to save it.
13. **Commit everything in one go**: renamed files, deleted old files, `DefaultEngine.ini`
    and the resaved assets.
    The app removes class and module redirects that no asset in **your** copy uses any
    more. If **other branches** or unsynced machines have assets that still use the old
    names, or you have **shipped save games**, add the removed lines back from your
    previous commit before committing. The log lists what was removed.
14. **Teammates** sync, delete their `Binaries/` and `Intermediate/` folders, and open the
    new `.uproject`.

## What it changes

| Changed | Not changed |
|---|---|
| The `.uproject` file name and its module list | The **project folder name** (it stays as it is) |
| Files and folders under `Source/` that contain the old name | `Content/` folders and asset names (`/Game/...` paths) |
| The old name inside source code, `*.Build.cs`, `*.Target.cs`, `Config/*.ini`, `MYGAME_API` macros | Engine and plugin names that happen to contain the old name |
| Your project's plugins that **use** the game's code (their references to it) | Those plugins' own names and files |
| The root `README` | Web links (a GitHub URL with the old name keeps working) |
| `Binaries/`, `Intermediate/`, the old `.sln` / `.slnx` files and the `.vs` / `.idea` caches are deleted; the build files are made again | Anything else outside `Source/`, `Config/`, plugins and the root README |

After renaming, it automatically:

1. generates the Visual Studio project files.
2. builds the editor.
3. **resaves assets** that still use the old C++ names. Any asset that shows a warning
   when loaded is **skipped, not saved**, so nothing is saved broken; the log lists them.
4. removes redirects that no asset needs any more (see below).
5. checks every enabled plugin still loads, and the assets that use it.

## How your Blueprints keep working: redirects

Blueprints, levels and other assets store the C++ names they use, such as
`/Script/MyGame.MyCharacter` or a property called `MyGameHealth`. If those names just
changed, the assets would silently lose values, connections and function calls.

So the tool adds a `[CoreRedirects]` section to `Config/DefaultEngine.ini`. It tells
Unreal, for example, "`MyGameHealth` is now `NewNameHealth`". Redirects are added for the
module, classes, structs, enums and their values, properties, functions and event
dispatchers that had the old name in them.

- **Keep those lines** in `DefaultEngine.ini` and commit them with the rename.
- Redirects for **classes and modules** are removed again once every asset in the project
  has been resaved with the new names. Redirects for **properties and functions** are always
  kept, because Blueprint nodes can still refer to the old names.

## Messages you might see

| Message | What it means / what to do |
|---|---|
| **"... is open in Unreal Editor / Visual Studio / Rider"** | Close the project there, then click **Check again**. |
| **Engine downgrade blocked** | You picked an older engine than the project uses. Assets saved by a newer engine can't be opened by an older one, so this is refused. Pick the same or a newer engine. |
| **Common project name** | The old name is a common word (like `Game` or `Character`) that is also part of engine names. To be safe, only the project's **own** names are renamed: modules, files, its classes, properties and functions. Local variables, comments and text that contain the word stay as they are. Click **Rename anyway** to continue, or **Cancel**. |
| **"... would clash with ... from the engine or a plugin"** | The new name would collide with something that already exists (for example `Water` or `Engine`). Pick another name. Nothing was changed. |
| **"... paths of N characters"** | The renamed files would be too deep for Windows' 260-character path limit. Move the project to a shorter folder (e.g. `C:\Projects\`) or use a shorter name. |
| **Note under Visual Studio** (Rename greyed out) | This Visual Studio, its C++ compiler or the Windows SDK isn't one the chosen engine accepts, or the project's engine isn't installed. The note says what to install or update in the Visual Studio Installer. **Preview** still works. |
| **"Plugin '...' contains the old name; it is NOT renamed"** | Information only. Plugins aren't renamed, so their names are left alone. |
| **"Renamed, but a follow-up step needs attention"** | The rename worked, but the build, resave or plugin check reported a problem. Scroll the log to see which one. |
| **SKIPPED `<asset>` (loaded with warnings)** | That asset already had a problem before the rename, so it wasn't resaved. Open it in the editor, fix it and save it. It still loads through the redirects. |

## Limitations

- **Blueprint-only projects** (no `Source/` folder) aren't supported. There's nothing to
  rename in code; rename those through Unreal's own tools.
- There's **no automatic backup** and no undo. That's why committing first matters.
- Projects inside **OneDrive** or other synced folders can have files locked during the
  rename. Pause syncing first.
- Other files that mention the old name aren't updated: build scripts, CI workflows,
  `.gitignore`, and docs other than the root README. Neither are other spellings of it,
  such as `mygame`.
- Content folders named after the project (`/Game/MyGame/...`) aren't renamed.
