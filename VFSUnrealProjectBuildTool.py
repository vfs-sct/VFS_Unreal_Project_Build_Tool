#!/usr/bin/env python3
"""
Rename a C++ Unreal Engine project.

By default makes a renamed COPY of the project and leaves the original alone.
With --in-place it renames the project where it is instead. Either way it:
  * renames the .uproject and every file/folder under Source/ whose name
    contains the old name (modules, *.Target.cs, *.Build.cs, class files)
  * rewrites the old name inside source, build-rule, .uproject and config files
    (OLDNAME_API export macros included)
  * works on whole identifiers, and leaves engine and plugin names alone even
    when they contain the old name (plus /Game/... content paths and web links)
  * also updates the root README and project plugins that use the project's code
  * adds [CoreRedirects] to Config/DefaultEngine.ini so Blueprints, maps and
    other assets that point at the old C++ module/classes/properties/functions/
    enum values still load
  * refuses to rename when the new names would clash with the engine or a
    plugin, when paths get too long, or (without --allow-common-name) when the
    old name is a common word; refuses engine downgrades
  * skips Binaries/, Intermediate/, Saved/, DerivedDataCache/, .vs/ and the
    old .sln, which all have to be regenerated anyway
  * optionally regenerates the Visual Studio project files

Usage:
  VFSUnrealProjectBuildTool.py <path to .uproject or project folder> <NewName> [options]
  VFSUnrealProjectBuildTool.py <path to .uproject or project folder> --action build|rebuild|package [options]

Run with --help for the options. Use --dry-run first to see every change.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

# Folders at the project root that are generated and should not be copied.
SKIP_ROOT_DIRS = {"Binaries", "Intermediate", "Saved", "DerivedDataCache", ".vs", ".idea"}
# Generated folders inside each plugin.
SKIP_PLUGIN_DIRS = {"Intermediate"}
# Root-level generated files.
SKIP_ROOT_FILE_SUFFIXES = {".sln", ".slnx", ".code-workspace"}

# Files whose text gets rewritten.
TEXT_SUFFIXES = {".h", ".hpp", ".hh", ".inl", ".cpp", ".cc", ".c", ".cs", ".ini", ".uproject"}

# Names so common that blind text replacement would hit unrelated code.
RISKY_NAMES = {
    "Game", "Engine", "Core", "Test", "Tests", "Object", "Actor", "Pawn", "Character",
    "Player", "World", "Level", "Map", "Main", "App", "Project", "Module", "Editor",
    "Class", "Name", "Script", "Content", "Source", "Config", "Plugin", "Plugins",
    "Unreal", "UE", "Base", "Default", "Common",
}

IDENT_RE = re.compile(r"^[A-Za-z][A-Za-z0-9_]*$")
IDENT_TOKEN_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
# Text that is never renamed: /Game/... asset paths (Content/ isn't renamed) and web links.
PROTECT_RE = re.compile(r"/Game/[^\s\"'(),;\]\[]*|https?://[^\s\"'<>()\]\[]+|\bTargetType\.[A-Za-z]+")
# Plain C++ declarations, so a project's own non-reflected names count as its own.
_PLAIN_DECL_RE = re.compile(r"\b(?:class|struct)\s+(?:[A-Z0-9_]+_API\s+)?([A-Za-z_]\w*)\s*(?:final\s*)?[:{]"
                            r"|\benum\s+(?:class\s+)?([A-Za-z_]\w*)|\bnamespace\s+([A-Za-z_]\w*)")
# Letters Unreal puts in front of type names (AActor, UObject, FVector, IInterface, EEnum, TArray, SWidget).
TYPE_PREFIXES = "AUFIETS"
# Root-level readme files, which get the new name too.
README_RE = re.compile(r"^readme(\.(md|markdown|txt|rst))?$", re.IGNORECASE)
# Files rewritten inside plugins that use the project's code.
PLUGIN_TEXT_SUFFIXES = {".h", ".hpp", ".hh", ".inl", ".cpp", ".cc", ".c", ".cs", ".ini", ".uplugin"}
# Engine folders that hold no Unreal identifiers worth protecting (and are slow to walk).
_NAME_SCAN_SKIP = {"Intermediate", "Binaries", "ThirdParty", "Content", "Resources", "Shaders", "Extras",
                   "Saved", "DerivedDataCache", "Documentation"}
REDIRECT_LINE_RE = re.compile(
    r"^\s*[+\-.!]?\s*(Class|Struct|Enum|Package|Function|Property|Delegate|Type|ActiveClass|ActiveStruct|ActiveGameName|ActivePlugin)Redirects\s*=",
    re.IGNORECASE,
)
# "EngineAssociation": "<value>" in a .uproject, keeping the file's formatting.
ASSOC_RE = re.compile(r'("EngineAssociation"\s*:\s*")([^"]*)(")')
# The NewName=/NewClassName=/NewGameName=... value inside a redirect line.
NEW_VALUE_RE = re.compile(r'(\bNew\w*\s*=\s*)("[^"]*"|[^,)\s]+)')

# Reflected type declarations, used to build class/struct/enum redirects.
UCLASS_RE = re.compile(r"\b(?:UCLASS|UINTERFACE)\s*\((?:[^()]|\([^()]*\))*\)\s*class\s+(?:[A-Z0-9_]+_API\s+)?([A-Za-z_]\w*)")
USTRUCT_RE = re.compile(r"\bUSTRUCT\s*\((?:[^()]|\([^()]*\))*\)\s*struct\s+(?:[A-Z0-9_]+_API\s+)?([A-Za-z_]\w*)")
UENUM_RE = re.compile(r"\bUENUM\s*\((?:[^()]|\([^()]*\))*\)\s*enum\s+(?:class\s+)?([A-Za-z_]\w*)")


@dataclass
class Plan:
    old: str
    new: str
    src_root: Path
    dst_root: Path
    copies: list[tuple[Path, Path]] = field(default_factory=list)  # (src file, dst file)
    rewrites: dict[Path, int] = field(default_factory=dict)         # dst file -> replacement count
    renamed: list[tuple[str, str]] = field(default_factory=list)     # relative path renames
    redirects: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)                  # problems that stop the rename
    engine_association: str | None = None  # new EngineAssociation value, if changing it
    protected: set[str] = field(default_factory=set)       # engine/plugin names that contain the old name
    protected_seen: set[str] = field(default_factory=set)  # ...of those, the ones the project's files use
    common_hits: list[str] = field(default_factory=list)   # engine names containing the old name
    common_name: bool = False                              # old name is too common to replace safely
    plugin_dirs: list[Path] = field(default_factory=list)  # project plugins that use the project's code
    plugin_map: dict[str, str] = field(default_factory=dict)  # project identifier -> renamed, for those plugins
    own_map: dict[str, str] = field(default_factory=dict)  # the project's own names -> renamed (common names)
    defined: set[str] = field(default_factory=set)         # names the project defines itself


# --------------------------------------------------------------------------- text helpers

def read_text(path: Path) -> tuple[str, str] | None:
    """Return (text, encoding) keeping BOMs and line endings, or None for binary files."""
    data = path.read_bytes()
    for bom, enc in ((b"\xef\xbb\xbf", "utf-8-sig"), (b"\xff\xfe", "utf-16"), (b"\xfe\xff", "utf-16")):
        if data.startswith(bom):
            return data.decode(enc), enc
    if b"\x00" in data:
        return None
    try:
        return data.decode("utf-8"), "utf-8"
    except UnicodeDecodeError:
        return data.decode("cp1252"), "cp1252"


def write_text(path: Path, text: str, encoding: str) -> None:
    path.write_bytes(text.encode(encoding))


def is_protected_name(tok: str, protected: set[str]) -> bool:
    """True for an engine/plugin name, also with a type prefix (UTundraToolsLibrary for TundraToolsLibrary)."""
    return tok in protected or (len(tok) > 1 and tok[0] in TYPE_PREFIXES and tok[1:] in protected)


def make_replacer(old: str, new: str, protected: set[str] = frozenset(), only: dict[str, str] | None = None,
                  seen: set[str] | None = None, defined: set[str] = frozenset()):
    """Build a function that renames `old` -> `new` in a block of text and returns (text, count).

    Works on whole identifiers: one containing the old name is renamed unless it is in `protected`
    (an engine or plugin name, also with an A/U/F/... prefix) - those go into `seen`.
    With `only`, just the identifiers listed in it are replaced (used for plugins that use the project)."""
    old_upper, new_upper = old.upper(), new.upper()
    token_re = re.compile(r"[A-Za-z0-9_]*" + re.escape(old) + r"[A-Za-z0-9_]*")
    # OLDNAME_API, OLDNAMEEDITOR_API, ... (only the export macros, not every upper-case word)
    api_re = re.compile(r"(?<![A-Za-z0-9])" + re.escape(old_upper) + r"([A-Z0-9_]*)_API\b")
    protected_upper = {p.upper() for p in protected}

    def is_protected(tok: str) -> bool:
        # The project's own names are always renamed, even if the engine has one that looks alike.
        return tok not in defined and is_protected_name(tok, protected)

    def replace_segment(seg: str) -> tuple[str, int]:
        count = 0
        if only is not None:
            def sub_only(m: re.Match) -> str:
                nonlocal count
                to = only.get(m.group(0))
                if to is None:
                    return m.group(0)
                count += 1
                return to
            seg = IDENT_TOKEN_RE.sub(sub_only, seg)
            return seg, count

        def sub_token(m: re.Match) -> str:
            nonlocal count
            tok = m.group(0)
            if is_protected(tok):
                if seen is not None:
                    seen.add(tok)
                return tok
            count += tok.count(old)
            return tok.replace(old, new)

        def sub_api(m: re.Match) -> str:
            nonlocal count
            if old_upper + m.group(1) in protected_upper:
                if seen is not None:
                    seen.add(m.group(0))
                return m.group(0)
            count += 1
            return new_upper + m.group(1) + "_API"

        seg = token_re.sub(sub_token, seg)
        if old_upper != old:
            seg = api_re.sub(sub_api, seg)
        return seg, count

    def replace_line(line: str) -> tuple[str, int]:
        # Existing redirects: the Old...= side must keep the old name, but the
        # New...= side is updated so old assets go straight to the new name
        # instead of through a chain of redirects.
        if REDIRECT_LINE_RE.match(line):
            total = 0

            def fix_new_value(m: re.Match) -> str:
                nonlocal total
                value, n = replace_segment(m.group(2))
                total += n
                return m.group(1) + value

            line = NEW_VALUE_RE.sub(fix_new_value, line)
            return line, total
        out, total, pos = [], 0, 0
        for m in PROTECT_RE.finditer(line):
            seg, n = replace_segment(line[pos:m.start()])
            out.append(seg)
            out.append(m.group(0))  # untouched content path
            total += n
            pos = m.end()
        seg, n = replace_segment(line[pos:])
        out.append(seg)
        return "".join(out), total + n

    def replace(text: str) -> tuple[str, int]:
        lines = text.splitlines(keepends=True)
        total = 0
        for i, line in enumerate(lines):
            lines[i], n = replace_line(line)
            total += n
        return "".join(lines), total

    return replace


# --------------------------------------------------------------------------- project discovery

def find_uproject(path: Path) -> Path:
    if path.is_file() and path.suffix.lower() == ".uproject":
        return path
    if path.is_dir():
        found = sorted(path.glob("*.uproject"))
        if len(found) == 1:
            return found[0]
        if len(found) > 1:
            sys.exit(f"error: more than one .uproject in {path}; pass the one you want")
    sys.exit(f"error: no .uproject found at {path}")


def project_modules(uproject: Path) -> list[str]:
    text, _ = read_text(uproject)
    try:
        data = json.loads(text)
    except json.JSONDecodeError as e:
        sys.exit(f"error: could not parse {uproject.name}: {e}")
    return [m.get("Name", "") for m in data.get("Modules", []) if m.get("Name")]


@dataclass
class Reflected:
    """What one module's headers expose to Unreal's reflection (and so to saved assets)."""
    types: dict[str, set[str]] = field(default_factory=lambda: {"Class": set(), "Struct": set(), "Enum": set()})
    members: list[tuple[str, str, str]] = field(default_factory=list)  # ("Property"|"Function", owner, name)
    delegates: set[str] = field(default_factory=set)                   # dynamic delegate types, e.g. FOnHit
    enum_values: dict[str, list[str]] = field(default_factory=dict)    # enum -> its value names


_MACRO_RE = re.compile(r"\b(UCLASS|USTRUCT|UINTERFACE|UENUM|UPROPERTY|UFUNCTION)\s*\(")
_DELEGATE_RE = re.compile(r"\bDECLARE_DYNAMIC_(?:MULTICAST_)?DELEGATE\w*\s*\(\s*([A-Za-z_]\w*)")


def _blank_comments(text: str) -> str:
    """Replace comments with spaces, keeping every position (and string literals) as they are."""
    out, i, n = list(text), 0, len(text)
    while i < n:
        if text[i] == '"':
            i += 1
            while i < n and text[i] not in '"\n':
                i += 2 if text[i] == "\\" else 1
            i += 1
        elif text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            out[i:j] = " " * (j - i)
            i = j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out[i:j] = ["\n" if c == "\n" else " " for c in text[i:j]]
            i = j
        else:
            i += 1
    return "".join(out)


def _close_paren(text: str, i: int) -> int:
    """Index just past the ')' matching the '(' at text[i], skipping string literals."""
    depth, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return n


def _split_top_level(body: str) -> list[str]:
    """Split on commas that aren't inside parentheses (UMETA(...) has its own commas)."""
    parts, depth, cur = [], 0, []
    for c in body:
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
        if c == "," and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(c)
    parts.append("".join(cur))
    return parts


def scan_reflected(source_dir: Path) -> Reflected:
    """Collect reflected types, their UPROPERTY/UFUNCTION members, dynamic delegates and enum values."""
    r = Reflected()
    if not source_dir.is_dir():
        return r
    for header in sorted(source_dir.rglob("*.h")):
        res = read_text(header)
        if res is None:
            continue
        text = _blank_comments(res[0])
        owner = None  # the class/struct the following members belong to
        for m in _MACRO_RE.finditer(text):
            macro = m.group(1)
            end = _close_paren(text, m.end() - 1)
            rest = text[end:end + 600]
            if macro in ("UCLASS", "UINTERFACE", "USTRUCT"):
                d = re.match(r"\s*(?:class|struct)\s+(?:[A-Z0-9_]+_API\s+)?([A-Za-z_]\w*)", rest)
                if d:
                    owner = d.group(1)
                    r.types["Struct" if macro == "USTRUCT" else "Class"].add(owner)
            elif macro == "UENUM":
                d = re.match(r"\s*enum\s+(?:class\s+)?([A-Za-z_]\w*)[^{;]*\{", rest)
                if d:
                    name = d.group(1)
                    r.types["Enum"].add(name)
                    start = end + d.end()
                    body = text[start:text.find("}", start)]
                    r.enum_values[name] = [v.group(1) for e in _split_top_level(body)
                                           if (v := re.match(r"\s*([A-Za-z_]\w*)", e))]
            elif owner and macro == "UPROPERTY":
                decl = re.split(r"[;={]", rest, maxsplit=1)[0]
                decl = re.sub(r"\[[^\]]*\]", "", decl).rstrip()
                decl = re.sub(r":\s*\d+$", "", decl)  # bit fields: uint8 bFlag : 1;
                v = re.search(r"([A-Za-z_]\w*)\s*$", decl)
                if v:
                    r.members.append(("Property", owner, v.group(1)))
            elif owner and macro == "UFUNCTION":
                v = re.search(r"([A-Za-z_]\w*)\s*$", rest.split("(", 1)[0])
                if v:
                    r.members.append(("Function", owner, v.group(1)))
        r.delegates.update(_DELEGATE_RE.findall(text))
    return r


def names_in_tree(root: Path, needles: list[str]) -> set[str]:
    """Header, module (.Build.cs) and plugin (.uplugin) names under `root` containing any of
    `needles` (ignoring case)."""
    lows = [n.lower() for n in needles if n]
    found: set[str] = set()
    if not root.is_dir():
        return found
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in _NAME_SCAN_SKIP and not d.startswith(".")]
        for f in filenames:
            low = f.lower()
            if low.endswith(".build.cs"):
                name = f[:-9]
            elif low.endswith(".uplugin"):
                name = f[:-8]
            elif low.endswith(".h"):
                name = f[:-2]
            else:
                continue
            if any(x in name.lower() for x in lows):
                found.add(name)
    return found


def strip_prefix(kind: str, name: str) -> str:
    """UE stores classes/structs without their A/U/F/I prefix; enums keep theirs."""
    if kind in ("Class", "Struct") and len(name) > 1 and name[0] in "AUFIS" and name[1].isupper():
        return name[1:]
    return name


# --------------------------------------------------------------------------- planning

def should_skip(rel: Path) -> bool:
    parts = rel.parts
    if parts and parts[0] in SKIP_ROOT_DIRS:
        return True
    if len(parts) == 1 and rel.suffix.lower() in SKIP_ROOT_FILE_SUFFIXES:
        return True
    # Plugins/<Name>/Intermediate (and nested plugin folders)
    if "Plugins" in parts:
        idx = parts.index("Plugins")
        if len(parts) > idx + 2 and parts[idx + 2] in SKIP_PLUGIN_DIRS:
            return True
    return False


def renamed_rel(rel: Path, old: str, new: str) -> Path:
    """Rename path components under Source/ and the root .uproject; leave the rest alone."""
    parts = list(rel.parts)
    if len(parts) == 1 and rel.suffix.lower() == ".uproject":
        return Path(new + ".uproject")
    if parts and parts[0] == "Source":
        parts = [parts[0]] + [p.replace(old, new) for p in parts[1:]]
    return Path(*parts)


def rewrite_mode(plan: Plan, rel: Path) -> str | None:
    """'full' (rename every project name), 'plugin' (only the project's own identifiers) or None."""
    suffix = rel.suffix.lower()
    if len(rel.parts) == 1 and README_RE.match(rel.name):
        return "full"
    for pd in plan.plugin_dirs:
        if rel.parts[:len(pd.parts)] == pd.parts:
            return "plugin" if suffix in PLUGIN_TEXT_SUFFIXES else None
    if suffix not in TEXT_SUFFIXES:
        return None
    top = rel.parts[0] if rel.parts else ""
    return "full" if len(rel.parts) == 1 or top in ("Source", "Config") else None


def _plugin_text_files(pdir: Path):
    for f in pdir.rglob("*"):
        if f.is_file() and f.suffix.lower() in PLUGIN_TEXT_SUFFIXES and \
                not any(p in ("Intermediate", "Binaries", "Saved") for p in f.relative_to(pdir).parts):
            yield f


def plugin_declared_names(src_root: Path, old: str) -> set[str]:
    """Names the project's own plugins declare (types, members, delegates, enum values, plain
    class/struct/enum/namespace names and file names) that contain the old name. They belong to the
    plugin, so they're protected even when the game's code mentions them."""
    found: set[str] = set()
    for uplugin in src_root.glob("Plugins/**/*.uplugin"):
        src = uplugin.parent / "Source"
        if not src.is_dir() or should_skip(uplugin.parent.relative_to(src_root)):
            continue
        r = scan_reflected(src)
        for names in r.types.values():
            found |= names
        found |= {m[2] for m in r.members} | r.delegates
        for values in r.enum_values.values():
            found |= set(values)
        for f in src.rglob("*"):
            if not f.is_file() or "Intermediate" in f.parts:
                continue
            found.add(f.name.split(".")[0])
            if f.suffix.lower() in (".h", ".hpp", ".cpp", ".inl", ".cs") and (res := read_text(f)):
                for m in _PLAIN_DECL_RE.finditer(_blank_comments(res[0])):
                    found.add(next(g for g in m.groups() if g))
    return {n for n in found if old in n}


def plugin_mentions_modules(pdir: Path, modules: list[str]) -> bool:
    """True when any of the plugin's code or config files mentions /Script/<game module>, for
    example a config entry pointing at one of the game's classes."""
    if not modules:
        return False
    pat = re.compile(r"/Script/(?:" + "|".join(re.escape(m) for m in modules) + r")(?![A-Za-z0-9_])")
    return any((res := read_text(f)) and pat.search(res[0]) for f in _plugin_text_files(pdir))


def build_plan(src_root: Path, uproject: Path, new: str, dst_root: Path, engine_dir: Path | None = None) -> Plan:
    old = uproject.stem
    plan = Plan(old=old, new=new, src_root=src_root, dst_root=dst_root)

    modules = project_modules(uproject)
    if not modules:
        plan.warnings.append("The .uproject lists no C++ modules - is this a Blueprint-only project?")

    source_dir = src_root / "Source"
    per_module = {mod: scan_reflected(source_dir / mod) for mod in modules}

    # Names the project defines itself - these are the ones being renamed.
    defined = set(modules)
    for r in per_module.values():
        for names in r.types.values():
            defined |= names
        defined |= {m[2] for m in r.members} | r.delegates
        for values in r.enum_values.values():
            defined |= set(values)
    if source_dir.is_dir():
        defined |= {p.name.split(".")[0] for p in source_dir.rglob("*") if p.is_file()}
        for f in source_dir.rglob("*"):
            if f.is_file() and f.suffix.lower() in (".h", ".hpp", ".cpp", ".inl", ".cs") and (res := read_text(f)):
                for m in _PLAIN_DECL_RE.finditer(_blank_comments(res[0])):
                    defined.add(next(g for g in m.groups() if g))
    plan.defined = defined

    # Names from the engine and from plugins: never renamed, even when they contain the old name
    # (a project called "Water" must not rename the Water plugin's classes).
    engine_known: set[str] = set()
    if engine_dir and (engine_dir / "Engine").is_dir():
        print("  checking engine and plugin names...", flush=True)
        for sub in ("Source", "Plugins"):
            engine_known |= names_in_tree(engine_dir / "Engine" / sub, [old, new])
    else:
        plan.warnings.append("The engine wasn't found, so engine names that contain the old name can't be protected. "
                             "Review the dry run carefully.")
    plugin_known = names_in_tree(src_root / "Plugins", [old, new]) | plugin_declared_names(src_root, old)
    plan.protected = {n for n in engine_known | plugin_known if old in n} - defined

    # Old name too common to replace safely: part of engine names, or a plain word.
    plan.common_hits = sorted((n for n in engine_known if old in n and n not in defined), key=lambda n: (len(n), n))
    plan.common_name = bool(plan.common_hits) or old in RISKY_NAMES or len(old) < 3
    if new in RISKY_NAMES:
        plan.warnings.append(f"'{new}' is a very common word; a later rename of this project will be hard to do safely.")

    # The new name must not clash with engine or plugin modules and classes.
    known_lower = {n.lower(): n for n in engine_known | plugin_known if n not in defined}
    for mod in modules:
        nm = mod.replace(old, new)
        if nm != mod and nm.lower() in known_lower:
            plan.errors.append(f"The module '{mod}' would become '{nm}', which clashes with "
                               f"'{known_lower[nm.lower()]}' from the engine or a plugin. Pick another name.")
    for r in per_module.values():
        for kind in ("Class", "Struct"):
            for t in sorted(r.types[kind]):
                nt = t.replace(old, new)
                if nt != t and strip_prefix(kind, nt) in engine_known | plugin_known:
                    plan.errors.append(f"'{t}' would become '{nt}', which clashes with the engine's or a plugin's "
                                       f"own {strip_prefix(kind, nt)}. Pick another name.")

    for uplugin in src_root.glob("Plugins/**/*.uplugin"):
        if old in uplugin.stem and not should_skip(uplugin.relative_to(src_root)):
            plan.warnings.append(f"Plugin '{uplugin.stem}' contains the old name; it is NOT renamed and its names "
                                 "are left as they are.")

    # Project plugins that use the project's code: rename the project's identifiers in them too,
    # but nothing else (their own names stay).
    project_tokens: set[str] = set()
    if source_dir.is_dir():
        token_re = re.compile(r"[A-Za-z0-9_]*" + re.escape(old) + r"[A-Za-z0-9_]*")
        for f in source_dir.rglob("*"):
            if f.is_file() and f.suffix.lower() in TEXT_SUFFIXES and (res := read_text(f)):
                project_tokens |= set(token_re.findall(res[0]))
    plan.plugin_map = {t: t.replace(old, new) for t in project_tokens
                       if not is_protected_name(t, plan.protected) and IDENT_RE.match(t)}
    api_macros = {mod.upper() + "_API": mod.replace(old, new).upper() + "_API"
                  for mod in modules if mod.replace(old, new) != mod}
    plan.plugin_map.update(api_macros)
    # With a common old name, only the project's own names are renamed (see the COMMON NAME warning).
    plan.own_map = {t: t.replace(old, new) for t in defined if old in t and IDENT_RE.match(t)}
    plan.own_map.update(api_macros)
    for uplugin in sorted(src_root.glob("Plugins/**/*.uplugin")):
        pdir = uplugin.parent
        if should_skip(pdir.relative_to(src_root)):
            continue
        rules = " ".join(res[0] for bc in pdir.rglob("*.Build.cs") if (res := read_text(bc)))
        if any(re.search(r'"' + re.escape(mod) + r'"', rules) for mod in modules) or \
                plugin_mentions_modules(pdir, modules):
            plan.plugin_dirs.append(pdir.relative_to(src_root))

    # Redirects so existing assets still find the renamed module, types, members and values.
    for mod in modules:
        new_mod = mod.replace(old, new)
        r = per_module[mod]
        if new_mod != mod:
            plan.redirects.append(f'+PackageRedirects=(OldName="/Script/{mod}",NewName="/Script/{new_mod}")')
        for kind in ("Class", "Struct", "Enum"):
            for t in sorted(r.types[kind]):
                nt = t.replace(old, new)
                values = [(v, v.replace(old, new)) for v in r.enum_values.get(t, []) if old in v] if kind == "Enum" else []
                if nt == t and not values:
                    continue
                o, n = strip_prefix(kind, t), strip_prefix(kind, nt)
                line = f'+{kind}Redirects=(OldName="/Script/{mod}.{o}",NewName="/Script/{new_mod}.{n}"'
                if values:
                    line += ",ValueChanges=(" + ",".join(f'("{a}","{b}")' for a, b in values) + ")"
                plan.redirects.append(line + ")")
        # Renamed UPROPERTYs and UFUNCTIONs: without these, values saved in Blueprints and levels and
        # Blueprint function calls are silently dropped. Unreal looks members up under the owner's new
        # name, so that form is written; the old-owner form is added for safety.
        owner_kind = {name: kind for kind in ("Class", "Struct") for name in r.types[kind]}
        for kind, owner, member in r.members:
            if old not in member:
                continue
            k = owner_kind.get(owner, "Class")
            o_owner, n_owner = strip_prefix(k, owner), strip_prefix(k, owner.replace(old, new))
            target = f"/Script/{new_mod}.{n_owner}.{member.replace(old, new)}"
            for src in (f"/Script/{new_mod}.{n_owner}.{member}", f"/Script/{mod}.{o_owner}.{member}"):
                line = f'+{kind}Redirects=(OldName="{src}",NewName="{target}")'
                if line not in plan.redirects:
                    plan.redirects.append(line)
        for d in sorted(r.delegates):
            if old in d:
                o = strip_prefix("Struct", d) + "__DelegateSignature"
                n = strip_prefix("Struct", d.replace(old, new)) + "__DelegateSignature"
                plan.redirects.append(f'+FunctionRedirects=(OldName="/Script/{mod}.{o}",NewName="/Script/{new_mod}.{n}")')

    # Files to copy.
    for dirpath, dirnames, filenames in os.walk(src_root):
        dpath = Path(dirpath)
        rel_dir = dpath.relative_to(src_root)
        dirnames[:] = [d for d in dirnames if not should_skip(rel_dir / d)]
        for fname in filenames:
            rel = rel_dir / fname
            if should_skip(rel):
                continue
            new_rel = renamed_rel(rel, old, new)
            if new_rel != rel:
                plan.renamed.append((rel.as_posix(), new_rel.as_posix()))
            plan.copies.append((src_root / rel, dst_root / new_rel))

    # Windows' 260-character path limit: Unreal's Intermediate build files sit much deeper than
    # the source files, so leave plenty of room.
    longest = max((len(str(dst)) for _, dst in plan.copies), default=0)
    if longest >= 250:
        plan.errors.append(f"Some renamed files would have paths of {longest} characters; Windows allows 260 and "
                           "Unreal's build files need more. Move the project to a shorter folder or use a shorter name.")
    elif longest > 200:
        plan.warnings.append(f"The longest renamed path is {longest} characters. Unreal's build files add more on top, "
                             "close to Windows' 260 limit - a shorter folder is safer.")

    return plan


# --------------------------------------------------------------------------- execution

def add_redirects(ini: Path, lines: list[str], old: str, new: str) -> None:
    res = read_text(ini) if ini.exists() else ("", "utf-8")
    text, enc = res
    nl = "\r\n" if "\r\n" in text else "\n"
    block = nl.join([f"; Added by VFSUnrealProjectBuildTool ({old} -> {new})"] + lines) + nl

    m = re.search(r"^\[CoreRedirects\][^\S\r\n]*\r?\n", text, re.MULTILINE)
    if m:
        text = text[: m.end()] + block + text[m.end():]
    else:
        if text and not text.endswith(("\n", "\r")):
            text += nl
        text += f"{nl}[CoreRedirects]{nl}{block}"
    ini.parent.mkdir(parents=True, exist_ok=True)
    write_text(ini, text, enc)


def execute(plan: Plan, dry_run: bool) -> None:
    rewrites = compute_rewrites(plan)
    if dry_run:
        return

    for src, dst in plan.copies:
        dst.parent.mkdir(parents=True, exist_ok=True)
        if src in rewrites:
            write_text(dst, *rewrites[src])
            shutil.copystat(src, dst)
        else:
            shutil.copy2(src, dst)

    if plan.redirects and not dry_run:
        add_redirects(plan.dst_root / "Config" / "DefaultEngine.ini", plan.redirects, plan.old, plan.new)


def compute_rewrites(plan: Plan) -> dict[Path, tuple[str, str]]:
    """Rewritten text for every file that changes: {source file: (new text, encoding)}."""
    replacers = {
        "full": (make_replacer(plan.old, plan.new, only=plan.own_map) if plan.common_name else
                 make_replacer(plan.old, plan.new, plan.protected, seen=plan.protected_seen, defined=plan.defined)),
        "plugin": make_replacer(plan.old, plan.new, only=plan.plugin_map),
    }
    out = {}
    for src, dst in plan.copies:
        rel = dst.relative_to(plan.dst_root)
        mode = rewrite_mode(plan, rel)
        if mode is None:
            continue
        res = read_text(src)
        if res is None:
            continue
        text, enc = res
        new_text, count = replacers[mode](text)
        if rel.suffix.lower() == ".uproject" and len(rel.parts) == 1 and plan.engine_association:
            new_text, n = ASSOC_RE.subn(lambda m: m.group(1) + plan.engine_association + m.group(3), new_text)
            count += n
        if count:
            plan.rewrites[rel] = count
            out[src] = (new_text, enc)
    return out


def generated_paths(root: Path) -> list[Path]:
    """Build output that an in-place rename deletes (it holds old-name DLLs and is rebuilt).
    Solution files come first, so a locked cache folder can't stop them being removed."""
    paths = [p for p in root.iterdir() if p.is_file() and p.suffix.lower() in SKIP_ROOT_FILE_SUFFIXES]
    # .vs (Visual Studio) and .idea (Rider) are per-user caches keyed to the old solution name.
    paths += [root / d for d in ("Binaries", "Intermediate", ".vs", ".idea") if (root / d).exists()]
    paths += [p for p in root.glob("Plugins/**/Intermediate") if p.is_dir()]
    return paths


def delete_paths(paths: list[Path]) -> list[str]:
    """Delete files and folders, clearing read-only flags; carry on past anything locked.
    Returns a message per path that couldn't be (fully) deleted."""
    import stat

    def clear_readonly(func, path, _exc):
        try:
            os.chmod(path, stat.S_IWRITE)
            func(path)
        except OSError:
            pass  # still locked: reported below if anything is left

    problems = []
    for p in paths:
        try:
            if p.is_dir():
                shutil.rmtree(p, onerror=clear_readonly)
            elif p.exists():
                try:
                    p.unlink()
                except PermissionError:
                    os.chmod(p, stat.S_IWRITE)
                    p.unlink()
        except OSError as e:
            problems.append(f"{p.name}: {e.strerror or e}")
            continue
        if p.exists():
            problems.append(f"{p.name}: some files are in use (close Visual Studio / Rider and delete it by hand)")
    return problems


def execute_in_place(plan: Plan) -> None:
    root = plan.src_root
    rewrites = compute_rewrites(plan)

    # 1. Write every changed/renamed file at its new path.
    moved = []
    for src, dst in plan.copies:
        if src in rewrites:
            dst.parent.mkdir(parents=True, exist_ok=True)
            write_text(dst, *rewrites[src])
        elif src != dst:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
        if src != dst:
            moved.append(src)

    # 2. Only then remove the old copies, so a failure part-way never loses a file.
    for src in moved:
        src.unlink()
    source = root / "Source"
    if source.is_dir():
        for d in sorted((p for p in source.rglob("*") if p.is_dir()), key=lambda p: len(p.parts), reverse=True):
            if not any(d.iterdir()):
                d.rmdir()

    if plan.redirects:
        add_redirects(root / "Config" / "DefaultEngine.ini", plan.redirects, plan.old, plan.new)

    for problem in delete_paths(generated_paths(root)):
        print(f"\nwarning: couldn't delete {problem}")


# --------------------------------------------------------------------------- project file generation

def read_association(uproject: Path) -> str:
    text, _ = read_text(uproject)
    return json.loads(text).get("EngineAssociation", "")


def installed_launcher_engines() -> list[str]:
    """Launcher engine versions registered on this PC, e.g. ['5.7', '5.8']."""
    if sys.platform != "win32":
        return []
    import winreg
    versions = []
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\EpicGames\Unreal Engine") as k:
            i = 0
            while True:
                try:
                    versions.append(winreg.EnumKey(k, i))
                except OSError:
                    break
                i += 1
    except OSError:
        pass
    return sorted(versions, key=lambda v: [int(p) if p.isdigit() else p for p in v.split(".")])


def engine_version(assoc: str) -> tuple[int, int] | None:
    """(major, minor) for an EngineAssociation: '5.8', or a registered source build's Build.version."""
    m = re.match(r"^(\d+)\.(\d+)", assoc or "")
    if m:
        return int(m.group(1)), int(m.group(2))
    d = engine_dir_for_association(assoc)
    try:
        data = json.loads((d / "Engine" / "Build" / "Build.version").read_text(encoding="utf-8")) if d else None
        return (int(data["MajorVersion"]), int(data["MinorVersion"])) if data else None
    except (OSError, ValueError, KeyError):
        return None


def find_engine_dir(uproject: Path, override: str | None) -> Path | None:
    if override:
        return Path(override)
    return engine_dir_for_association(read_association(uproject))


def engine_dir_for_association(assoc: str) -> Path | None:
    """Engine folder for an EngineAssociation value, or None if this PC doesn't know it."""
    if not assoc:
        return None
    if sys.platform == "win32":
        import winreg
        # Launcher installs: HKLM\SOFTWARE\EpicGames\Unreal Engine\<version>\InstalledDirectory
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, rf"SOFTWARE\EpicGames\Unreal Engine\{assoc}") as k:
                return Path(winreg.QueryValueEx(k, "InstalledDirectory")[0])
        except OSError:
            pass
        # Source builds: HKCU\SOFTWARE\Epic Games\Unreal Engine\Builds\{GUID} = path
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"SOFTWARE\Epic Games\Unreal Engine\Builds") as k:
                return Path(winreg.QueryValueEx(k, assoc)[0])
        except OSError:
            pass
    return None


# Visual Studio major version -> the year Unreal uses in its option names.
_VS_YEARS = {"15": "2017", "16": "2019", "17": "2022", "18": "2026"}


@dataclass
class VisualStudio:
    year: str            # "2026"
    path: Path           # installation folder
    msvc: str | None     # newest MSVC toolset in it, e.g. "14.51.36231"

    def describe(self) -> str:
        return f"Visual Studio {self.year} ({self.path}" + (f", MSVC {self.msvc})" if self.msvc else ")")


def find_visual_studio(year: str, path: str | None = None) -> VisualStudio | None:
    """The Visual Studio install for `year` (or at `path`), with its newest MSVC toolset."""
    vswhere = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    installs = []
    if vswhere.exists():
        try:
            out = subprocess.run([str(vswhere), "-all", "-prerelease", "-products", "*", "-format", "json"],
                                 capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
            installs = json.loads(out or "[]")
        except (OSError, json.JSONDecodeError):
            installs = []
    for inst in sorted(installs, key=lambda i: i.get("installationVersion", ""), reverse=True):
        ipath = Path(inst.get("installationPath", ""))
        iyear = _VS_YEARS.get(inst.get("installationVersion", "").split(".")[0])
        if (path and ipath == Path(path)) or (not path and iyear == year):
            msvc_dir = ipath / "VC" / "Tools" / "MSVC"
            versions = sorted((d.name for d in msvc_dir.iterdir() if d.is_dir()),
                              key=lambda v: [int(p) for p in v.split(".") if p.isdigit()]) if msvc_dir.is_dir() else []
            return VisualStudio(iyear or year, ipath, versions[-1] if versions else None)
    return None


def engine_supports_vs(engine_dir: Path, year: str) -> bool:
    """Whether this engine's UnrealBuildTool knows VisualStudio<year>."""
    ubt = engine_dir / "Engine" / "Binaries" / "DotNET" / "UnrealBuildTool" / "UnrealBuildTool.dll"
    try:
        data = ubt.read_bytes()
    except OSError:
        return True  # can't tell; let UBT decide
    name = f"VisualStudio{year}"
    return name.encode("ascii") in data or name.encode("utf-16-le") in data


def generate_project_files(engine_dir: Path, uproject: Path, vs: VisualStudio | None = None) -> bool:
    if sys.platform == "win32":
        script = engine_dir / "Engine" / "Build" / "BatchFiles" / "Build.bat"
        cmd = [str(script), "-projectfiles", f"-project={uproject}", "-game", "-progress"]
        if vs:
            cmd += [f"-ProjectFileFormat=VisualStudio{vs.year}", f"-{vs.year}"]
    else:
        script = engine_dir / "Engine" / "Build" / "BatchFiles" / "RunUBT.sh"
        cmd = [str(script), "-projectfiles", f"-project={uproject}", "-game", "-progress"]
    if not script.exists():
        print(f"  could not find {script}")
        return False
    print("  " + " ".join(f'"{c}"' if " " in c else c for c in cmd))
    return subprocess.call(cmd) == 0


# --------------------------------------------------------------------------- after the rename: build, resave, clean up

def editor_target_name(root: Path) -> str | None:
    targets = sorted((root / "Source").glob("*Editor.Target.cs"))
    return targets[0].name[: -len(".Target.cs")] if targets else None


def build_project(engine_dir: Path, uproject: Path, vs: VisualStudio | None = None, rebuild: bool = False) -> bool:
    target = editor_target_name(uproject.parent)
    if not target:
        print("  no *Editor.Target.cs found in Source/")
        return False
    script = engine_dir / "Engine" / "Build" / "BatchFiles" / ("Build.bat" if sys.platform == "win32" else "RunUBT.sh")
    platform = {"win32": "Win64", "darwin": "Mac"}.get(sys.platform, "Linux")
    cmd = [str(script), target, platform, "Development", f"-Project={uproject}", "-WaitMutex", "-NoHotReload"]
    if rebuild:
        cmd.append("-Rebuild")  # what Engine/Build/BatchFiles/Rebuild.bat adds: clean, then build
    if vs and sys.platform == "win32":
        # -Compiler alone isn't enough: UBT still prefers the toolset it ranks highest (which may belong to
        # another Visual Studio), so also pin this install's own MSVC version.
        cmd.append(f"-Compiler=VisualStudio{vs.year}")
        if vs.msvc:
            cmd.append(f"-CompilerVersion={vs.msvc}")
    print("  " + " ".join(f'"{c}"' if " " in c else c for c in cmd))
    return subprocess.call(cmd) == 0


def content_mounts(root: Path) -> list[tuple[Path, str]]:
    """(Content folder, mount point) for the project and each project plugin."""
    mounts = [(root / "Content", "/Game")]
    for uplugin in root.glob("Plugins/**/*.uplugin"):
        if (uplugin.parent / "Content").is_dir():
            mounts.append((uplugin.parent / "Content", "/" + uplugin.stem))
    return mounts


def redirected_packages(ini: Path) -> list[str]:
    """Old script packages (/Script/X) that [CoreRedirects] PackageRedirects send elsewhere."""
    if not ini.exists():
        return []
    text = read_text(ini)[0]
    return sorted(set(re.findall(r'^\s*\+PackageRedirects=\(OldName="(/Script/[A-Za-z0-9_]+)"', text, re.MULTILINE)))


def assets_referencing(root: Path, packages: list[str]) -> dict[str, set[str]]:
    """{asset package path: {old packages it mentions}} by scanning .uasset/.umap headers for the names."""
    needles = {}
    for p in packages:
        # Match "/Script/X" but not "/Script/X2": the name ends with a NUL, '.', ':' or quote.
        needles[p] = [p.encode("ascii") + end for end in (b"\x00", b".", b":", b"'", b'"')]
        needles[p] += [p.encode("utf-16-le") + end.encode("utf-16-le") for end in ("\x00", ".", ":")]
    found: dict[str, set[str]] = {}
    for content, mount in content_mounts(root):
        for f in list(content.rglob("*.uasset")) + list(content.rglob("*.umap")):
            try:
                data = f.read_bytes()
            except OSError:
                continue
            hits = {p for p, ns in needles.items() if any(n in data for n in ns)}
            if hits:
                rel = f.relative_to(content).with_suffix("").as_posix()
                found[f"{mount}/{rel}"] = hits
    return found


# Runs inside the Unreal Editor (headless). Reads its job from the file named in UPR_JOB.
_EDITOR_SCRIPT = r'''
import ctypes, json, os, unreal
job = json.load(open(os.environ["UPR_JOB"], encoding="utf-8"))

_get_module = ctypes.windll.kernel32.GetModuleHandleW
_get_module.argtypes, _get_module.restype = [ctypes.c_wchar_p], ctypes.c_void_p

def module_loaded(m):
    # Modules with UObjects get a /Script package; modules without one are found by their loaded DLL.
    if unreal.find_object(None, "/Script/" + m) is not None:
        return True
    return any(_get_module("UnrealEditor-%s%s.dll" % (m, s)) for s in ("", "-Win64-DebugGame"))

# Headless editors don't finish scanning plugin content on their own; wait for a full scan.
unreal.AssetRegistryHelpers.get_asset_registry().search_all_assets(True)
if job["mode"] == "plugins":
    lib = unreal.PluginBlueprintLibrary
    enabled = set(lib.get_enabled_plugin_names())
    for name, modules in job["plugins"].items():
        unreal.log("UPR_PLUGIN|%s|%s|%s" % (name, name in enabled, lib.is_plugin_mounted(name)))
        for m in modules:
            unreal.log("UPR_MODULE|%s|%s|%s" % (name, m, module_loaded(m)))
for pkg in job["packages"]:
    unreal.log("UPR_BEGIN|" + pkg)
    ok = False
    try:
        asset = unreal.EditorAssetLibrary.load_asset(pkg)
        if asset is not None and job["mode"] == "save":
            ok = unreal.EditorAssetLibrary.save_asset(pkg, only_if_is_dirty=False)
        else:
            ok = asset is not None
    except Exception as e:
        unreal.log_warning("UPR_EXCEPTION|" + pkg + "|" + repr(e))
    unreal.log("UPR_END|" + pkg + "|" + ("ok" if ok else "failed"))
'''

_PROBLEM_RE = re.compile(r":\s*(Warning|Error):")
# Warnings about the whole project, logged once by whichever asset happens to load first; they say
# nothing about that asset, so they don't count against it.
_PROJECT_WIDE_RE = re.compile(r"No GameplayCueNotifyPaths were specified")


def run_in_editor(engine_dir: Path, uproject: Path, mode: str, packages: list[str],
                  problems: dict[str, list[str]] | None = None) -> dict[str, str]:
    """Load (mode 'check') or load+save (mode 'save') packages in a headless editor.
    Returns {package: 'ok' | 'failed' | 'warnings'} - 'warnings' if anything was logged while it loaded.
    Every problem line logged for a package goes into `problems` (nothing is printed)."""
    lines = run_editor_job(engine_dir, uproject, {"mode": mode, "packages": packages})
    return parse_package_results(lines, packages, quiet=True, all_problems=problems)


# Things to look at in Unreal Editor afterwards: (asset or file, why). Collected while the steps run and
# printed together at the very end of the log (the app shows that section in yellow).
EDITOR_CHECKS: list[tuple[str, str]] = []
EDITOR_CHECKS_HEADER = "CHECK IN UNREAL EDITOR - confirm these to know everything works:"


def note_check(what: str, why: str) -> None:
    """Remember something to check in the editor (the first reason per item wins)."""
    if all(w != what for w, _ in EDITOR_CHECKS):
        EDITOR_CHECKS.append((what, why))


def first_problem(package: str, problems: dict[str, list[str]], none: str = "no reason was logged") -> str:
    lines = problems.get(package, [])
    return readable_problem(lines[0], package)[:200] if lines else none


def default_maps(root: Path) -> list[str]:
    """The project's start-up maps (GameDefaultMap / EditorStartupMap), except engine ones."""
    ini = root / "Config" / "DefaultEngine.ini"
    found: list[str] = []
    if ini.exists():
        for key, value in re.findall(r"^\s*(GameDefaultMap|EditorStartupMap)\s*=\s*(\S+)", read_text(ini)[0], re.M):
            path = value.strip("\"'").split(".")[0]
            if path.startswith("/Game/") and path not in found and is_map(root, path):
                found.append(path)
    return found


def is_map(root: Path, package: str) -> bool:
    return package.startswith("/Game/") and (root / "Content" / (package[len("/Game/"):] + ".umap")).exists()


BUILD_CHECK_HEADER = "CONFIRM WORKING BUILD:"


def print_editor_checks(items: list[tuple[str, str]], header: str = EDITOR_CHECKS_HEADER) -> None:
    if not items:
        return
    print("\n" + "=" * 78)
    print(header)
    for i, (what, why) in enumerate(items, 1):
        print(f"  {i}. {what}")
        print(f"     {why}")
    print("=" * 78)


def readable_problem(line: str, package: str) -> str:
    """An editor log line, shortened: no timestamp; an "[AssetLog] <path>:" prefix becomes "(in <file>)"
    when it's about another asset than `package` (warnings often come from assets it loads)."""
    msg = re.sub(r"^\[[^\]]*\]\[\s*\d+\]", "", line.strip())
    m = re.search(r"\[AssetLog\] (.*?\.u(?:asset|map)): ", msg)
    if m:
        file = Path(m.group(1)).name
        msg = msg.replace(m.group(0), "")
        if Path(file).stem != package.rsplit("/", 1)[-1]:
            msg += f" (in {file})"
    return msg


def print_problems(package: str, problems: dict[str, list[str]], limit: int = 2) -> None:
    """Up to `limit` distinct reasons for a package, repeats counted, under its SKIPPED/FAILED line."""
    counts: dict[str, int] = {}
    for line in problems.get(package, []):
        text = readable_problem(line, package)
        counts[text] = counts.get(text, 0) + 1
    for text, n in list(counts.items())[:limit]:
        print(f"      why: {text[:220]}{' (x%d)' % n if n > 1 else ''}")
    if len(counts) > limit:
        print(f"      ...and {len(counts) - limit} other message(s); open the asset to see them all")


def run_editor_job(engine_dir: Path, uproject: Path, job: dict) -> list[str]:
    """Run _EDITOR_SCRIPT with `job` in a headless editor and return its log lines."""
    import tempfile
    exe = engine_dir / "Engine" / "Binaries" / ("Win64/UnrealEditor-Cmd.exe" if sys.platform == "win32" else
                                                 ("Mac/UnrealEditor-Cmd" if sys.platform == "darwin" else "Linux/UnrealEditor-Cmd"))
    with tempfile.TemporaryDirectory(prefix="upr_") as tmp:
        script = Path(tmp) / "upr_editor_job.py"
        script.write_text(_EDITOR_SCRIPT, encoding="utf-8")
        job_file = Path(tmp) / "job.json"
        job_file.write_text(json.dumps(job), encoding="utf-8")
        env = dict(os.environ, UPR_JOB=str(job_file), UE_PYTHONPATH=tmp)
        # The script is found through UE_PYTHONPATH, so no path (or its spaces) goes on the command line.
        cmd = [str(exe), str(uproject), "-run=pythonscript", "-script=upr_editor_job.py",
               "-EnablePlugins=PythonScriptPlugin", "-unattended", "-nosplash", "-nop4", "-nullrhi", "-stdout",
               "-AllowStdOutLogVerbosity"]  # without this, Log-level lines (our markers) never reach stdout
        proc = subprocess.run(cmd, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return proc.stdout.splitlines()


def parse_package_results(lines: list[str], packages: list[str], quiet: bool = False,
                          messages: dict[str, str] | None = None,
                          all_problems: dict[str, list[str]] | None = None) -> dict[str, str]:
    """{package: 'ok' | 'warnings' | 'failed'}; the first problem line per package goes into `messages`,
    every problem line into `all_problems`."""
    results: dict[str, str] = {}
    current, problems = None, False
    for line in lines:
        if "UPR_BEGIN|" in line:
            current, problems = line.split("UPR_BEGIN|", 1)[1].strip(), False
        elif "UPR_END|" in line and current:
            status = line.split("UPR_END|", 1)[1].strip().rsplit("|", 1)[-1]
            results[current] = "warnings" if problems and status == "ok" else status
            current = None
        elif current and _PROBLEM_RE.search(line) and not _PROJECT_WIDE_RE.search(line):
            problems = True
            if all_problems is not None:
                all_problems.setdefault(current, []).append(line)
            if messages is not None and current not in messages:
                # Keep the readable part: drop the timestamp and the "[AssetLog] <file path>:" prefix.
                msg = re.sub(r"^\[[^\]]*\]\[\s*\d+\]", "", line.strip())
                messages[current] = re.sub(r"\[AssetLog\] .*?\.u(?:asset|map): ", "", msg)
            if not quiet:
                print(f"    {current}: {line.strip()[:200]}")
    for p in packages:
        results.setdefault(p, "failed")
    return results


def resave_assets(engine_dir: Path, uproject: Path, old_packages: list[str]) -> tuple[list[str], list[str]]:
    """Resave every asset that still stores an old module name, skipping any that load with problems.
    Returns (saved, skipped)."""
    refs = assets_referencing(uproject.parent, old_packages)
    if not refs:
        print("  no assets reference the old names")
        return [], []
    pkgs = sorted(refs)
    print(f"  {len(pkgs)} asset(s) store an old name; checking they load cleanly first...")
    problems: dict[str, list[str]] = {}
    check = run_in_editor(engine_dir, uproject, "check", pkgs, problems)
    safe = [p for p in pkgs if check[p] == "ok"]
    skipped = [p for p in pkgs if check[p] != "ok"]
    for p in skipped:
        print(f"  SKIPPED {p} ({'loaded with warnings' if check[p] == 'warnings' else 'failed to load'}) - "
              "saving it could lose data; open it in the editor and fix it by hand")
        print_problems(p, problems)
        note_check(p, "Not resaved (it " + ("loaded with a warning" if check[p] == "warnings" else "failed to load") +
                   f": {first_problem(p, problems)}). It still works through the redirects. Open it, fix that, "
                   "and save it.")
    saved = []
    if safe:
        print(f"  resaving {len(safe)} asset(s)...")
        save_problems: dict[str, list[str]] = {}
        result = run_in_editor(engine_dir, uproject, "save", safe, save_problems)
        for p in safe:
            if result[p] == "ok":
                saved.append(p)
                print(f"  saved   {p}")
            elif result[p] == "warnings":
                # It loaded cleanly in the check and the save succeeded; something was only logged meanwhile.
                saved.append(p)
                print(f"  saved   {p} (with warnings - worth a look, but it was saved)")
                print_problems(p, save_problems)
                note_check(p, f"Saved, but logged: {first_problem(p, save_problems)}. Open it and check it looks right.")
            else:
                skipped.append(p)
                print(f"  FAILED  {p} (the save didn't succeed)")
                print_problems(p, save_problems)
                note_check(p, f"Couldn't be resaved ({first_problem(p, save_problems, 'the save did not succeed')}). "
                              "Open it and save it by hand.")
        # Resaved maps: worth opening and playing, since they now point at the renamed C++ classes.
        maps = [p for p in saved if is_map(uproject.parent, p)]
        for p in maps[:6]:
            note_check(p, "Resaved with the new C++ names. Open it and press Play to check it still works.")
        if len(maps) > 6:
            note_check(f"...and {len(maps) - 6} more resaved map(s)", "Open any you rely on and press Play.")
    return saved, skipped


def remove_unused_redirects(ini: Path, root: Path) -> list[str]:
    """Drop [CoreRedirects] entries for old modules that no asset references any more."""
    old = redirected_packages(ini)
    still_used = set().union(*assets_referencing(root, old).values()) if old else set()
    removable = [p for p in old if p not in still_used]
    if not removable:
        return []
    text, enc = read_text(ini)
    nl = "\r\n" if "\r\n" in text else "\n"
    out, in_section = [], False
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("["):
            in_section = stripped == "[CoreRedirects]"
        if in_section:
            # Property/Function redirects are always kept: Blueprint nodes may still use the old names.
            m = re.match(r'\s*\+(?:Package|Class|Struct|Enum)Redirects=\(OldName="(/Script/[A-Za-z0-9_]+)[".]', line)
            if m and m.group(1) in removable:
                continue
        out.append(line)
    # Drop our "; Added by" comments that no longer head any redirect, and an empty section.
    cleaned = []
    for i, line in enumerate(out):
        # The old wording too: projects renamed before the script was renamed have it.
        if line.startswith(("; Added by VFSUnrealProjectBuildTool", "; Added by rename_unreal_project.py")):
            nxt = out[i + 1].strip() if i + 1 < len(out) else ""
            if not re.match(r"[+\-.!]?\w+Redirects=", nxt):
                continue
        cleaned.append(line)
    final = []
    for i, line in enumerate(cleaned):
        if line.strip() == "[CoreRedirects]":
            rest = [l.strip() for l in cleaned[i + 1:]]
            body = []
            for l in rest:
                if l.startswith("["):
                    break
                body.append(l)
            if not any(body):
                continue
        final.append(line)
    write_text(ini, nl.join(final) + (nl if text.endswith(("\n", "\r")) else ""), enc)
    return removable


# Module types the editor loads (in a commandlet), and so can be checked there.
_EDITOR_MODULE_TYPES = {"Runtime", "RuntimeAndProgram", "Editor", "UncookedOnly", "Developer", "DeveloperTool"}
_ASSETS_PER_PLUGIN = 200


def _host_platform() -> str:
    return {"win32": "Win64", "darwin": "Mac"}.get(sys.platform, "Linux")


def plugins_in_use(root: Path, uproject: Path, engine_dir: Path) -> dict[str, tuple[Path | None, list[str], bool]]:
    """{plugin: (.uplugin path or None, modules to check, is project-local)} for every enabled plugin."""
    desc = json.loads(read_text(uproject)[0])
    listed = {p["Name"]: p.get("Enabled", True) for p in desc.get("Plugins", []) if p.get("Name")}
    local = {p.stem: p for p in root.glob("Plugins/**/*.uplugin")}

    names = [n for n, on in listed.items() if on]
    for name, path in local.items():
        try:
            by_default = json.loads(read_text(path)[0]).get("EnabledByDefault", True)
        except (json.JSONDecodeError, OSError):
            by_default = True
        if listed.get(name, by_default) and name not in names:
            names.append(name)

    engine_uplugins: dict[str, Path] = {}
    if any(n not in local for n in names):
        for dirpath, _, files in os.walk(engine_dir / "Engine" / "Plugins"):
            for f in files:
                if f.endswith(".uplugin"):
                    engine_uplugins.setdefault(f[: -len(".uplugin")], Path(dirpath) / f)

    plat = _host_platform()
    result = {}
    for name in names:
        path = local.get(name) or engine_uplugins.get(name)
        modules = []
        if path:
            try:
                for m in json.loads(read_text(path)[0]).get("Modules", []):
                    allow, deny = m.get("PlatformAllowList") or m.get("WhitelistPlatforms"), m.get("PlatformDenyList") or []
                    # LoadingPhase "None" modules are never loaded automatically (the plugin loads them itself
                    # when needed, e.g. Streamline's D3D RHI modules), so they aren't expected to be loaded.
                    if m.get("Type") in _EDITOR_MODULE_TYPES and (not allow or plat in allow) and plat not in deny \
                            and m.get("LoadingPhase") != "None":
                        modules.append(m["Name"])
            except (json.JSONDecodeError, OSError, KeyError):
                pass
        result[name] = (path, modules, name in local)
    return result


def plugin_has_content(uplugin: Path | None) -> bool:
    """True when Unreal mounts the plugin's content: only when its .uplugin says CanContainContent: true.
    (A Content folder alone doesn't count - GameplayAbilities has one but sets CanContainContent false.)"""
    if uplugin is None:
        return False
    try:
        return bool(json.loads(read_text(uplugin)[0]).get("CanContainContent"))
    except (json.JSONDecodeError, OSError, TypeError):
        return False


def verify_plugins(engine_dir: Path, uproject: Path) -> bool:
    """Check every enabled plugin is enabled, mounted and its modules load in the editor, and that the
    project's assets using each plugin load without warnings. Prints a report; returns True if all pass."""
    root = uproject.parent
    plugins = plugins_in_use(root, uproject, engine_dir)
    if not plugins:
        print("  the project enables no plugins")
        return True

    # Assets that use each plugin's classes.
    module_owner = {f"/Script/{m}": name for name, (_, mods, _) in plugins.items() for m in mods}
    refs = assets_referencing(root, sorted(module_owner)) if module_owner else {}
    per_plugin: dict[str, list[str]] = {n: [] for n in plugins}
    for pkg, used in sorted(refs.items()):
        for owner in {module_owner[u] for u in used}:
            per_plugin[owner].append(pkg)
    totals = {n: len(p) for n, p in per_plugin.items()}
    packages = sorted({p for pkgs in per_plugin.values() for p in pkgs[:_ASSETS_PER_PLUGIN]})

    print(f"  checking {len(plugins)} plugin(s) and {len(packages)} asset(s) that use them...")
    lines = run_editor_job(engine_dir, uproject, {
        "mode": "plugins", "packages": packages,
        "plugins": {n: mods for n, (_, mods, _) in plugins.items()}})
    load_messages: dict[str, str] = {}
    loads = parse_package_results(lines, packages, quiet=True, messages=load_messages)

    state = {n: {"enabled": False, "mounted": False, "modules": {}} for n in plugins}
    for line in lines:
        if "UPR_PLUGIN|" in line:
            n, en, mo = line.split("UPR_PLUGIN|", 1)[1].strip().split("|")
            if n in state:
                state[n]["enabled"], state[n]["mounted"] = en == "True", mo == "True"
        elif "UPR_MODULE|" in line:
            n, m, found = line.split("UPR_MODULE|", 1)[1].strip().split("|")
            if n in state:
                state[n]["modules"][m] = found == "True"
    editor_ran = any("UPR_PLUGIN|" in l for l in lines)
    if not editor_ran:
        print("  FAILED: the editor didn't start - a plugin (or the project) may have failed to load:")
        for l in lines:
            if _PROBLEM_RE.search(l):
                print(f"    {l.strip()[:220]}")
        return False

    all_ok = True
    width = max(len(n) for n in plugins)
    for name, (path, mods, is_local) in plugins.items():
        s = state[name]
        problems = []
        if path is None:
            problems.append("its .uplugin wasn't found")
        if not s["enabled"]:
            problems.append("not enabled")
        # Only plugins that can hold content get a mount point; code-only plugins never do.
        if not s["mounted"] and plugin_has_content(path):
            problems.append("not mounted")
        missing = [m for m in mods if not s["modules"].get(m)]
        if missing:
            problems.append("modules not loaded: " + ", ".join(missing))
        checked = per_plugin[name][:_ASSETS_PER_PLUGIN]
        failed_assets = [p for p in checked if loads.get(p) not in ("ok", "warnings")]
        warned_assets = [p for p in checked if loads.get(p) == "warnings"]
        if failed_assets:
            problems.append(f"{len(failed_assets)} asset(s) failed to load: " + ", ".join(failed_assets[:5])
                            + (" ..." if len(failed_assets) > 5 else ""))
        where = "project" if is_local else "engine"
        loaded = len(checked) - len(failed_assets)
        assets = f"{loaded}/{len(checked)} assets load" if checked else "no project assets use it"
        if totals[name] > len(checked):
            assets += f" (checked {len(checked)} of {totals[name]})"
        if problems:
            all_ok = False
            print(f"  FAIL  {name:<{width}}  ({where})  " + "; ".join(problems))
        elif warned_assets:
            print(f"  WARN  {name:<{width}}  ({where})  {len(mods)} module(s) loaded, {assets}, "
                  f"{len(warned_assets)} with warnings:")
            for p in warned_assets[:5]:
                print(f"          {p}: {load_messages.get(p, '')[:180]}")
                note_check(p, f"Loads with a warning (uses the {name} plugin): "
                              f"{readable_problem(load_messages.get(p, ''), p)[:180]}. Open it and check it.")
        else:
            print(f"  OK    {name:<{width}}  ({where})  {len(mods)} module(s) loaded, {assets}")
    return all_ok


def cook_and_package(engine_dir: Path, uproject: Path, config: str | None, clean: bool,
                     archive_dir: Path | None, rebuild: bool = False) -> bool:
    """Cook the project's content for Windows (config None) or cook and package a Development/Shipping build
    into archive_dir, with UnrealAutomationTool's BuildCookRun. The editor isn't opened. clean=True deletes
    old cooked data and cooks everything; otherwise only what changed is recooked."""
    script = engine_dir / "Engine" / "Build" / "BatchFiles" / "RunUAT.bat"
    if not script.exists():
        print(f"  could not find {script}")
        return False
    if clean:
        cooked = uproject.parent / "Saved" / "Cooked"
        if cooked.is_dir():
            print(f"  deleting old cooked content ({cooked})")
            shutil.rmtree(cooked, ignore_errors=True)
    cmd = [str(script), "BuildCookRun", f"-project={uproject}", "-noP4", "-platform=Win64", "-cook",
           "-nocompileeditor", "-unattended", "-utf8output"]
    if not clean:
        cmd.append("-cookincremental")
    if config:
        cmd += [f"-clientconfig={config}", "-build", "-stage", "-pak", "-archive", f"-archivedirectory={archive_dir}"]
        if rebuild:
            cmd.append("-clean")  # clean the game build first, like the editor's rebuild
    print("  " + " ".join(f'"{c}"' if " " in c else c for c in cmd))
    return subprocess.call(cmd) == 0


def open_in_editor(engine_dir: Path, uproject: Path) -> None:
    exe = engine_dir / "Engine" / "Binaries" / ("Win64/UnrealEditor.exe" if sys.platform == "win32" else
                                                ("Mac/UnrealEditor" if sys.platform == "darwin" else "Linux/UnrealEditor"))
    flags = getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    subprocess.Popen([str(exe), str(uproject)], creationflags=flags, close_fds=True)


def run_action(args, uproject: Path) -> int:
    """--action build/rebuild/package: no rename, just the follow-up steps on the project as it is.
    Returns 0, 1 if refused before anything ran, or 2 if a step failed."""
    names = {"build": "Building", "rebuild": "Rebuilding", "package": "Building and packaging",
             "regenerate": "Regenerating Visual Studio project files for",
             "upgrade": "Upgrading the engine version of"}
    print(f"{names[args.action]} '{uproject.stem}'")
    print(f"  project: {uproject}")
    old_assoc = read_association(uproject)
    if args.action == "upgrade":
        if not args.engine_association:
            print("\nerror: --action upgrade needs --engine-association (the engine to upgrade to)")
            print("\nNothing was changed.")
            return 1
        if args.engine_association == old_assoc:
            print(f"\nerror: the project already uses {old_assoc}")
            print("\nNothing was changed.")
            return 1
        if sys.platform == "win32" and engine_dir_for_association(args.engine_association) is None:
            print(f"\nerror: engine '{args.engine_association}' isn't installed on this PC")
            print("\nNothing was changed.")
            return 1
    if args.engine_association and args.engine_association != old_assoc:
        was, to = engine_version(old_assoc), engine_version(args.engine_association)
        if was and to and to < was:
            print(f"\nerror: can't switch the project from UE {was[0]}.{was[1]} to the older UE {to[0]}.{to[1]}. "
                  "Assets saved by a newer engine can't be opened by an older one. Pick the same or a newer engine.")
            print("\nNothing was changed.")
            return 1
        text, enc = read_text(uproject)
        new_text, n = ASSOC_RE.subn(lambda m: m.group(1) + args.engine_association + m.group(3), text, count=1)
        if n == 0:  # no EngineAssociation line yet: add one after the opening brace
            new_text = text.replace("{", '{\n\t"EngineAssociation": "' + args.engine_association + '",', 1)
        write_text(uproject, new_text, enc)
        print(f"  engine: {old_assoc or '(none)'} -> {args.engine_association}")
    else:
        print(f"  engine: {old_assoc or '(none)'}")
    if args.action == "package" and not (args.package or args.cook):
        print("  (no cook or package step chosen: building only)")

    problems: list[str] = []
    if args.action in ("regenerate", "upgrade"):
        # A clean slate for Visual Studio: generated folders and solution files next to the .uproject.
        root = uproject.parent
        targets = [p for p in root.iterdir() if p.is_file()
                   and (p.name.lower() == ".vsconfig" or p.suffix.lower() in (".sln", ".slnx"))]
        targets += [root / d for d in (".vs", "Binaries", "DerivedDataCache", "Intermediate") if (root / d).exists()]
        print("\nDeleting generated files...")
        for p in targets:
            print(f"  {p.name}{'/' if p.is_dir() else ''}")
        if not targets:
            print("  nothing to delete")
        problems = delete_paths(targets)
        for p in problems:
            print(f"  warning: couldn't delete {p}")

    code = after_rename(args, uproject)
    # Anything left behind (in use) counts as a step that needs attention.
    return 2 if problems and code == 0 else code


# --------------------------------------------------------------------------- main

def main() -> int:
    ap = argparse.ArgumentParser(
        description="Make a renamed copy of a C++ Unreal Engine project.",
        epilog="Run with --dry-run first. The original project is never modified.",
    )
    ap.add_argument("project", help="path to the .uproject file or the folder that contains it")
    ap.add_argument("new_name", nargs="?",
                    help="new project name (letters, digits, underscores; starts with a letter). Not used with --action")
    ap.add_argument("--action", choices=["build", "rebuild", "package", "regenerate", "upgrade"],
                    help="don't rename: build, rebuild (clean + build), build then cook/package, or regenerate "
                         "(delete .vs, Binaries, DerivedDataCache, Intermediate, .vsconfig, .sln/.slnx; then use "
                         "--generate-project-files --build) the project as it is. "
                         "Combine with --generate-project-files, --package/--cook, --open-editor, --engine-association")
    ap.add_argument("--launch", action="store_true", help="with --package, start the packaged game when it's done")
    ap.add_argument("--rebuild", action="store_true",
                    help="with --action build/package: clean, then build everything (like Visual Studio's Rebuild)")
    ap.add_argument("-o", "--output", help="folder for the renamed copy (default: <parent>/<NewName>)")
    ap.add_argument("-n", "--dry-run", action="store_true", help="show what would change without writing anything")
    ap.add_argument("--in-place", action="store_true",
                    help="rename the project where it is instead of making a copy. Deletes Binaries/, "
                         "Intermediate/ and the .sln (they are rebuilt), and renames the project folder "
                         "if it has the old name. Commit or back up first.")
    ap.add_argument("--keep-folder-name", action="store_true",
                    help="with --in-place, don't rename the project folder")
    ap.add_argument("--generate-project-files", action="store_true",
                    help="run UnrealBuildTool to create the new .sln afterwards")
    ap.add_argument("--build", action="store_true", help="compile the renamed project's editor target afterwards")
    ap.add_argument("--visual-studio", metavar="YEAR",
                    help="Visual Studio to generate project files for and compile with, e.g. 2026 "
                         "(default: let Unreal choose)")
    ap.add_argument("--visual-studio-path", help="a specific Visual Studio install folder, when several of the "
                                                 "same year are installed")
    ap.add_argument("--visual-studio-msvc", metavar="TOOLSET",
                    help="MSVC toolset folder to compile with, e.g. 14.51.36231 (default: the newest in that install)")
    ap.add_argument("--resave-assets", action="store_true",
                    help="after building, resave assets that still store old module names (assets that load with "
                         "warnings are skipped, so nothing is saved in a broken state)")
    ap.add_argument("--remove-unused-redirects", action="store_true",
                    help="after resaving, remove [CoreRedirects] for old names no asset uses any more. Only do this "
                         "if no other branch or teammate has assets that still use the old names")
    ap.add_argument("--verify-plugins", action="store_true",
                    help="after building, check every enabled plugin is mounted, its modules load, and the "
                         "project's assets that use it load without warnings")
    ap.add_argument("--open-editor", action="store_true", help="open the renamed project in Unreal Editor at the end")
    ap.add_argument("--cook", action="store_true", help="after building, cook the content for Windows (editor stays closed)")
    ap.add_argument("--package", choices=["Development", "Shipping"],
                    help="after building, cook and package a Windows build in this configuration (editor stays closed)")
    ap.add_argument("--clean-cook", action="store_true",
                    help="with --cook/--package, delete old cooked data and cook everything (default: only recook "
                         "what changed)")
    ap.add_argument("--package-dir", help="where --package saves the build (default: <project>/Builds/<config>)")
    ap.add_argument("--engine-dir", help="engine root (e.g. 'C:/Program Files/Epic Games/UE_5.8'); "
                                         "found from EngineAssociation if omitted")
    ap.add_argument("--engine-association",
                    help="set the new project's EngineAssociation, e.g. '5.8' for a launcher engine "
                         "(default: keep the original value)")
    ap.add_argument("--allow-common-name", action="store_true",
                    help="rename even though the old name is a common word or part of engine names "
                         "(engine and plugin names are still left alone)")
    ap.add_argument("-y", "--yes", action="store_true", help="don't ask for confirmation")
    args = ap.parse_args()

    uproject = find_uproject(Path(args.project).resolve())
    if args.action:
        return run_action(args, uproject)
    if not args.new_name:
        sys.exit("error: give the new project name (or use --action to build without renaming)")
    src_root = uproject.parent
    old, new = uproject.stem, args.new_name

    if not IDENT_RE.match(new):
        sys.exit(f"error: '{new}' is not a valid project name (use letters, digits and underscores; start with a letter)")
    if new == old:
        sys.exit("error: the new name is the same as the old one")
    if len(new) > 20:
        print(f"warning: '{new}' is longer than 20 characters; Unreal recommends shorter project names")

    folder_target = None
    if args.in_place:
        if args.output:
            sys.exit("error: --output can't be used with --in-place")
        dst_root = src_root
        if not args.keep_folder_name and src_root.name == old:
            folder_target = src_root.parent / new
            if folder_target.exists():
                sys.exit(f"error: can't rename the project folder, {folder_target} already exists "
                         "(use --keep-folder-name to leave the folder as it is)")
    else:
        dst_root = Path(args.output).resolve() if args.output else src_root.parent / new
        if dst_root == src_root or src_root in dst_root.parents:
            sys.exit("error: the output folder must be outside the source project")
        if dst_root.exists() and any(dst_root.iterdir()):
            sys.exit(f"error: output folder already exists and is not empty: {dst_root}")

    print(f"Renaming '{old}' -> '{new}'" + (" IN PLACE" if args.in_place else ""))
    print(f"  from: {src_root}")
    if args.in_place:
        print(f"  folder: {'renamed to ' + str(folder_target) if folder_target else 'unchanged'}")
    else:
        print(f"  to:   {dst_root}")

    # Engine association: an unknown value makes Unreal ask "Select Unreal Engine version"
    # every time someone double-clicks the .uproject.
    old_assoc = read_association(uproject)

    # Downgrading isn't possible: assets saved by a newer engine won't open in an older one.
    if args.engine_association:
        was, to = engine_version(old_assoc), engine_version(args.engine_association)
        if was and to and to < was:
            sys.exit(f"error: can't switch the project from UE {was[0]}.{was[1]} to the older UE {to[0]}.{to[1]}. "
                     "Assets saved by a newer engine can't be opened by an older one. Pick the same or a newer engine.")

    engine_for_names = (Path(args.engine_dir) if args.engine_dir
                        else engine_dir_for_association(args.engine_association or old_assoc))
    plan = build_plan(src_root, uproject, new, dst_root, engine_for_names)
    installed = installed_launcher_engines()
    if args.engine_association:
        plan.engine_association = args.engine_association
        print(f"  engine: {old_assoc or '(none)'} -> {args.engine_association}")
        if sys.platform == "win32" and engine_dir_for_association(args.engine_association) is None:
            plan.warnings.append(
                f"EngineAssociation '{args.engine_association}' isn't registered on this PC. "
                f"Installed launcher engines: {', '.join(installed) or 'none'}."
            )
    else:
        print(f"  engine: {old_assoc or '(none)'} (unchanged)")
        if sys.platform == "win32" and old_assoc and engine_dir_for_association(old_assoc) is None:
            hint = f" --engine-association {installed[-1]}" if installed else " --engine-association <version>"
            plan.warnings.append(
                f"EngineAssociation '{old_assoc}' isn't registered on this PC (the original project has the same "
                f"problem), so opening the .uproject will ask which engine to use. To fix it in the copy, add:{hint}"
            )

    execute(plan, dry_run=True)  # fills plan.rewrites without writing

    if args.in_place:
        gen = generated_paths(src_root)
        print(f"\nGenerated build output to delete ({len(gen)}):")
        for p in gen:
            print(f"  {p.relative_to(src_root).as_posix()}")
    else:
        print(f"\nFiles to copy: {len(plan.copies)}")
    print(f"\nRenamed paths ({len(plan.renamed)}):")
    for a, b in plan.renamed:
        print(f"  {a}  ->  {b}")
    print(f"\nFiles with text changes ({len(plan.rewrites)}):")
    for rel, n in sorted(plan.rewrites.items(), key=lambda kv: kv[0].as_posix()):
        print(f"  {rel.as_posix()}  ({n} replacement{'s' if n != 1 else ''})")
    print(f"\nCore redirects added to Config/DefaultEngine.ini ({len(plan.redirects)}):")
    for r in plan.redirects:
        print(f"  {r}")
    if plan.plugin_dirs:
        print(f"\nPlugins that use the project's code (its names are updated in them) ({len(plan.plugin_dirs)}):")
        for d in plan.plugin_dirs:
            print(f"  {d.as_posix()}")
    if plan.protected_seen:
        print(f"\nEngine/plugin names left alone ({len(plan.protected_seen)}):")
        for n in sorted(plan.protected_seen):
            print(f"  {n}")
    for w in plan.warnings:
        print(f"\nwarning: {w}")
    common_msg = None
    if plan.common_name:
        hits = ""
        if plan.common_hits:
            hits = (" that is part of engine names (" + ", ".join(plan.common_hits[:6])
                    + (", ..." if len(plan.common_hits) > 6 else "") + ")")
        common_msg = (f"'{old}' is a common word{hits}. To be safe, only the project's own names are renamed "
                      "(modules, files, its classes/structs/enums, properties, functions, delegates and _API macros); "
                      f"anything else containing '{old}' - local variables, comments, text - is left as it is. "
                      "Check the lists above.")
        print(f"\nwarning: COMMON NAME: {common_msg}")
    for e in plan.errors:
        print(f"\nerror: {e}")
    if plan.errors:
        print("\nNothing was written.")
        return 1

    if args.dry_run:
        print("\nDry run - nothing was written.")
        return 0

    if plan.common_name and not args.allow_common_name:
        # Exit code 3: the GUI asks the user and re-runs with --allow-common-name.
        print(f"\nerror: COMMON NAME: not renaming without confirmation. Nothing was written. "
              "Pass --allow-common-name to go ahead.")
        return 3

    if not args.yes:
        question = ("Rename the project in place? Make sure it's committed or backed up."
                    if args.in_place else "Create the renamed copy?")
        if input(f"\n{question} [y/N] ").strip().lower() not in ("y", "yes"):
            print("Cancelled.")
            return 1

    plan.rewrites.clear()
    if args.in_place:
        execute_in_place(plan)
        final_root = src_root
        if folder_target:
            try:
                src_root.rename(folder_target)
                final_root = folder_target
            except OSError as e:
                print(f"\nwarning: files were renamed, but the folder couldn't be renamed ({e}). "
                      "Close anything using it (editor, Visual Studio, Explorer) and rename it by hand.")
    else:
        execute(plan, dry_run=False)
        final_root = dst_root
    new_uproject = final_root / f"{new}.uproject"
    print(f"\nDone: {new_uproject}")

    return after_rename(args, new_uproject)


def after_rename(args, new_uproject: Path) -> int:
    """Optional follow-up steps. Returns 0, or 2 if a step failed (the rename itself still succeeded)."""
    root = new_uproject.parent
    ini = root / "Config" / "DefaultEngine.ini"
    wants_package = bool(args.cook or args.package)
    wants_engine = (args.generate_project_files or args.build or args.resave_assets
                    or args.remove_unused_redirects or args.verify_plugins or args.open_editor or wants_package)
    engine = find_engine_dir(new_uproject, args.engine_dir) if wants_engine else None
    if wants_engine and engine is None:
        print("\nerror: couldn't find the engine for the follow-up steps; pass --engine-dir")
        return 2

    failed = False
    generated = built = resaved_ok = False
    skipped: list[str] = []

    vs = None
    if (args.visual_studio or args.visual_studio_path) and (args.generate_project_files or args.build
                                                             or args.resave_assets or args.verify_plugins):
        vs = find_visual_studio(args.visual_studio or "", args.visual_studio_path)
        if vs is not None and args.visual_studio_msvc:
            vs.msvc = args.visual_studio_msvc
        if vs is None:
            print(f"\nwarning: Visual Studio {args.visual_studio or args.visual_studio_path} wasn't found; "
                  "letting Unreal choose")
        elif not engine_supports_vs(engine, vs.year):
            print(f"\nwarning: this engine doesn't support Visual Studio {vs.year}; letting Unreal choose")
            vs = None
        else:
            print(f"\nUsing {vs.describe()}")

    if args.generate_project_files:
        print("\nGenerating project files...")
        generated = generate_project_files(engine, new_uproject, vs)
        if not generated:
            print("  project file generation failed (see output above)")
            failed = True

    if args.build or args.resave_assets or args.verify_plugins or wants_package:
        rebuild = bool(getattr(args, "rebuild", False)) or getattr(args, "action", None) == "rebuild"
        print("\nRebuilding (clean, then build)..." if rebuild else "\nBuilding...")
        built = build_project(engine, new_uproject, vs, rebuild)
        if not built:
            print("  build FAILED (see output above); skipping the steps that need it")
            failed = True

    if args.resave_assets and built:
        print("\nResaving assets that still store old names...")
        saved, skipped = resave_assets(engine, new_uproject, redirected_packages(ini))
        resaved_ok = not skipped
        if skipped:
            failed = True

    if args.remove_unused_redirects:
        if not args.resave_assets or not built:
            print("\nNot removing redirects: that needs a successful --build and --resave-assets first")
        else:
            print("\nRemoving redirects no asset uses any more...")
            removed = remove_unused_redirects(ini, root)
            kept = redirected_packages(ini)
            for p in removed:
                print(f"  removed redirects for {p}")
            for p in kept:
                print(f"  kept redirects for {p} (assets still use it)")
            if not removed and not kept:
                print("  nothing to remove")

    plugins_ok = None
    if args.verify_plugins and built:
        print("\nVerifying plugins...")
        plugins_ok = verify_plugins(engine, new_uproject)
        if not plugins_ok:
            failed = True

    packaged_ok = None
    if wants_package and built:
        if args.package:
            out_dir = Path(args.package_dir).resolve() if args.package_dir else root / "Builds" / args.package
            print(f"\nCooking and packaging a {args.package} build"
                  f" ({'clean cook' if args.clean_cook else 'only recooking what changed'})...")
        else:
            out_dir = None
            print(f"\nCooking content ({'clean cook' if args.clean_cook else 'only recooking what changed'})...")
        packaged_ok = cook_and_package(engine, new_uproject, args.package, args.clean_cook, out_dir,
                                       bool(getattr(args, "rebuild", False)))
        if not packaged_ok:
            print(f"  {'packaging' if args.package else 'cook'} FAILED (see output above)")
            failed = True
        elif args.package:
            print(f"  Packaged: {out_dir}")
            if args.launch:
                game = out_dir / "Windows" / f"{new_uproject.stem}.exe"
                if game.exists():
                    print(f"\nLaunching {game}")
                    flags = getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
                    subprocess.Popen([str(game)], cwd=str(game.parent), creationflags=flags, close_fds=True)
                else:
                    print(f"\nwarning: packaged, but {game} wasn't found, so it wasn't launched")
        else:
            print(f"  Cooked: {root / 'Saved' / 'Cooked'}")

    if args.open_editor and (built or not args.build):
        print("\nOpening Unreal Editor...")
        open_in_editor(engine, new_uproject)

    todo = []
    if not args.generate_project_files:
        todo.append("Right-click the new .uproject -> 'Generate Visual Studio project files'")
    if not built:
        todo.append("Build the project (or open the .uproject and let Unreal build it)")
    if packaged_ok is False:
        todo.append(f"Fix the {'packaging' if args.package else 'cook'} errors above, then try again "
                    "(or use Platforms > Windows in the editor)")
    if plugins_ok is False:
        todo.append("Fix the plugins marked FAIL above (see their messages), then build again")
    if skipped:
        todo.append("Open the skipped assets listed above, fix their warnings, and save them")
    elif not resaved_ok and getattr(args, "action", None) is None:  # only a rename leaves old names behind
        todo.append("Assets that use the old C++ names load through the [CoreRedirects] in "
                    "Config/DefaultEngine.ini. Keep those lines unless every asset (on every branch) "
                    "has been resaved since the rename")
    print("\nNext steps:" if todo else "\nAll steps finished.")
    for i, t in enumerate(todo, 1):
        print(f"  {i}. {t}")

    # Last of all: what to confirm in the editor (only when the main work got done).
    if built or not args.build:
        action = getattr(args, "action", None)
        items = list(EDITOR_CHECKS)
        if action is None:  # a rename
            items.insert(0, ("The project in Unreal Editor",
                             "Open it. Blueprints based on the renamed C++ classes should compile with no errors "
                             "(Window > Message Log), and their values should be intact."))
        elif action == "upgrade":
            items.insert(0, ("The project in Unreal Editor",
                             "Open it in the new engine. Check Window > Message Log for upgrade warnings; assets "
                             "are converted to the new engine when you save them."))
        if packaged_ok and args.package and action == "package":
            # A Package Build only needs the packaged game checked.
            game = out_dir / "Windows" / f"{new_uproject.stem}.exe"
            print_editor_checks([(str(game), "The packaged game: " +
                                  ("it was started for you. " if args.launch else "start it. ") +
                                  "Check it opens, loads the first level and plays.")], BUILD_CHECK_HEADER)
            return 2 if failed else 0
        if packaged_ok and args.package:
            game = out_dir / "Windows" / f"{new_uproject.stem}.exe"
            items.append((str(game), "The packaged game: " + ("it was started for you. " if args.launch else "start it. ") +
                          "Check it opens, loads the first level and plays."))
        for m in default_maps(root):
            items.append((m, "Your start-up map: open it and press Play to check the game still runs."))
        print_editor_checks(items)
    return 2 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
