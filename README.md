# Unreal Project Renamer

Renames an Unreal Engine **C++ project** safely, without breaking your Blueprints, levels
or saved values, and then rebuilds it so it's ready to open.

Renaming a C++ project by hand means renaming the `.uproject`, the modules, the
`*.Target.cs` / `*.Build.cs` files, the `MYGAME_API` macros, config entries and class
names. Then you have to stop every Blueprint and level that points at the old C++ names
from breaking. This tool does all of that for you, and checks the result.

---

## What you need

- **Windows 10 or 11**
- **Unreal Engine 5** installed (Epic Games Launcher, or a registered source build)
- **Visual Studio 2022 or 2026** with the **"Game development with C++"** workload
  (this includes the C++ compiler and a Windows SDK)

There's nothing to install. Unreal's own Python is used behind the scenes.

## Before you start

1. **Commit or back up the project.** The rename changes files in place.
2. **Close the project** in Unreal Editor, Visual Studio and Rider. The tool checks this
   and won't start while the project is open.
3. If teammates or other branches use the project, read
   [Working in a team](#working-in-a-team) first.

## How to use it

1. Double-click **`UnrealProjectRenamer.exe`**.
   Windows may warn about an unrecognised app, because the .exe isn't signed. Choose
   **More info → Run anyway**, or ask IT to allow it if your security software blocks it.
2. **Project (.uproject):** click **Browse...** and pick the project's `.uproject`, or drag
   the `.uproject` (or its folder) onto the window.
3. **New name:** type the new project name. It must start with a letter and use only
   letters, digits and underscores, for example `SpaceRacer` or `Project_X2`. Keep it
   short; Unreal recommends 20 characters or fewer.
4. **Engine version:** leave it on **Keep current**, or pick a newer installed engine to
   upgrade the project at the same time. Picking an **older** engine is blocked
   (see [Messages you might see](#messages-you-might-see)).
5. **Visual Studio:** the newest Visual Studio that works with the chosen engine is picked
   for you. Change it only if you need a specific version.
6. *(Optional)* tick **Open in Unreal Editor when done**.
7. Click **Preview changes**. This changes nothing. It lists every file that will be
   renamed or edited, the redirects that will be added, and any warnings. Read it.
8. Click **Rename** and confirm.

The rename itself takes a few seconds. The app then **builds the project, updates the
assets and checks the plugins**, which can take several minutes. When it says
**"Renamed and built. All steps finished."**, click **Open folder** or open the new
`.uproject`.

You can press **Cancel** while it's building or checking. It's greyed out at the moments
when stopping could leave files half-written. If you cancel, the project is already
renamed; just build it in Visual Studio or Unreal to finish.

## What it changes

| Changed | Not changed |
|---|---|
| The `.uproject` file name and its module list | The **project folder name** (it stays as it is) |
| Files and folders under `Source/` that contain the old name | `Content/` folders and asset names (`/Game/...` paths) |
| The old name inside source code, `*.Build.cs`, `*.Target.cs`, `Config/*.ini`, `MYGAME_API` macros | Engine and plugin names that happen to contain the old name |
| Your project's plugins that **use** the game's code (their references to it) | Those plugins' own names and files |
| The root `README` | Web links (a GitHub URL with the old name keeps working) |
| `Binaries/`, `Intermediate/` and the `.sln` are deleted, then rebuilt | Anything else outside `Source/`, `Config/`, plugins and the root README |

After renaming, it automatically:

1. generates the Visual Studio project files;
2. builds the editor;
3. **resaves assets** that still use the old C++ names. Any asset that shows a warning
   when loaded is **skipped, not saved**, so nothing is saved broken; the log lists them;
4. removes redirects that no asset needs any more (see below);
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

## Working in a team

- Rename on **one machine**, build, open the project and check it, then **commit
  everything in one go**: renamed files, deleted old files, `DefaultEngine.ini` and the
  resaved assets.
- Everyone else should **sync, delete their `Binaries/` and `Intermediate/` folders**, and
  open the new `.uproject`.
- The app removes class and module redirects that no asset in **your** copy uses any more.
  If **other branches** or unsynced machines have assets that still use the old names, or
  you have **shipped save games**, add the removed lines back from your previous commit.
  The log lists what was removed.
- With **Perforce**, check out the whole project first. Read-only files make the rename
  fail partway.

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

## Command line

The app runs `rename_unreal_project.py`, which you can also use directly with Unreal's
Python or any Python 3.8+:

```bash
python rename_unreal_project.py "C:\Projects\MyGame\MyGame.uproject" NewName --dry-run
```

By default it makes a renamed **copy** next to the original. Useful options:
- `--in-place`: rename where it is, like the app does.
- `--keep-folder-name`: with `--in-place`, keep the folder name.
- `--engine-association 5.8`: switch engine.
- `--generate-project-files`, `--build`, `--resave-assets`, `--remove-unused-redirects`,
  `--verify-plugins`, `--open-editor`: the follow-up steps.
- `--allow-common-name`: rename a common-word name.

Run with `--help` for the full list.

## Rebuilding the app (for maintainers)

The app is a single C# file, `App/RenamerApp.cs`, with the Python script embedded in it.
After changing either file, run `App\build.bat`. It needs Visual Studio's C# compiler, and
the .exe runs on the .NET Framework built into Windows.
