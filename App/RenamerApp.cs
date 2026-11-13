// VFSUnrealProjectBuildTool - Windows GUI for VFSUnrealProjectBuildTool.py (build, rebuild, package or rename).
//
// The Python script is embedded in this .exe and run with the Python that ships
// with every Unreal Engine install, so the rename logic lives in one place.
// Build with build.bat next to this file.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

// Shown in the .exe's Properties > Details. The number itself is Program.Version.
[assembly: AssemblyTitle("VFS Unreal Project Build Tool")]
[assembly: AssemblyProduct("VFS Unreal Project Build Tool")]
[assembly: AssemblyVersion(UnrealProjectRenamer.Program.Version + ".0")]
[assembly: AssemblyFileVersion(UnrealProjectRenamer.Program.Version + ".0")]
[assembly: AssemblyInformationalVersion(UnrealProjectRenamer.Program.Version)]

namespace UnrealProjectRenamer
{
    static class Program
    {
        // App version: bump this for every build you hand out (major.minor.patch).
        public const string Version = "0.21.3";

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Before the main window: check GitHub for a newer version and, if there is one, swap it in
            // and start it instead. Skipped right after an update (no loops) and when turned off.
            // Nothing in here may stop the app opening (offline, GitHub down, any error): it just carries on.
            try
            {
                Updater.CleanUp();
                if (!args.Contains(Updater.UpdatedArg) && Updater.Enabled)
                {
                    using (var f = new UpdateForm())
                    {
                        Application.Run(f);
                        if (f.Restarted) return;
                    }
                }
            }
            catch { }
            Application.Run(new MainForm());
        }
    }

    /// <summary>Finds engines and Python on this PC.</summary>
    static class Engines
    {
        /// <summary>Launcher installs: version -> engine root, e.g. "5.8" -> C:\Program Files\Epic Games\UE_5.8.</summary>
        public static SortedDictionary<string, string> Installed()
        {
            var result = new SortedDictionary<string, string>(Comparer<string>.Create(CompareVersions));
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine"))
                {
                    if (key != null)
                        foreach (var ver in key.GetSubKeyNames())
                            using (var sub = key.OpenSubKey(ver))
                            {
                                var dir = sub == null ? null : sub.GetValue("InstalledDirectory") as string;
                                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) result[ver] = dir;
                            }
                }
            }
            catch { }
            // The launcher doesn't always write the registry key (e.g. 5.7 / 5.8 on some PCs);
            // its own install list has every engine it installed: AppName "UE_5.8" -> InstallLocation.
            try
            {
                var dat = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Epic\UnrealEngineLauncher\LauncherInstalled.dat");
                if (File.Exists(dat))
                {
                    var d = new System.Web.Script.Serialization.JavaScriptSerializer()
                        .Deserialize<Dictionary<string, object>>(File.ReadAllText(dat));
                    object list;
                    if (d != null && d.TryGetValue("InstallationList", out list) && list is System.Collections.IEnumerable)
                        foreach (var item in (System.Collections.IEnumerable)list)
                        {
                            var e = item as Dictionary<string, object>;
                            object app, loc;
                            if (e == null || !e.TryGetValue("AppName", out app) || !e.TryGetValue("InstallLocation", out loc)) continue;
                            var m = Regex.Match(app as string ?? "", @"^UE_(\d+\.\d+)$");
                            var dir = loc as string;
                            if (m.Success && !result.ContainsKey(m.Groups[1].Value) && !string.IsNullOrEmpty(dir)
                                && Directory.Exists(System.IO.Path.Combine(dir, "Engine")))
                                result[m.Groups[1].Value] = dir;
                        }
                }
            }
            catch { }
            return result;
        }

        /// <summary>Source builds registered with the version selector: GUID -> engine root.</summary>
        public static Dictionary<string, string> SourceBuilds()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds"))
                {
                    if (key != null)
                        foreach (var name in key.GetValueNames())
                        {
                            var dir = key.GetValue(name) as string;
                            if (!string.IsNullOrEmpty(dir)) result[name] = dir;
                        }
                }
            }
            catch { }
            return result;
        }

        public static bool IsRegistered(string association)
        {
            if (string.IsNullOrEmpty(association)) return false;
            return Installed().ContainsKey(association) || SourceBuilds().ContainsKey(association);
        }

        /// <summary>Major.minor engine version of an EngineAssociation ("5.8", or a registered source
        /// build's Build.version), or null if unknown.</summary>
        public static Version VersionOf(string association)
        {
            if (string.IsNullOrEmpty(association)) return null;
            var m = Regex.Match(association, @"^(\d+)\.(\d+)");
            if (m.Success) return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            var dir = EngineDirFor(association);
            if (dir == null) return null;
            try
            {
                var json = File.ReadAllText(System.IO.Path.Combine(dir, @"Engine\Build\Build.version"));
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                return new Version(Convert.ToInt32(d["MajorVersion"]), Convert.ToInt32(d["MinorVersion"]));
            }
            catch { return null; }
        }

        /// <summary>Engine folder for an EngineAssociation ("5.8" or a source-build GUID), or null.</summary>
        public static string EngineDirFor(string association)
        {
            if (string.IsNullOrEmpty(association)) return null;
            string dir;
            if (Installed().TryGetValue(association, out dir)) return dir;
            if (SourceBuilds().TryGetValue(association, out dir) && Directory.Exists(dir)) return dir;
            return null;
        }

        /// <summary>A Visual Studio install, with the MSVC toolsets it contains.</summary>
        public class VisualStudio
        {
            public string Name, Year, Path, Version;
            /// <summary>MSVC toolset folder name -> real compiler version (from cl.exe), newest first.</summary>
            public List<KeyValuePair<string, string>> Toolsets = new List<KeyValuePair<string, string>>();
            public override string ToString() { return Name + "  (" + Version + ")"; }
        }

        /// <summary>Installed Visual Studios, newest first, found with Microsoft's vswhere tool.</summary>
        public static List<VisualStudio> VisualStudios()
        {
            var result = new List<VisualStudio>();
            var vswhere = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft Visual Studio\Installer\vswhere.exe");
            if (!File.Exists(vswhere)) return result;
            try
            {
                var psi = new ProcessStartInfo(vswhere, "-all -prerelease -products * -format json")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
                };
                string json;
                using (var p = Process.Start(psi)) { json = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
                var years = new Dictionary<string, string> { { "15", "2017" }, { "16", "2019" }, { "17", "2022" }, { "18", "2026" } };
                var list = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(json);
                foreach (var inst in list)
                {
                    var path = inst.ContainsKey("installationPath") ? inst["installationPath"] as string : null;
                    var version = inst.ContainsKey("installationVersion") ? inst["installationVersion"] as string : "";
                    string year;
                    if (path == null || !years.TryGetValue(version.Split('.')[0], out year)) continue;
                    var name = inst.ContainsKey("displayName") ? inst["displayName"] as string : "Visual Studio " + year;
                    var vs = new VisualStudio { Name = name, Year = year, Path = path, Version = version };
                    // Toolsets: the folder name is what UBT is told; the real version (which the engine's
                    // banned/minimum lists refer to) comes from cl.exe itself.
                    var msvc = System.IO.Path.Combine(path, @"VC\Tools\MSVC");
                    if (Directory.Exists(msvc))
                        foreach (var dir in Directory.GetDirectories(msvc))
                        {
                            var cl = System.IO.Path.Combine(dir, @"bin\Hostx64\x64\cl.exe");
                            if (!File.Exists(cl)) continue;
                            var fv = FileVersionInfo.GetVersionInfo(cl);
                            // cl.exe calls itself 19.xx; Unreal's lists use the toolset numbering 14.xx
                            // (same minor and build), as UnrealBuildTool does.
                            var real = string.Format("14.{0}.{1}", fv.FileMinorPart, fv.FileBuildPart);
                            vs.Toolsets.Add(new KeyValuePair<string, string>(System.IO.Path.GetFileName(dir), real));
                        }
                    vs.Toolsets = vs.Toolsets.OrderByDescending(t => t.Value, Comparer<string>.Create(CompareVersions)).ToList();
                    result.Add(vs);
                }
            }
            catch { }
            return result.OrderByDescending(v => v.Version, Comparer<string>.Create(CompareVersions)).ToList();
        }

        /// <summary>Windows 10/11 SDK versions installed (those with headers).</summary>
        public static List<string> WindowsSdks()
        {
            var result = new List<string>();
            try
            {
                string root = null;
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots"))
                    if (key != null) root = key.GetValue("KitsRoot10") as string;
                if (root != null && Directory.Exists(System.IO.Path.Combine(root, "Include")))
                    foreach (var dir in Directory.GetDirectories(System.IO.Path.Combine(root, "Include")))
                        if (File.Exists(System.IO.Path.Combine(dir, @"um\windows.h")))
                            result.Add(System.IO.Path.GetFileName(dir));
            }
            catch { }
            return result;
        }

        /// <summary>What an engine needs from Visual Studio and the Windows SDK (its Windows_SDK.json),
        /// plus the Visual Studio years its UnrealBuildTool knows.</summary>
        public class Requirements
        {
            public string EngineName, MinMsvc, MinSdk, MaxSdk;
            public List<string> BannedMsvc = new List<string>();
            public Dictionary<string, string> MinVsByYear = new Dictionary<string, string>();
            public HashSet<string> SupportedYears;  // null = couldn't tell
            public Version EngineVersion;           // major.minor from Engine/Build/Build.version; null = couldn't tell
            // Our rule (not Unreal's): UE 5.8 and newer are built with Visual Studio 2026 or newer.
            static readonly Version NeedsVs2026From = new Version(5, 8);

            public static Requirements Load(string engineDir, string engineName)
            {
                var r = new Requirements { EngineName = engineName };
                try
                {
                    var bv = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(System.IO.Path.Combine(engineDir, @"Engine\Build\Build.version")));
                    r.EngineVersion = new Version(Convert.ToInt32(bv["MajorVersion"]), Convert.ToInt32(bv["MinorVersion"]));
                }
                catch { }
                try
                {
                    var json = File.ReadAllText(System.IO.Path.Combine(engineDir, @"Engine\Config\Windows\Windows_SDK.json"));
                    var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    object v;
                    if (d.TryGetValue("MinimumVisualCppVersion", out v)) r.MinMsvc = v as string;
                    if (d.TryGetValue("MinVersion", out v)) r.MinSdk = v as string;
                    if (d.TryGetValue("MaxVersion", out v)) r.MaxSdk = v as string;
                    if (d.TryGetValue("BannedVisualCppVersions", out v) && v is System.Collections.ArrayList)
                        foreach (var b in (System.Collections.ArrayList)v) r.BannedMsvc.Add(b as string);
                    foreach (var kv in d)
                    {
                        var m = Regex.Match(kv.Key, @"^MinimumVisualStudio(\d{4})Version$");
                        if (m.Success) r.MinVsByYear[m.Groups[1].Value] = kv.Value as string;
                    }
                }
                catch { }
                try
                {
                    var bytes = File.ReadAllBytes(System.IO.Path.Combine(engineDir, @"Engine\Binaries\DotNET\UnrealBuildTool\UnrealBuildTool.dll"));
                    var text = Encoding.ASCII.GetString(bytes) + Encoding.Unicode.GetString(bytes);
                    r.SupportedYears = new HashSet<string>(Regex.Matches(text, @"VisualStudio(20\d\d)").Cast<Match>().Select(m => m.Groups[1].Value));
                }
                catch { }
                return r;
            }

            bool Banned(string version)
            {
                foreach (var range in BannedMsvc)
                {
                    var parts = (range ?? "").Split('-');
                    if (parts.Length == 2 && CompareVersions(version, parts[0]) >= 0 && CompareVersions(version, parts[1]) <= 0) return true;
                    if (parts.Length == 1 && CompareVersions(version, parts[0]) == 0) return true;
                }
                return false;
            }

            /// <summary>The newest MSVC toolset (folder name) in `vs` this engine accepts, or null.</summary>
            public string UsableToolset(VisualStudio vs)
            {
                foreach (var t in vs.Toolsets)
                    if ((MinMsvc == null || CompareVersions(t.Value, MinMsvc) >= 0) && !Banned(t.Value)) return t.Key;
                return null;
            }

            /// <summary>Why `vs` can't build for this engine, or null if it can.</summary>
            public string ProblemWith(VisualStudio vs)
            {
                int year;
                if (EngineVersion != null && EngineVersion >= NeedsVs2026From && int.TryParse(vs.Year, out year) && year < 2026)
                    return EngineName + " requires Visual Studio 2026 or higher. Pick it above, or install it with the " +
                           "\"Game development with C++\" workload.";
                if (SupportedYears != null && SupportedYears.Count > 0 && !SupportedYears.Contains(vs.Year))
                    return EngineName + " doesn't support Visual Studio " + vs.Year + ".";
                string min;
                if (MinVsByYear.TryGetValue(vs.Year, out min) && min != null && CompareVersions(vs.Version, min) < 0)
                    return EngineName + " needs Visual Studio " + vs.Year + " version " + min + " or newer - update it in the Visual Studio Installer.";
                if (vs.Toolsets.Count == 0)
                    return "This Visual Studio has no C++ compiler. In the Visual Studio Installer, add the \"Game development with C++\" workload.";
                if (UsableToolset(vs) == null)
                    return "This Visual Studio's C++ compiler (" + vs.Toolsets[0].Value + ") is too old or not allowed by " + EngineName +
                           " (needs " + (MinMsvc ?? "a newer version") + "+). Update it in the Visual Studio Installer.";
                return null;
            }

            /// <summary>Why the installed Windows SDKs won't do for this engine, or null.</summary>
            public string SdkProblem(List<string> sdks)
            {
                if (sdks.Count == 0)
                    return "No Windows SDK is installed. In the Visual Studio Installer, add a Windows 11 SDK (part of \"Game development with C++\").";
                if (MinSdk != null && !sdks.Any(s => CompareVersions(s, MinSdk) >= 0 && (MaxSdk == null || CompareVersions(s, MaxSdk) <= 0)))
                    return EngineName + " needs Windows SDK " + MinSdk + " or newer; installed: " + string.Join(", ", sdks) + ".";
                return null;
            }
        }

        /// <summary>Unreal Editors, Visual Studios and Riders that have this project open.</summary>
        public static List<string> ProgramsUsing(string projectPath, string projectName)
        {
            var found = new List<string>();
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath)).TrimEnd('\\');
            Func<string, bool> mentionsProject = cmd =>
            {
                if (string.IsNullOrEmpty(cmd)) return false;
                var c = cmd.Replace('/', '\\');
                return c.IndexOf(dir + "\\", StringComparison.OrdinalIgnoreCase) >= 0;
            };
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name='UnrealEditor.exe' OR " +
                    "Name='UnrealEditor-Cmd.exe' OR Name='devenv.exe' OR Name='rider64.exe'"))
                    foreach (System.Management.ManagementObject p in searcher.Get())
                    {
                        var name = (p["Name"] as string ?? "").ToLowerInvariant();
                        var cmd = p["CommandLine"] as string;
                        bool hit = mentionsProject(cmd);
                        if (!hit && (name == "devenv.exe" || name == "rider64.exe"))
                        {
                            // IDEs opened from their start screen don't have the path on the command line,
                            // so also check the window title ("MyGame - Microsoft Visual Studio").
                            try
                            {
                                var title = Process.GetProcessById(Convert.ToInt32(p["ProcessId"])).MainWindowTitle ?? "";
                                hit = Regex.IsMatch(title, @"^(Automation_)?" + Regex.Escape(projectName) + @"(\.sln)?(\s|$)", RegexOptions.IgnoreCase);
                            }
                            catch { }
                        }
                        if (hit)
                            found.Add(name.StartsWith("unrealeditor") ? "Unreal Editor" : name == "devenv.exe" ? "Visual Studio" : "Rider");
                    }
            }
            catch { }
            return found.Distinct().ToList();
        }

        /// <summary>The newest Unreal-bundled Python, falling back to a Python on PATH.</summary>
        public static string FindPython()
        {
            var roots = Installed().Values.Reverse().ToList();
            var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
            if (Directory.Exists(epic))
                roots.AddRange(Directory.GetDirectories(epic, "UE_*").OrderByDescending(d => d));
            foreach (var root in roots)
            {
                var py = Path.Combine(root, @"Engine\Binaries\ThirdParty\Python3\Win64\python.exe");
                if (File.Exists(py)) return py;
            }
            // Python on PATH (skipping the Microsoft Store stub, which is a 0-byte alias).
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var py = Path.Combine(dir.Trim(), "python.exe");
                    if (File.Exists(py) && new FileInfo(py).Length > 0 && !py.Contains("WindowsApps")) return py;
                }
                catch { }
            }
            return null;
        }

        static int CompareVersions(string a, string b)
        {
            var pa = a.Split('.');
            var pb = b.Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x, y;
                bool nx = int.TryParse(i < pa.Length ? pa[i] : "0", out x);
                bool ny = int.TryParse(i < pb.Length ? pb[i] : "0", out y);
                int c = nx && ny ? x.CompareTo(y) : string.CompareOrdinal(i < pa.Length ? pa[i] : "", i < pb.Length ? pb[i] : "");
                if (c != 0) return c;
            }
            return 0;
        }
    }

    /// <summary>The app's only theme: dark, with the warm greys and orange accent of the Claude apps.</summary>
    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(38, 38, 36);        // window
        public static readonly Color Surface = Color.FromArgb(31, 30, 29);     // text boxes, log
        public static readonly Color Raised = Color.FromArgb(48, 48, 46);      // buttons
        public static readonly Color Hover = Color.FromArgb(60, 60, 57);
        public static readonly Color Border = Color.FromArgb(68, 68, 64);
        public static readonly Color Text = Color.FromArgb(232, 230, 227);
        public static readonly Color Muted = Color.FromArgb(156, 154, 146);
        public static readonly Color Disabled = Color.FromArgb(105, 104, 99);
        public static readonly Color Accent = Color.FromArgb(217, 119, 87);
        public static readonly Color AccentHover = Color.FromArgb(229, 137, 107);
        public static readonly Color Warn = Color.FromArgb(224, 164, 88);
        public static readonly Color Error = Color.FromArgb(240, 90, 85);       // red: problems that block the main button
        public static readonly Color Highlight = Color.FromArgb(250, 204, 21); // yellow: the "check in the editor" list

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        /// <summary>Dark title bar (Windows 10 2004+ uses attribute 20, older builds 19).</summary>
        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
        }

        /// <summary>Dark scroll bars on text boxes and lists.</summary>
        public static void DarkScrollBars(Control c)
        {
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            else c.HandleCreated += (s, e) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        }

        public static void Apply(Control root)
        {
            foreach (Control c in root.Controls) Apply(c);
            if (root is Form || root is Panel || root is Label || root is TableLayoutPanel || root is FlowLayoutPanel)
            {
                root.BackColor = Back;
                if (root.ForeColor != Warn && root.ForeColor != Error && root.ForeColor != Muted) root.ForeColor = Text; // keep notes' colours
            }
            var tb = root as TextBoxBase; // text boxes and the rich-text log
            if (tb != null)
            {
                tb.BackColor = Surface;
                tb.ForeColor = Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                DarkScrollBars(tb);
            }
            var btn = root as Button;
            if (btn != null) StyleButton(btn, false);
            var cb = root as CheckBox;
            if (cb != null) StyleCheck(cb.FlatAppearance, cb);
            var rb = root as RadioButton;
            if (rb != null) StyleCheck(rb.FlatAppearance, rb);
            var combo = root as ComboBox;
            if (combo != null) StyleCombo(combo);
        }

        /// <summary>A tooltip drawn in the dark theme, wrapped to a readable width, that stays up long enough to read.</summary>
        public static ToolTip DarkToolTip()
        {
            const int maxWidth = 360, pad = 8;
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.NoPrefix;
            var font = new Font("Segoe UI", 9f);
            var tip = new ToolTip { OwnerDraw = true, InitialDelay = 400, AutoPopDelay = 30000, ReshowDelay = 200 };
            tip.Popup += (s, e) =>
            {
                var text = tip.GetToolTip(e.AssociatedControl);
                var size = TextRenderer.MeasureText(text, font, new Size(maxWidth, 0), flags);
                e.ToolTipSize = new Size(size.Width + pad * 2, size.Height + pad * 2);
            };
            tip.Draw += (s, e) =>
            {
                using (var bg = new SolidBrush(Raised)) e.Graphics.FillRectangle(bg, e.Bounds);
                using (var edge = new Pen(Border)) e.Graphics.DrawRectangle(edge, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                var area = new Rectangle(pad, pad, e.Bounds.Width - pad * 2, e.Bounds.Height - pad * 2);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, font, area, Text, flags);
            };
            return tip;
        }

        public static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Padding = new Padding(10, 2, 10, 2);
            b.FlatAppearance.BorderSize = 1;
            Action paint = () =>
            {
                bool on = b.Enabled;
                b.BackColor = primary && on ? Accent : Raised;
                b.ForeColor = !on ? Disabled : primary ? Color.FromArgb(28, 20, 16) : Text;
                b.FlatAppearance.BorderColor = primary && on ? Accent : Border;
                b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Hover;
                b.FlatAppearance.MouseDownBackColor = primary ? Accent : Border;
            };
            paint();
            b.EnabledChanged += (s, e) => paint();
        }

        static void StyleCheck(FlatButtonAppearance fa, ButtonBase c)
        {
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Back;
            c.ForeColor = Text;
            fa.BorderColor = Muted;
            fa.CheckedBackColor = Accent;
            fa.MouseOverBackColor = Hover;
            c.EnabledChanged += (s, e) => c.ForeColor = c.Enabled ? Text : Disabled;
        }

        static void StyleCombo(ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = Surface;
            combo.ForeColor = Text;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.ItemHeight = combo.Font.Height + 6;
            combo.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var bg = new SolidBrush(hot ? Hover : Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
                TextRenderer.DrawText(e.Graphics, combo.Items[e.Index].ToString(), combo.Font,
                    new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height), combo.Enabled ? Text : Disabled,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            DarkScrollBars(combo);
        }
    }

    /// <summary>Short sound effects, synthesized in code (no sound files to ship).</summary>
    static class Sfx
    {
        public enum Kind { Start, Success, Failure }

        const int Rate = 44100;
        static System.Media.SoundPlayer player; // kept alive while it plays
        static readonly Dictionary<Kind, byte[]> cache = new Dictionary<Kind, byte[]>();

        public static void Play(Kind kind)
        {
            try
            {
                if (player != null) player.Stop();
                player = new System.Media.SoundPlayer(new MemoryStream(Wav(kind)));
                player.Play(); // plays in the background
            }
            catch { } // no audio device: stay silent
        }

        /// <summary>The sound as a complete .wav file (16-bit mono).</summary>
        public static byte[] Wav(Kind kind)
        {
            byte[] wav;
            if (cache.TryGetValue(kind, out wav)) return wav;
            // (start in seconds, frequency in Hz, length in seconds, volume, brightness)
            double[][] notes;
            switch (kind)
            {
                case Kind.Start:   // soft rising blip
                    notes = new[] { new[] { 0.00, 659.3, 0.12, 0.30, 0.2 }, new[] { 0.09, 880.0, 0.20, 0.30, 0.2 } };
                    break;
                case Kind.Success: // bright rising chime, last note rings
                    notes = new[] { new[] { 0.00, 523.3, 0.30, 0.26, 0.3 }, new[] { 0.09, 659.3, 0.30, 0.26, 0.3 },
                                    new[] { 0.18, 784.0, 0.35, 0.26, 0.3 }, new[] { 0.27, 1046.5, 0.70, 0.30, 0.3 } };
                    break;
                default:           // two falling, buzzier low notes
                    notes = new[] { new[] { 0.00, 392.0, 0.22, 0.32, 0.6 }, new[] { 0.20, 277.2, 0.45, 0.34, 0.6 } };
                    break;
            }
            double total = notes.Max(n => n[0] + n[2]) + 0.05;
            var samples = new double[(int)(total * Rate)];
            foreach (var n in notes)
            {
                int start = (int)(n[0] * Rate), len = (int)(n[2] * Rate);
                for (int i = 0; i < len && start + i < samples.Length; i++)
                {
                    double t = (double)i / Rate;
                    double env = Math.Min(1, t / 0.006) * Math.Exp(-t * 5.0 / n[2]); // quick attack, smooth decay
                    double w = 2 * Math.PI * n[1] * t;
                    samples[start + i] += n[3] * env * (Math.Sin(w) + n[4] * 0.5 * Math.Sin(2 * w) + n[4] * 0.25 * Math.Sin(3 * w));
                }
            }
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                int dataLen = samples.Length * 2;
                bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + dataLen); bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
                bw.Write(Rate); bw.Write(Rate * 2); bw.Write((short)2); bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(dataLen);
                foreach (var s in samples) bw.Write((short)(Math.Max(-1, Math.Min(1, s)) * 32000));
                bw.Flush();
                wav = ms.ToArray();
            }
            cache[kind] = wav;
            return wav;
        }
    }

    /// <summary>Check box drawn in the dark theme (the built-in flat style makes on/off hard to tell apart).</summary>
    class DarkCheck : CheckBox
    {
        public DarkCheck() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e) { Toggle.Paint(e.Graphics, this, false, Checked); }
        public override Size GetPreferredSize(Size proposed) { return Toggle.PreferredSize(this); }
    }

    /// <summary>Radio button drawn in the dark theme.</summary>
    class DarkRadio : RadioButton
    {
        public DarkRadio() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e) { Toggle.Paint(e.Graphics, this, true, Checked); }
        public override Size GetPreferredSize(Size proposed) { return Toggle.PreferredSize(this); }
    }

    static class Toggle
    {
        static int GlyphSize(Control c) { return Math.Max(12, c.Font.Height - 3); }

        /// <summary>Glyph + gap + text, so AutoSize never cuts the label off.</summary>
        public static Size PreferredSize(ButtonBase c)
        {
            var text = TextRenderer.MeasureText(c.Text, c.Font);
            return new Size(1 + GlyphSize(c) + 6 + text.Width + 4, Math.Max(text.Height, GlyphSize(c)) + 6);
        }

        public static void Paint(Graphics g, ButtonBase c, bool round, bool on)
        {
            g.Clear(Theme.Back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int size = GlyphSize(c);
            var box = new Rectangle(1, (c.Height - size) / 2, size, size);
            bool enabled = c.Enabled;
            Color fill = on ? (enabled ? Theme.Accent : Theme.Disabled) : Theme.Surface;
            Color edge = on ? fill : (enabled ? Theme.Muted : Theme.Disabled);
            using (var b = new SolidBrush(fill))
            using (var p = new Pen(edge, 1.4f))
            {
                if (round) { g.FillEllipse(b, box); g.DrawEllipse(p, box); }
                else
                {
                    using (var path = Rounded(box, 3)) { g.FillPath(b, path); g.DrawPath(p, path); }
                }
            }
            if (on)
            {
                var mark = Color.FromArgb(28, 20, 16);
                if (round)
                {
                    int d = size / 2 - 1;
                    using (var b = new SolidBrush(mark)) g.FillEllipse(b, box.X + (size - d) / 2f, box.Y + (size - d) / 2f, d, d);
                }
                else
                {
                    using (var p = new Pen(mark, 2f))
                        g.DrawLines(p, new[] {
                            new PointF(box.X + size * 0.22f, box.Y + size * 0.52f),
                            new PointF(box.X + size * 0.42f, box.Y + size * 0.72f),
                            new PointF(box.X + size * 0.78f, box.Y + size * 0.30f) });
                }
            }
            var textRect = new Rectangle(box.Right + 6, 0, c.Width - box.Right - 6, c.Height);
            TextRenderer.DrawText(g, c.Text, c.Font, textRect, enabled ? Theme.Text : Theme.Disabled,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>A dark replacement for MessageBox.</summary>
    class DarkDialog : Form
    {
        DarkDialog(string title, string message, bool warning, string yes, string no)
        {
            Text = title;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(20, 18, 20, 14) };
            var label = new Label { Text = message, AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 16) };
            layout.Controls.Add(label);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
            var yesButton = new Button { Text = yes, AutoSize = true, DialogResult = DialogResult.Yes, Name = "yes" };
            buttons.Controls.Add(yesButton);
            if (no != null)
                buttons.Controls.Add(new Button { Text = no, AutoSize = true, DialogResult = DialogResult.No, Name = "no" });
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = yesButton;
            CancelButton = no != null ? (IButtonControl)buttons.Controls[1] : yesButton;

            Theme.Apply(this);
            label.ForeColor = Theme.Text;
            Theme.StyleButton(yesButton, true);
            HandleCreated += (s, e) => Theme.DarkTitleBar(Handle);
        }

        public static bool Ask(IWin32Window owner, string title, string message, bool warning, string yes = "Yes", string no = "No")
        {
            using (var d = new DarkDialog(title, message, warning, yes, no)) return d.ShowDialog(owner) == DialogResult.Yes;
        }

        public static void Show(IWin32Window owner, string title, string message)
        {
            using (var d = new DarkDialog(title, message, true, "OK", null)) d.ShowDialog(owner);
        }
    }

    /// <summary>Self-update from the GitHub repo. Every push puts the new version in README.md's title
    /// ("# VFS Unreal Project Build Tool v0.19.0") and the new .exe on main, so those are the source.</summary>
    static class Updater
    {
        const string DefaultSource = "https://raw.githubusercontent.com/vfs-sct/VFS_Unreal_Project_Build_Tool/main/";
        public const string ExeName = "VFSUnrealProjectBuildTool.exe";
        public const string UpdatedArg = "--updated";

        /// <summary>Where README.md and the .exe come from. VFSBUILDTOOL_UPDATE_URL replaces it
        /// (another URL or a file:/// folder, for testing).</summary>
        public static string Source
        {
            get
            {
                var s = Environment.GetEnvironmentVariable("VFSBUILDTOOL_UPDATE_URL");
                if (string.IsNullOrEmpty(s)) return DefaultSource;
                return s.EndsWith("/") ? s : s + "/";
            }
        }

        /// <summary>VFSBUILDTOOL_NO_UPDATE=1 turns the check off.</summary>
        public static bool Enabled { get { return Environment.GetEnvironmentVariable("VFSBUILDTOOL_NO_UPDATE") != "1"; } }

        /// <summary>The running .exe (replaceable for tests).</summary>
        public static string ExePath = Application.ExecutablePath;
        static string OldPath { get { return ExePath + ".old"; } }
        public static string NewPath { get { return ExePath + ".new"; } }

        public static Version Current { get { return new Version(Program.Version); } }

        /// <summary>The version in README.md's title, or null.</summary>
        public static Version FromReadme(string readme)
        {
            var m = Regex.Match(readme ?? "", @"^#[^\r\n]*?\bv(\d+\.\d+\.\d+)\b", RegexOptions.Multiline);
            return m.Success ? new Version(m.Groups[1].Value) : null;
        }

        /// <summary>The version a downloaded .exe says it is (Properties > Details), or null.</summary>
        public static Version FileVersionOf(string path)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                var m = Regex.Match(v ?? "", @"^\d+\.\d+\.\d+");
                return m.Success ? new Version(m.Value) : null;
            }
            catch { return null; }
        }

        /// <summary>Puts the downloaded .exe in place of the running one (a running .exe can be renamed,
        /// not overwritten). Puts the old one back if that fails. Throws on failure.</summary>
        public static void Swap()
        {
            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(ExePath, OldPath);
            try { File.Move(NewPath, ExePath); }
            catch
            {
                try { File.Move(OldPath, ExePath); } catch { }
                throw;
            }
        }

        /// <summary>Removes what an update left behind: the previous .exe (still closing for a moment
        /// after the update, so retried in the background) and an unfinished download.</summary>
        public static void CleanUp()
        {
            try { if (File.Exists(NewPath)) File.Delete(NewPath); } catch { }
            if (!File.Exists(OldPath)) return;
            var old = OldPath;
            new System.Threading.Thread(() =>
            {
                for (int i = 0; i < 20 && File.Exists(old); i++)
                {
                    try { File.Delete(old); } catch { System.Threading.Thread.Sleep(500); }
                }
            }) { IsBackground = true }.Start();
        }
    }

    /// <summary>Thin progress bar in the app's colours. Value -1 = busy (sliding block).</summary>
    class DarkProgress : Control
    {
        int value = -1, offset;
        readonly Timer timer = new Timer { Interval = 30 };

        public DarkProgress()
        {
            DoubleBuffered = true;
            Height = 6;
            timer.Tick += (s, e) => { offset = (offset + 6) % (Width + Width / 3); Invalidate(); };
            timer.Start();
        }

        public int Value
        {
            get { return value; }
            set { this.value = Math.Min(100, value); if (value < 0) timer.Start(); else timer.Stop(); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Raised);
            using (var b = new SolidBrush(Theme.Accent))
            {
                if (value >= 0) e.Graphics.FillRectangle(b, 0, 0, Width * value / 100, Height);
                else e.Graphics.FillRectangle(b, offset - Width / 3, 0, Width / 3, Height);
            }
        }

        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>The window shown at start-up while checking for (and installing) a newer version.
    /// Any problem (offline, GitHub down, folder not writable) just opens the current version.</summary>
    class UpdateForm : Form
    {
        const int CheckTimeoutMs = 5000;
        readonly Label status = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        readonly DarkProgress bar = new DarkProgress { Width = 340, Margin = new Padding(0) };

        /// <summary>True when the new version was started and this one should close.</summary>
        public bool Restarted { get; private set; }

        public UpdateForm()
        {
            Text = "VFS Unreal Project Build Tool v" + Program.Version;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(22, 18, 22, 22) };
            status.Text = "Checking for updates...";
            layout.Controls.Add(status);
            layout.Controls.Add(bar);
            Controls.Add(layout);
            Theme.Apply(this);
            status.ForeColor = Theme.Text;
            HandleCreated += (s, e) => Theme.DarkTitleBar(Handle);
            // Closing this window skips the update: stop any download, then the current version opens.
            FormClosing += (s, e) =>
            {
                if (installing) { e.Cancel = true; return; } // the .exe swap takes a moment; let it finish
                closedByUser = true;
                try { if (web != null) web.CancelAsync(); } catch { }
            };
            Shown += async (s, e) =>
            {
                try { await CheckAndUpdate(); }
                catch { } // never stop the app opening
                if (!IsDisposed) { installing = false; Close(); }
            };
        }

        System.Net.WebClient web;
        bool closedByUser, installing;

        /// <summary>Runs one download; cancelled when it takes longer than `limitMs` in total, or when
        /// no data arrives for `stallMs` (a dropped or very slow connection).</summary>
        async System.Threading.Tasks.Task<T> Fetch<T>(Func<System.Net.WebClient, System.Threading.Tasks.Task<T>> start,
                                                      int limitMs, int stallMs, Action<System.Net.DownloadProgressChangedEventArgs> progress = null)
        {
            using (var w = new System.Net.WebClient())
            using (var timer = new Timer { Interval = 500 })
            {
                web = w;
                w.Headers[System.Net.HttpRequestHeader.CacheControl] = "no-cache";
                var started = DateTime.UtcNow;
                var lastData = DateTime.UtcNow;
                w.DownloadProgressChanged += (s, e) => { lastData = DateTime.UtcNow; if (progress != null) progress(e); };
                timer.Tick += (s, e) =>
                {
                    var now = DateTime.UtcNow;
                    if ((now - started).TotalMilliseconds > limitMs || (now - lastData).TotalMilliseconds > stallMs)
                    {
                        timer.Stop();
                        w.CancelAsync();
                    }
                };
                timer.Start();
                try { return await start(w); }
                finally { web = null; }
            }
        }

        async System.Threading.Tasks.Task CheckAndUpdate()
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
            Version latest;
            try
            {
                latest = Updater.FromReadme(await Fetch(w => w.DownloadStringTaskAsync(new Uri(Updater.Source + "README.md")),
                                                        CheckTimeoutMs, CheckTimeoutMs));
            }
            catch { return; } // offline, timed out, GitHub unreachable: carry on with this version
            if (closedByUser || latest == null || latest <= Updater.Current) return;

            status.Text = "Updating to v" + latest + " (you have v" + Program.Version + ")...";
            bar.Value = 0;
            string problem = null;
            try
            {
                await Fetch(async w => { await w.DownloadFileTaskAsync(new Uri(Updater.Source + Updater.ExeName), Updater.NewPath); return true; },
                            5 * 60 * 1000, 15000,
                            e => bar.Value = e.TotalBytesToReceive > 0 ? (int)(e.BytesReceived * 100 / e.TotalBytesToReceive) : -1);
                if (closedByUser) throw new OperationCanceledException();
                bar.Value = 100;
                // Only install a real .exe that's newer than this one (the README may be pushed ahead of it).
                var got = Updater.FileVersionOf(Updater.NewPath);
                if (got == null || got <= Updater.Current)
                {
                    File.Delete(Updater.NewPath);
                    return;
                }
                status.Text = "Installing v" + got + "...";
                Refresh();
                installing = true;
                Updater.Swap();
                Process.Start(new ProcessStartInfo(Updater.ExePath, Updater.UpdatedArg) { UseShellExecute = false });
                Restarted = true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(Updater.NewPath)) File.Delete(Updater.NewPath); } catch { }
                // Lost connection, timed out or closed by the user: just open this version, no popup.
                if (closedByUser || ex is System.Net.WebException || ex is OperationCanceledException) return;
                problem = ex is UnauthorizedAccessException
                    ? "This folder can't be written to, so the update can't be installed here. Move the app to a folder you own (e.g. Documents) or download the new version from GitHub."
                    : ex.Message;
            }
            finally { installing = false; }
            if (problem != null)
            {
                Hide();
                DarkDialog.Show(null, "Update failed",
                    "Couldn't update to v" + latest + ": " + problem + "\n\nOpening v" + Program.Version + " instead.");
            }
        }
    }

    class MainForm : Form
    {
        static readonly Regex ValidName = new Regex("^[A-Za-z][A-Za-z0-9_]*$");

        // Names that pass ValidName but still break a rename (keep in step with the script's new_name_problem).
        static readonly HashSet<string> WindowsReserved = new HashSet<string>(
            new[] { "CON", "PRN", "AUX", "NUL" }
                .Concat(Enumerable.Range(1, 9).Select(i => "COM" + i))
                .Concat(Enumerable.Range(1, 9).Select(i => "LPT" + i)), StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> CppKeywords = new HashSet<string>((
            "alignas alignof and and_eq asm auto bitand bitor bool break case catch char char8_t char16_t char32_t class " +
            "compl concept const consteval constexpr constinit const_cast continue co_await co_return co_yield decltype " +
            "default delete do double dynamic_cast else enum explicit export extern false float for friend goto if inline " +
            "int long mutable namespace new noexcept not not_eq nullptr operator or or_eq private protected public register " +
            "reinterpret_cast requires return short signed sizeof static static_assert static_cast struct switch template " +
            "this thread_local throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t " +
            "while xor xor_eq").Split(' '), StringComparer.Ordinal); // C++ is case-sensitive: "Class" is fine

        /// <summary>Why a well-formed new name still can't be used, or null.</summary>
        static string NewNameProblem(string name, string old)
        {
            if (WindowsReserved.Contains(name))
                return "\"" + name + "\" is a reserved name in Windows (files can't be called that). Pick another name.";
            if (CppKeywords.Contains(name))
                return "\"" + name + "\" is a C++ keyword, so the project wouldn't build. Pick another name.";
            if (old.Length > 0 && string.Equals(name, old, StringComparison.OrdinalIgnoreCase) && name != old)
                return "Only the capitalisation changes. Windows treats \"" + name + "\" and \"" + old +
                       "\" as the same file name, so it can't be renamed safely. Pick a different name.";
            return null;
        }
        const string KeepEngine = "Keep current";

        readonly TextBox projectBox = new TextBox();
        readonly Button projectBrowse = new Button { Text = "Browse...", AutoSize = true };
        // Unreal recommends project names of 20 characters or fewer.
        readonly TextBox nameBox = new TextBox { MaxLength = 20 };
        readonly ComboBox engineBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label engineNote = new Label { AutoSize = true, ForeColor = Theme.Warn };
        readonly ComboBox vsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label vsNote = new Label { AutoSize = true, ForeColor = Theme.Error, MaximumSize = new Size(700, 0) };
        // Under Project when there is no project: a grey "pick a .uproject" hint while the box is empty, or red when the
        // remembered/typed project can't be found. Hidden once a project is picked.
        readonly Label projectNote = new Label { AutoSize = true, ForeColor = Theme.Error, MaximumSize = new Size(700, 0), Visible = false };
        // What the app does. Order is the order in the Action dropdown; Build Solution is the default.
        enum Act { Build, Regenerate, Package, Upgrade, Rename } // settings store these names, so the order can change
        static readonly string[] ActionNames = { "Build Solution", "Regenerate Visual Studio project files", "Package Build", "Upgrade Project Version", "Rename Project" };
        // Shown in grey under the Action dropdown, in the same order as Act.
        static readonly string[] ActionDescriptions =
        {
            "Compiles the project's C++ code for the editor, like Build in Visual Studio.",
            "Deletes the generated files (.vs, Binaries, Intermediate, DerivedDataCache, .sln), makes fresh Visual Studio " +
                "project files, then builds.",
            "Builds, cooks the content and packages a Windows game into the Build location.",
            "Upgrades the project to a newer installed Unreal Engine version, updates its build settings, regenerates the " +
                "project files and builds. Can't be undone: commit first.",
            "Renames the project (files, modules, code and config) and adds redirects so Blueprints and levels keep " +
                "working, then builds and resaves assets. Commit first.",
        };
        readonly Label actionNote = new Label { AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(620, 0) };
        // Under New name (Rename Project only): what a name may contain, matching ValidName and NewNameProblem.
        readonly Label nameNote = new Label
        {
            AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(620, 0),
            Text = "Letters, numbers and underscores only, starting with a letter. No spaces or symbols (such as - . ! @ #). " +
                   "Not allowed: Windows reserved names (CON, PRN, AUX, NUL, COM1-9, LPT1-9), C++ keywords (class, int, ...), " +
                   "or only changing the capitalisation.",
        };
        static readonly string[] ActionVerbs = { "Build", "Regenerate", "Package", "Upgrade", "Rename" };
        readonly ComboBox actionBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        Label nameLabel;
        Act runAction;              // the action of the run in progress (or the last one)
        Label engineLabel;          // the Engine version row is shown for Upgrade Project Version only
        string upgradedTo;          // the engine an Upgrade run switches to, e.g. "UE 5.8"
        bool runRebuild;            // whether that run was a rebuild
        // Build Solution and Package Build: clean, then build everything (Unreal's Rebuild). Off by default.
        readonly CheckBox rebuildBox = new DarkCheck { Text = "Rebuild Solution", AutoSize = true, Checked = false };
        readonly Label rebuildNote = new Label { AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(620, 0),
            Text = "Deletes the compiled code and builds everything from scratch. Slower; use it when a build gives " +
                   "strange errors or C++ changes don't take effect." };

        // "On Finish Success" choices (see WhenDoneArgs).
        const string DoNothing = "Do nothing";
        const string OpenEditor = "Open in Unreal Editor when done";
        const string LaunchExe = "Launch .exe";
        static readonly string[] BasicWhenDone = { DoNothing, OpenEditor };
        static readonly string[] PackageWhenDone = { DoNothing, OpenEditor, LaunchExe };
        const int PackageWhenDoneDefault = 2; // Launch .exe
        readonly ComboBox whenDoneBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };

        // Build field (Package Build only): which configuration to package. Development is the default.
        static readonly string[] BuildConfigs = { "Development", "Shipping" };
        readonly ComboBox buildConfigBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        Label buildConfigLabel;
        // Package Build only: where the packaged game goes. Filled with <project>\Packaged when a project is picked.
        readonly TextBox buildLocationBox = new TextBox();
        readonly Button buildLocationBrowse = new Button { Text = "Browse...", AutoSize = true };
        Label buildLocationLabel;
        string autoBuildLocation;   // the location last filled in automatically (replaced when the project changes)
        // Package Build only: ticked = clean cook (delete old cooked data, cook everything);
        // unticked = only recook what changed.
        // Shown in grey under the Build dropdown for the selected configuration (same order as BuildConfigs).
        static readonly string[] BuildConfigDescriptions =
        {
            "For testing purposes; keeps the console, print strings, visible draw traces, and Unreal's debug commands.\n" +
                "Ideal for Milestone reviews so instructors can debug through your game if required.",
            "For public consumption; removes console, print strings, visible draw traces, and Unreal's debug commands.",
        };
        readonly Label buildConfigNote = new Label { AutoSize = true, ForeColor = Theme.Warn, MaximumSize = new Size(620, 0),
                                                      Text = BuildConfigDescriptions[0] };
        // Red, under the Development description: a Development build isn't for the public.
        readonly Label buildConfigWarning = new Label { AutoSize = true, ForeColor = Theme.Error, MaximumSize = new Size(620, 0),
                                                        Text = "USE SHIPPING FOR PITCH N PLAY / STEAM / EPIC / ITCH.IO / VFS ARCADE BUILDS!" };
        readonly CheckBox cookContentBox = new DarkCheck { Text = "Clean cook content", AutoSize = true, Checked = false };
        // Clean cook content is remembered per Build configuration (the user's choice, 13 Nov): switching between
        // Development and Shipping brings back each one's own tick. Shipping starts ticked.
        static readonly Dictionary<string, bool> CleanCookDefaults = new Dictionary<string, bool> { { "Development", false }, { "Shipping", true } };
        Dictionary<string, bool> cleanCookByConfig = new Dictionary<string, bool>(CleanCookDefaults);
        bool showingCleanCook; // true while the box is being set from cleanCookByConfig (not a user click)
        readonly Label cookContentNote = new Label { AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(620, 0),
            Text = "Deletes the old cooked content and cooks every asset from scratch. Slower to build when on. When off, only changed " +
                   "assets are recooked. Use it if the packaged game shows old or missing content, or if packaging it for public " +
                   "consumption for the first time (Pitch n Play, Steam/Epic store, etc)." };
        readonly List<Panel> separators = new List<Panel>(); // thin lines between groups of fields
        string packageDir;          // where this rename's packaged build goes (asked when Rename is clicked)
        string packagedFolder;      // set after a successful package, for Open folder
        readonly Button renameButton = new Button { Text = "Rename", AutoSize = true };
        readonly Button cancelButton = new Button { Text = "Cancel", AutoSize = true, Visible = false };
        readonly Button openButton = new Button { Text = "Open folder", AutoSize = true, Enabled = false };
        readonly Button previewButton = new Button { Text = "Preview changes", AutoSize = true };
        readonly Button saveLogButton = new Button { Text = "Save Output Log", AutoSize = true, Enabled = false };
        readonly Button copyLogButton = new Button { Text = "Copy Output Log", AutoSize = true, Enabled = false };
        readonly RichTextBox log = new RichTextBox(); // rich text so the final "Check in Unreal Editor" section can be yellow
        readonly Label status = new Label { AutoSize = true };

        string currentAssociation = "";
        string lastResultFolder;
        Process running;
        string runScript;          // this run's private copy of the script (deleted afterwards)
        int lastEngineIndex;       // engine choice to go back to when a downgrade is refused
        bool settingEngine;        // engine choice is being set by code, not by the user
        bool allowCommonName;      // the user confirmed renaming a project with a common-word name
        int runId;                 // counts runs, so output from an earlier run can't reach the new log
        bool inEditorChecks;       // the log has reached the final "Check in Unreal Editor" list (shown yellow)

        // Dialogs, replaceable so tests can answer them.
        internal static Action<IWin32Window, string, string> Notify = (o, t, m) => DarkDialog.Show(o, t, m);
        internal static Func<IWin32Window, string, string, string, string, bool> AskUser =
            (o, t, m, yes, no) => DarkDialog.Ask(o, t, m, true, yes, no);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        const int EM_SETCUEBANNER = 0x1501;

        // Sound effects, replaceable so tests can see which one played.
        internal static Action<Sfx.Kind> PlaySound = Sfx.Play;
        bool runIsPreview, cancellable, cancelled;
        List<Engines.VisualStudio> visualStudios = new List<Engines.VisualStudio>();
        List<string> windowsSdks = new List<string>();
        readonly Dictionary<string, Engines.Requirements> requirements = new Dictionary<string, Engines.Requirements>(StringComparer.OrdinalIgnoreCase);

        public MainForm()
        {
            Text = "VFS Unreal Project Build Tool v" + Program.Version;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(760, 680);
            Size = new Size(920, 820);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            AddRow(grid, "Project (.uproject)", Frame(projectBox), projectBrowse);
            projectNote.Margin = new Padding(3, 0, 3, 6);
            AddRow(grid, "", projectNote, null);
            AddSeparator(grid);

            actionBox.Items.AddRange(ActionNames);
            actionBox.SelectedIndex = (int)Act.Regenerate; // the default action (until settings restore the last one)
            actionBox.Width = 260;
            actionBox.Margin = new Padding(3);
            AddRow(grid, "Action", Host(actionBox), null);
            actionNote.Margin = new Padding(3, 0, 3, 6);
            AddRow(grid, "", actionNote, null);
            rebuildBox.Margin = new Padding(3, 6, 3, 0);
            AddRow(grid, "", rebuildBox, null);
            rebuildNote.Margin = new Padding(3, 2, 3, 6);
            AddRow(grid, "", rebuildNote, null);

            buildConfigBox.Items.AddRange(BuildConfigs);
            buildConfigBox.SelectedIndexChanged += (s, e) =>
            {
                buildConfigNote.Text = BuildConfigDescriptions[Math.Max(0, buildConfigBox.SelectedIndex)];
                UpdateBuildConfigWarning();
                ShowCleanCook();
            };
            cookContentBox.CheckedChanged += (s, e) => { if (!showingCleanCook) cleanCookByConfig[BuildConfigName] = cookContentBox.Checked; };
            buildConfigBox.SelectedIndex = 0; // Development
            buildConfigBox.Width = 260;
            buildConfigBox.Margin = new Padding(3);
            buildConfigLabel = AddRow(grid, "Build", Host(buildConfigBox), null);
            buildConfigNote.Margin = new Padding(3, 0, 3, 0); // the red line below sits right under it
            AddRow(grid, "", buildConfigNote, null);
            buildConfigWarning.Margin = new Padding(3, 0, 3, 2);
            AddRow(grid, "", buildConfigWarning, null);
            cookContentBox.Margin = new Padding(3, 6, 3, 0);
            AddRow(grid, "", cookContentBox, null);
            cookContentNote.Margin = new Padding(3, 2, 3, 6);
            AddRow(grid, "", cookContentNote, null);
            buildLocationLabel = AddRow(grid, "Build location", Frame(buildLocationBox), buildLocationBrowse);

            // New name belongs to Rename Project, so it sits in the Action group.
            // Grey hint shown while the box is empty (also while it has focus).
            nameBox.HandleCreated += (s, e) => SendMessage(nameBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Maximum 20 characters");
            buildLocationBox.HandleCreated += (s, e) =>
                SendMessage(buildLocationBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Choose a folder for the packaged game");
            nameLabel = AddRow(grid, "New name", Frame(nameBox), null);
            nameNote.Margin = new Padding(3, 0, 3, 6);
            AddRow(grid, "", nameNote, null);

            // A thin line under the Action group (Action, plus Rebuild Solution / Build / Clean cook content /
            // New name, depending on the action).
            AddSeparator(grid);

            var enginePanel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), WrapContents = false };
            engineBox.Width = 260;
            enginePanel.Controls.Add(Host(engineBox));
            engineNote.Margin = new Padding(8, 6, 0, 0);
            enginePanel.Controls.Add(engineNote);
            engineLabel = AddRow(grid, "Engine version", enginePanel, null); // Upgrade Project Version only

            vsBox.Width = 360;
            vsBox.Margin = new Padding(3);
            AddRow(grid, "Visual Studio", Host(vsBox), null);
            vsNote.Margin = new Padding(3, 0, 3, 6);
            AddRow(grid, "", vsNote, null);

            // And one above On Finish Success.
            AddSeparator(grid);

            whenDoneBox.Width = 520;
            whenDoneBox.Margin = new Padding(3);
            AddRow(grid, "On Finish Success", Host(whenDoneBox), null);
            AddSeparator(grid);

            // Buttons start at the left edge, in line with the field labels, with a line under them.
            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 4), WrapContents = false };
            foreach (var b in new[] { previewButton, renameButton, cancelButton, openButton, saveLogButton, copyLogButton })
            {
                b.Margin = new Padding(0, 3, 6, 3);
                buttons.Controls.Add(b);
            }
            int buttonRow = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(buttons, 0, buttonRow);
            grid.SetColumnSpan(buttons, 3);
            AddSeparator(grid);

            log.Multiline = true;
            log.DetectUrls = false;
            log.ReadOnly = true;
            log.ScrollBars = RichTextBoxScrollBars.Both;
            log.WordWrap = false;
            log.Font = new Font("Consolas", 9f);
            log.BackColor = SystemColors.Window;
            log.Dock = DockStyle.Fill;
            // "Output Log" heading on the left, just above the box.
            var logLabel = new Label { Text = "Output Log", AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(logLabel, 0, grid.RowCount);
            grid.SetColumnSpan(logLabel, 3);
            grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(log, 0, grid.RowCount);
            grid.SetColumnSpan(log, 3);
            grid.RowCount++;

            grid.Controls.Add(status, 0, grid.RowCount);
            grid.SetColumnSpan(status, 3);
            grid.RowCount++;

            Controls.Add(grid);

            projectBrowse.Click += (s, e) => BrowseProject();
            buildLocationBrowse.Click += (s, e) =>
            {
                var start = buildLocationBox.Text.Trim().Length > 0 ? buildLocationBox.Text.Trim()
                          : File.Exists(ProjectPath) ? Path.GetDirectoryName(ProjectPath) : "";
                var folder = AskFolder(this, "Choose where to save the packaged build", start);
                if (folder != null) buildLocationBox.Text = folder;
            };
            buildLocationBox.TextChanged += (s, e) => UpdateState();
            projectBox.TextChanged += (s, e) => ProjectChanged();
            nameBox.TextChanged += (s, e) => UpdateState();
            engineBox.SelectedIndexChanged += (s, e) => EngineChanged();
            vsBox.SelectedIndexChanged += (s, e) => UpdateState();
            renameButton.Click += (s, e) => StartAction();
            actionBox.SelectedIndexChanged += (s, e) => ActionChanged();
            rebuildBox.CheckedChanged += (s, e) => { UpdateMainButtonText(); UpdateState(); };

            // Hover help for the two checkboxes.
            var tips = Theme.DarkToolTip();
            FormClosed += (s, e) => tips.Dispose();
            tips.SetToolTip(rebuildBox,
                "Deletes the project's compiled code, then compiles everything from scratch.\n\n" +
                "Slower than a normal build. Use it when a build gives strange errors, or changes " +
                "to your C++ don't seem to take effect. With Package Build, the packaged game's " +
                "code is rebuilt from scratch too.");
            tips.SetToolTip(cookContentBox,
                "Deletes the old cooked content, then cooks every asset from scratch before packaging.\n\n" +
                "Slower. When off, only assets that changed since the last cook are recooked " +
                "(faster). Turn it on if the packaged game shows old or missing content, or " +
                "after renaming the project.");
            cancelButton.Click += (s, e) => CancelRun();
            previewButton.Click += (s, e) => Run(true);
            saveLogButton.Click += (s, e) => SaveLog();
            copyLogButton.Click += (s, e) => CopyLog();
            log.TextChanged += (s, e) => UpdateSaveLogButton();
            openButton.Click += (s, e) =>
            {
                // After packaging, open the packaged build; otherwise the project.
                var folder = OpenFolderTarget();
                if (folder != null) Process.Start("explorer.exe", "\"" + folder + "\"");
            };
            DragEnter += (s, e) => { if (DroppedProject(e) != null) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => { var p = DroppedProject(e); if (p != null && projectBox.Enabled) projectBox.Text = p; };
            FormClosing += (s, e) =>
            {
                if (running == null || running.HasExited) return;
                if (!cancellable)
                {
                    DarkDialog.Show(this, Text, "Please wait - files are being renamed or saved right now. " +
                        "Stopping at this point could leave the project half-changed.");
                    e.Cancel = true;
                }
                else if (DarkDialog.Ask(this, Text, "A " + Verb(runAction, runRebuild).ToLower() + " is still running. Stop it and close?",
                                        true, "Stop and close", "Keep running"))
                {
                    cancelled = true; // stopped by the user: no failure sound or popup
                    KillRun();
                }
                else
                    e.Cancel = true;
            };

            // Names double as UI Automation ids (used by accessibility tools and tests).
            projectBox.Name = "projectBox"; nameBox.Name = "nameBox"; actionBox.Name = "actionBox"; buildConfigBox.Name = "buildConfigBox"; cookContentBox.Name = "cookContentBox"; rebuildBox.Name = "rebuildBox";
            engineBox.Name = "engineBox"; vsBox.Name = "vsBox"; whenDoneBox.Name = "whenDoneBox"; log.Name = "log"; status.Name = "status";
            previewButton.Name = "previewButton"; renameButton.Name = "renameButton"; openButton.Name = "openButton";
            cancelButton.Name = "cancelButton"; vsNote.Name = "vsNote"; saveLogButton.Name = "saveLogButton"; copyLogButton.Name = "copyLogButton";

            Theme.Apply(this);
            Theme.StyleButton(renameButton, true);
            foreach (var line in separators) line.BackColor = Theme.Border; // after Apply, which paints panels with the background
            foreach (var f in frames)
            {
                f.BackColor = Theme.Surface;                       // Apply painted it like the window
                ((TextBox)f.Controls[0]).BorderStyle = BorderStyle.None; // the frame draws the border
            }
            // Fields and dropdowns as tall as the buttons, once the real (DPI-scaled) sizes are known.
            Load += (s, e) => MatchFieldHeights();
            Shown += (s, e) => MatchFieldHeights();
            status.ForeColor = Theme.Muted;
            HandleCreated += (s, e) => Theme.DarkTitleBar(Handle);

            vsNote.ForeColor = Theme.Error;

            FillEngines();
            FillVisualStudios();
            PickDefaultVisualStudio();
            ActionChanged(); // fills On Finish Success, shows the right fields, updates the state
            LoadSettings();  // last-used action and each action's last inputs
            FormClosed += (s, e) => SaveFields(CurrentAction);
        }

        Act CurrentAction { get { return (Act)Math.Max(0, actionBox.SelectedIndex); } }

        bool Rebuilding { get { return rebuildBox.Checked && (CurrentAction == Act.Build || CurrentAction == Act.Package); } }

        /// <summary>Verb for an action, e.g. "Build", "Rebuild", "Package", "Rename".</summary>
        static string Verb(Act act, bool rebuild) { return act == Act.Build && rebuild ? "Rebuild" : ActionVerbs[(int)act]; }

        void UpdateMainButtonText() { renameButton.Text = Verb(CurrentAction, Rebuilding); }

        /// <summary>Show the fields and On Finish Success choices that belong to the chosen Action.</summary>
        void ActionChanged()
        {
            var act = CurrentAction;
            actionNote.Text = ActionDescriptions[(int)act];
            // Save the action we're leaving first, while its own values are still on screen.
            if (settingsLoaded && act != shownAction) SaveFields(shownAction);
            bool rename = act == Act.Rename;
            nameBox.Parent.Visible = rename; // the frame around the box
            if (nameLabel != null) nameLabel.Visible = rename;
            nameNote.Visible = rename;
            previewButton.Visible = rename;
            // A rename or a regenerate deletes Binaries/Intermediate, so it always builds from scratch.
            rebuildBox.Visible = rebuildNote.Visible = act == Act.Build || act == Act.Package;
            UpdateMainButtonText();
            bool package = act == Act.Package;
            buildConfigBox.Parent.Visible = package; // its host panel
            buildConfigNote.Visible = package;
            UpdateBuildConfigWarning();
            cookContentBox.Visible = cookContentNote.Visible = package;
            buildLocationBox.Parent.Visible = package; // its frame
            buildLocationBrowse.Visible = package;
            if (buildLocationLabel != null) buildLocationLabel.Visible = package;
            if (buildConfigLabel != null) buildConfigLabel.Visible = package;
            // Engine version belongs to Upgrade Project Version; every other action uses the project's engine.
            bool upgrade = act == Act.Upgrade;
            engineBox.Parent.Parent.Visible = upgrade; // host panel -> engine row panel
            if (engineLabel != null) engineLabel.Visible = upgrade;

            // Each action starts on its own defaults (Project and Visual Studio carry over)...
            FillWhenDone(package ? PackageWhenDoneDefault : Array.IndexOf(BasicWhenDone, OpenEditor));
            rebuildBox.Checked = false;
            cleanCookByConfig = new Dictionary<string, bool>(CleanCookDefaults);
            buildConfigBox.SelectedIndex = 0; // Development
            ShowCleanCook();
            if (DefaultBuildLocation() != null) { autoBuildLocation = DefaultBuildLocation(); buildLocationBox.Text = autoBuildLocation; }
            PickActionEngine();
            // ...then gets back what it had last time, if anything.
            if (settingsLoaded) RestoreFields(act);
            shownAction = act;
        }

        /// <summary>Upgrade Project Version: the newest installed engine that's an upgrade for this project.
        /// Every other action: "Keep current" (they always build with the project's own engine).</summary>
        void PickActionEngine()
        {
            if (CurrentAction != Act.Upgrade) { SetEngineIndex(0); EngineChanged(); return; }
            for (int i = 1; i < engineBox.Items.Count; i++) // newest first
                if (DowngradeMessage(i) == null && (string)engineBox.Items[i] != "UE " + currentAssociation)
                {
                    SetEngineIndex(i);
                    EngineChanged();
                    return;
                }
            SetEngineIndex(0);
            EngineChanged();
        }

        // ------------------------------------------------------------------ remembered inputs (per action)

        internal static string SettingsFile = Path.Combine( // replaceable so tests never touch the real file
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VFSUnrealProjectBuildTool", "settings.json");
        Dictionary<string, Dictionary<string, string>> savedFields = new Dictionary<string, Dictionary<string, string>>();
        string savedProject; // the project, shared by every action
        bool settingsLoaded;  // false until LoadSettings ran (so start-up doesn't save half-filled fields)
        Act shownAction;      // the action whose fields are on screen

        /// <summary>Reads settings.json and shows the last-used action with its saved fields.</summary>
        void LoadSettings()
        {
            string last = null;
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var data = new System.Web.Script.Serialization.JavaScriptSerializer()
                        .Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsFile));
                    object v;
                    if (data.TryGetValue("LastAction", out v)) last = v as string;
                    if (data.TryGetValue("Project", out v)) savedProject = v as string;
                    if (data.TryGetValue("Actions", out v) && v is Dictionary<string, object>)
                        foreach (var a in (Dictionary<string, object>)v)
                        {
                            var fields = a.Value as Dictionary<string, object>;
                            if (fields != null)
                                savedFields[a.Key] = fields.ToDictionary(f => f.Key, f => f.Value as string ?? "");
                        }
                }
            }
            catch { } // unreadable settings: start with the defaults
            // One project for every action. Settings from before 0.20.4 kept one per action: use the last-used
            // action's (or any saved one).
            if (string.IsNullOrEmpty(savedProject))
            {
                Dictionary<string, string> f;
                string p;
                if (last != null && savedFields.TryGetValue(last, out f) && f.TryGetValue("Project", out p) && p.Length > 0) savedProject = p;
                else savedProject = savedFields.Values.Select(x => { string q; return x.TryGetValue("Project", out q) ? q : ""; })
                                                     .FirstOrDefault(q => q.Length > 0);
            }
            if (!string.IsNullOrEmpty(savedProject) && savedProject != projectBox.Text) projectBox.Text = savedProject;
            shownAction = CurrentAction;
            settingsLoaded = true;
            Act lastAct;
            // Show the starting action's own saved fields first: switching to the last-used action saves the
            // action being left, and it must save what was remembered, not the still-empty start-up fields.
            RestoreFields(CurrentAction);
            if (last != null && Enum.TryParse(last, out lastAct) && lastAct != CurrentAction)
                actionBox.SelectedIndex = (int)lastAct; // ActionChanged saves the start action, restores this one
        }

        /// <summary>Remembers the on-screen fields for <paramref name="act"/> and writes settings.json.</summary>
        void SaveFields(Act act)
        {
            if (!settingsLoaded) return;
            var vs = vsBox.SelectedItem as Engines.VisualStudio;
            savedProject = projectBox.Text; // shared by every action
            savedFields[act.ToString()] = new Dictionary<string, string>
            {
                { "Engine", engineBox.SelectedIndex > 0 ? (string)engineBox.SelectedItem : "" },
                { "VisualStudio", vs != null ? vs.Path : "" },
                { "OnFinishSuccess", WhenDone },
                { "Rebuild", rebuildBox.Checked ? "1" : "0" },
                { "Build", buildConfigBox.SelectedItem as string ?? "" },
                { "CleanCookDevelopment", CleanCookFor("Development") ? "1" : "0" },
                { "CleanCookShipping", CleanCookFor("Shipping") ? "1" : "0" },
                { "BuildLocation", buildLocationBox.Text },
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                var json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "LastAction", CurrentAction.ToString() },
                    { "Project", savedProject ?? "" },
                    { "Actions", savedFields },
                });
                File.WriteAllText(SettingsFile, json, new UTF8Encoding(false));
            }
            catch { } // can't save: the app still works, it just won't remember
        }

        /// <summary>Puts back the fields saved for <paramref name="act"/> (anything not saved keeps its current value).</summary>
        void RestoreFields(Act act)
        {
            Dictionary<string, string> f;
            if (!savedFields.TryGetValue(act.ToString(), out f)) return;
            string v;
            // The project isn't per action: it stays as it is on screen (saved once, at the top of settings.json).
            if (act == Act.Upgrade && f.TryGetValue("Engine", out v) && v.Length > 0) // only Upgrade picks an engine
            {
                int i = v.Length == 0 ? 0 : engineBox.Items.IndexOf(v);
                if (i >= 0 && DowngradeMessage(i) == null) SetEngineIndex(i);
                EngineChanged();
            }
            if (f.TryGetValue("VisualStudio", out v) && v.Length > 0)
            {
                int i = visualStudios.FindIndex(x => string.Equals(x.Path, v, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) vsBox.SelectedIndex = i;
            }
            if (f.TryGetValue("OnFinishSuccess", out v) && whenDoneBox.Items.Contains(v)) whenDoneBox.SelectedItem = v;
            if (f.TryGetValue("Rebuild", out v)) rebuildBox.Checked = v == "1";
            if (f.TryGetValue("Build", out v) && buildConfigBox.Items.Contains(v)) buildConfigBox.SelectedItem = v;
            // Clean cook per configuration. Settings from before 0.20.5 had one box for both: it becomes Development's.
            foreach (var config in BuildConfigs)
                if (f.TryGetValue("CleanCook" + config, out v)) cleanCookByConfig[config] = v == "1";
            if (!f.ContainsKey("CleanCookDevelopment") && f.TryGetValue("CleanCook", out v)) cleanCookByConfig["Development"] = v == "1";
            ShowCleanCook();
            // A saved location is kept, except the old <project>/Packaged suggestion, which becomes <project>/Builds.
            if (f.TryGetValue("BuildLocation", out v) && v.Length > 0)
            {
                if (IsOldDefaultLocation(v)) { autoBuildLocation = DefaultBuildLocation(); buildLocationBox.Text = autoBuildLocation; }
                else buildLocationBox.Text = v;
            }
            // Nothing saved (or saved empty) but a project is set: suggest <project>\Builds.
            if (buildLocationBox.Text.Trim().Length == 0 && DefaultBuildLocation() != null)
            {
                autoBuildLocation = DefaultBuildLocation();
                buildLocationBox.Text = autoBuildLocation;
            }
            UpdateState();
        }

        /// <summary>Fill On Finish Success for the current Action and select the choice at <paramref name="select"/>.</summary>
        void FillWhenDone(int select)
        {
            var items = CurrentAction == Act.Package ? PackageWhenDone : BasicWhenDone;
            whenDoneBox.BeginUpdate();
            whenDoneBox.Items.Clear();
            whenDoneBox.Items.AddRange(items);
            whenDoneBox.SelectedIndex = Math.Max(0, Math.Min(select, items.Length - 1));
            whenDoneBox.EndUpdate();
            UpdateState();
        }

        readonly List<Panel> frames = new List<Panel>(); // button-height boxes around the single-line text boxes
        readonly List<Panel> hosts = new List<Panel>();  // button-height panels holding the dropdowns

        /// <summary>Puts a dropdown in a plain panel. The table lays rows out by the panel's size, which
        /// MatchFieldHeights sets to the button height (a combo made taller later isn't re-measured).</summary>
        Panel Host(ComboBox combo)
        {
            var host = new Panel { Size = new Size(combo.Width, combo.Height), Margin = combo.Margin };
            combo.Margin = new Padding(0);
            combo.Dock = DockStyle.Top;
            host.Controls.Add(combo);
            hosts.Add(host);
            return host;
        }

        /// <summary>Puts a text box in a frame as tall as a button, with its text centred vertically
        /// (a plain single-line text box can't be made taller without its text sticking to the top).</summary>
        Panel Frame(TextBox tb)
        {
            var frame = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3), Height = 28 };
            tb.Dock = DockStyle.None;
            frame.Controls.Add(tb);
            Action place = () =>
            {
                int h = tb.PreferredHeight;
                tb.SetBounds(6, Math.Max(0, (frame.ClientSize.Height - h) / 2), Math.Max(10, frame.ClientSize.Width - 12), h);
            };
            frame.Resize += (s, e) => place();
            frame.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, frame.Width - 1, frame.Height - 1);
            };
            frame.Click += (s, e) => tb.Focus();
            frames.Add(frame);
            return frame;
        }

        /// <summary>Makes the text-box frames and the dropdowns exactly as tall as the buttons.</summary>
        void MatchFieldHeights()
        {
            int h = projectBrowse.Height;
            if (h <= 0) return;
            foreach (var f in frames) f.Height = h;
            foreach (var host in hosts)
            {
                var combo = (ComboBox)host.Controls[0];
                int diff = h - combo.Height;
                if (diff != 0) combo.ItemHeight = Math.Max(10, combo.ItemHeight + diff);
                host.Height = h; // the rows follow the host panel's size (they ignore a combo grown later)
            }
        }

        /// <summary>Adds a full-width 1 px line as its own row (coloured after the theme is applied).</summary>
        void AddSeparator(TableLayoutPanel grid)
        {
            var line = new Panel { Height = 1, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8) };
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(line, 0, row);
            grid.SetColumnSpan(line, 3);
            separators.Add(line);
        }

        /// <summary>Adds a row; returns its label (null when there is none) so the row can be hidden.</summary>
        static Label AddRow(TableLayoutPanel grid, string label, Control field, Control extra)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label l = null;
            if (label.Length > 0)
            {
                // Fills the row's height with its text centred, so it lines up with the field next to it.
                l = new Label { Text = label, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                                Margin = new Padding(0, 3, 12, 3) };
                grid.Controls.Add(l, 0, row);
            }
            grid.Controls.Add(field, 1, row);
            if (extra != null) grid.Controls.Add(extra, 2, row);
            else grid.SetColumnSpan(field, 2);
            return l;
        }

        static string DroppedProject(DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length != 1) return null;
            var f = files[0];
            if (f.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)) return f;
            if (Directory.Exists(f)) return Directory.GetFiles(f, "*.uproject").FirstOrDefault();
            return null;
        }

        // ------------------------------------------------------------------ UI state

        string ProjectPath
        {
            get
            {
                var p = projectBox.Text.Trim().Trim('"');
                if (Directory.Exists(p)) p = Directory.GetFiles(p, "*.uproject").FirstOrDefault() ?? p;
                return p;
            }
        }

        /// <summary>Why there's no project to work on: nothing picked yet, a remembered project that has
        /// been moved or deleted, or a folder with no .uproject in it.</summary>
        string MissingProjectMessage()
        {
            var typed = projectBox.Text.Trim().Trim('"');
            if (typed.Length == 0) return "Pick a .uproject to start (or drag one onto this window).";
            if (Directory.Exists(typed))
                return "There's no .uproject in this folder. Pick the project's .uproject file with Browse.";
            return "Project not found. It may have been moved or deleted. Pick it again with Browse.";
        }

        string OldName { get { return File.Exists(ProjectPath) ? Path.GetFileNameWithoutExtension(ProjectPath) : ""; } }

        void FillVisualStudios()
        {
            visualStudios = Engines.VisualStudios();
            windowsSdks = Engines.WindowsSdks();
            vsBox.Items.Clear();
            foreach (var vs in visualStudios) vsBox.Items.Add(vs);
            if (vsBox.Items.Count == 0) vsBox.Items.Add("No Visual Studio found");
            vsBox.SelectedIndex = 0; // newest first
        }

        /// <summary>Select the newest Visual Studio that can build for the chosen engine.</summary>
        void PickDefaultVisualStudio()
        {
            var req = SelectedRequirements();
            if (req == null || visualStudios.Count == 0) return;
            for (int i = 0; i < visualStudios.Count; i++)
                if (req.ProblemWith(visualStudios[i]) == null) { vsBox.SelectedIndex = i; return; }
        }

        /// <summary>The engine the follow-up steps will use: the one picked in the dropdown, or the
        /// project's current one. Null folder if it isn't installed.</summary>
        void SelectedEngine(out string dir, out string name)
        {
            dir = null; name = null;
            if (CurrentAction == Act.Upgrade && engineBox.SelectedIndex > 0) // only Upgrade switches engines
            {
                var ver = ((string)engineBox.SelectedItem).Substring(3);
                Engines.Installed().TryGetValue(ver, out dir);
                name = "UE " + ver;
            }
            else if (File.Exists(ProjectPath))
            {
                dir = Engines.EngineDirFor(currentAssociation);
                name = Regex.IsMatch(currentAssociation, @"^\d+\.\d+") ? "UE " + currentAssociation : "this project's engine";
            }
            else
            {
                var newest = Engines.Installed().LastOrDefault();
                if (newest.Value != null) { dir = newest.Value; name = "UE " + newest.Key; }
            }
        }

        Engines.Requirements SelectedRequirements()
        {
            string dir, name;
            SelectedEngine(out dir, out name);
            if (dir == null) return null;
            Engines.Requirements r;
            if (!requirements.TryGetValue(dir, out r)) requirements[dir] = r = Engines.Requirements.Load(dir, name);
            return r;
        }

        /// <summary>Why the build steps can't run with the current engine / Visual Studio, or null.</summary>
        string BuildBlocker()
        {
            string dir, name;
            SelectedEngine(out dir, out name);
            if (dir == null)
            {
                if (Engines.Installed().Count == 0 && Engines.SourceBuilds().Count == 0)
                    return "No Unreal Engine is installed on this PC, so the renamed project can't be built. Install Unreal Engine first.";
                return "This project's engine (" + Short(currentAssociation) + ") isn't installed on this PC. " +
                       (CurrentAction == Act.Upgrade ? "Pick an installed engine under Engine version."
                                                     : "Use the Upgrade Project Version action to move it to an installed engine.");
            }
            if (visualStudios.Count == 0)
                return "No Visual Studio is installed. Install Visual Studio 2022 or newer with the \"Game development with C++\" workload.";
            var req = SelectedRequirements();
            var vs = vsBox.SelectedItem as Engines.VisualStudio;
            if (req != null && vs != null)
                return req.ProblemWith(vs) ?? req.SdkProblem(windowsSdks);
            return null;
        }

        void FillEngines()
        {
            engineBox.Items.Clear();
            engineBox.Items.Add(KeepEngine);
            foreach (var v in Engines.Installed().Keys.Reverse()) engineBox.Items.Add("UE " + v);
            engineBox.SelectedIndex = 0;
        }

        /// <summary>Why switching to the engine at `index` is refused (a downgrade), or null.</summary>
        string DowngradeMessage(int index)
        {
            if (index <= 0 || index >= engineBox.Items.Count || !File.Exists(ProjectPath)) return null;
            var target = Engines.VersionOf(((string)engineBox.Items[index]).Substring(3));
            var current = Engines.VersionOf(currentAssociation);
            if (target == null || current == null || target >= current) return null;
            return "Can't switch \"" + OldName + "\" from UE " + current + " to the older UE " + target + ".\n\n" +
                   "Downgrading isn't supported: assets saved by a newer engine can't be opened by an older one, " +
                   "so the project would break. Pick UE " + current + " or newer, or keep the current engine.";
        }

        void SetEngineIndex(int index)
        {
            settingEngine = true;
            engineBox.SelectedIndex = index;
            settingEngine = false;
            lastEngineIndex = index;
        }

        void EngineChanged()
        {
            if (!settingEngine)
            {
                var msg = DowngradeMessage(engineBox.SelectedIndex);
                if (msg != null)
                {
                    SetEngineIndex(DowngradeMessage(lastEngineIndex) == null ? lastEngineIndex : 0);
                    Notify(this, "Engine downgrade blocked", msg);
                    return; // SetEngineIndex already refreshed everything
                }
                lastEngineIndex = engineBox.SelectedIndex;
            }
            UpdateEngineNote();
            PickDefaultVisualStudio();
            UpdateState();
        }

        /// <summary>What Open folder opens: the packaged build after a successful Package, otherwise the
        /// project's folder. Null (button greyed out) when no project is chosen.</summary>
        string OpenFolderTarget()
        {
            if (!File.Exists(ProjectPath)) return null;
            if (packagedFolder != null && Directory.Exists(packagedFolder)) return packagedFolder;
            return Path.GetDirectoryName(ProjectPath);
        }

        /// <summary>&lt;project folder&gt;\Builds, the Build location used when none is chosen; null without a project.</summary>
        string DefaultBuildLocation()
        {
            return File.Exists(ProjectPath) ? Path.Combine(Path.GetDirectoryName(ProjectPath), "Builds") : null;
        }

        /// <summary>The Build location to package into (Package is greyed out while it's empty).</summary>
        string ChosenBuildLocation()
        {
            var typed = buildLocationBox.Text.Trim();
            return typed.Length > 0 ? typed : null;
        }

        /// <summary>True for &lt;project&gt;\Packaged, the suggestion before 6 Nov (it may be in saved settings).</summary>
        bool IsOldDefaultLocation(string path)
        {
            return File.Exists(ProjectPath) &&
                   string.Equals(path.TrimEnd('\\'), Path.Combine(Path.GetDirectoryName(ProjectPath), "Packaged"),
                                 StringComparison.OrdinalIgnoreCase);
        }

        void ProjectChanged()
        {
            currentAssociation = "";
            packagedFolder = null; // Open folder shows this project's folder, not another project's build
            // Suggest <project>\Builds as the Build location, unless the user typed or browsed their own.
            var suggested = DefaultBuildLocation();
            if (suggested != null)
            {
                var current = buildLocationBox.Text.Trim();
                if (current.Length == 0 || current == autoBuildLocation || IsOldDefaultLocation(current))
                {
                    autoBuildLocation = suggested;
                    buildLocationBox.Text = suggested;
                }
            }
            if (File.Exists(ProjectPath))
            {
                try
                {
                    var m = Regex.Match(File.ReadAllText(ProjectPath), "\"EngineAssociation\"\\s*:\\s*\"([^\"]*)\"");
                    currentAssociation = m.Success ? m.Groups[1].Value : "";
                }
                catch { }
                engineBox.Items[0] = KeepEngine + " (" + (currentAssociation.Length > 0 ? currentAssociation : "none") + ")";
                // Upgrade Project Version suggests the newest engine that is an upgrade; other actions keep the project's.
                PickActionEngine();
            }
            UpdateEngineNote();
            UpdateState();
        }

        void UpdateEngineNote()
        {
            engineNote.Text = "";
            if (File.Exists(ProjectPath) && engineBox.SelectedIndex > 0 && !Engines.IsRegistered(currentAssociation))
                engineNote.Text = "Current engine \"" + Short(currentAssociation) + "\" isn't installed on this PC";
            else if (File.Exists(ProjectPath) && engineBox.SelectedIndex == 0 && !Engines.IsRegistered(currentAssociation))
                engineNote.Text = "Not installed here: Unreal will ask which engine to use";
        }

        static string Short(string s) { return s.Length > 16 ? s.Substring(0, 9) + "..." + s.Substring(s.Length - 4) : s; }

        void UpdateState()
        {
            var old = OldName;

            var act = CurrentAction;
            bool rename = act == Act.Rename;
            string problem = null;
            // No project yet: a grey hint under Project. One that can't be found: the reason in red there,
            // and a pointer in the status line.
            bool noProject = !File.Exists(ProjectPath);
            bool empty = projectBox.Text.Trim().Trim('"').Length == 0;
            projectNote.Text = noProject ? MissingProjectMessage() : "";
            projectNote.ForeColor = empty ? Theme.Muted : Theme.Error;
            projectNote.Visible = noProject;
            if (noProject) problem = empty ? "" : "Can't start - see the message under Project.";
            else if (rename && nameBox.Text.Length == 0) problem = "Type the new project name.";
            else if (rename && !ValidName.IsMatch(nameBox.Text)) problem = "The name must start with a letter and use only letters, digits and underscores.";
            else if (rename && nameBox.Text == old) problem = "The new name is the same as the current one.";
            else if (rename && NewNameProblem(nameBox.Text, old) != null) problem = NewNameProblem(nameBox.Text, old);
            else if (act == Act.Upgrade && engineBox.SelectedIndex <= 0)
                problem = "Pick the engine version to upgrade to (no newer engine is installed, or Keep current is selected).";
            else if (act == Act.Upgrade && (string)engineBox.SelectedItem == "UE " + currentAssociation)
                problem = "The project already uses " + engineBox.SelectedItem + ". Pick a newer engine version.";
            else if (act == Act.Package && buildLocationBox.Text.Trim().Length == 0)
                problem = "Choose a Build location for the packaged game (Browse...).";

            // Engine / Visual Studio / Windows SDK problems block Rename (it builds), not Preview.
            var blocker = BuildBlocker();
            vsNote.Text = blocker ?? "";
            vsNote.Visible = blocker != null;

            bool busy = running != null && !running.HasExited;
            SetInputsLocked(busy);
            previewButton.Enabled = problem == null && !busy;
            renameButton.Enabled = problem == null && blocker == null && !busy;
            cancelButton.Visible = busy;
            cancelButton.Enabled = busy && cancellable;
            openButton.Enabled = !busy && OpenFolderTarget() != null; // greyed out without a project
            UpdateSaveLogButton();
            if (!busy)
                status.Text = problem ??
                    (blocker != null ? (rename ? "Can't rename yet - see the message above. Preview still works."
                                               : "Can't " + Verb(act, Rebuilding).ToLower() + " yet - see the message above.")
                     : rename ? "Ready to rename \"" + old + "\" to \"" + nameBox.Text + "\". Preview first to see every change."
                     : "Ready to " + Verb(act, Rebuilding).ToLower() + " \"" + old + "\".");
        }

        /// <summary>Grey out the inputs while a preview or rename runs, so nothing can change under it.</summary>
        void SetInputsLocked(bool locked)
        {
            foreach (Control c in new Control[] { actionBox, rebuildBox, buildConfigBox, cookContentBox, buildLocationBox, buildLocationBrowse, projectBox, projectBrowse, nameBox, engineBox, vsBox, whenDoneBox })
                c.Enabled = !locked;
            AllowDrop = !locked;
        }

        /// <summary>Popup after a rename that failed on its own (never after Cancel), with Save Output Log.</summary>
        void ShowFailure(string title, string message)
        {
            if (AskUser(this, title, message, "Save Output Log", "OK")) SaveLog();
        }

        /// <summary>What went wrong, worked out from the script's output.</summary>
        string FailureMessage(bool renamed)
        {
            var lines = log.Lines;
            Func<string, bool> has = pattern => lines.Any(l => Regex.IsMatch(l, pattern));

            if (!renamed)
            {
                if (has(@"^Traceback "))
                    return "The rename stopped with an unexpected error.\n\nSome files may already have been changed. " +
                           "Check the project with git (for example git status) before trying again, and save the " +
                           "output log if you need help.";
                var why = lines.Where(l => l.StartsWith("error: ") && !l.Contains("COMMON NAME"))
                               .Select(l => "- " + l.Substring(7).Trim()).Distinct().Take(3).ToList();
                return "The project wasn't renamed. Nothing was changed.\n\n" +
                       (why.Count > 0 ? "Why:\n" + string.Join("\n", why) : "The output log has the details.");
            }

            var steps = new List<string>();
            if (has(@"couldn't find the engine for the follow-up steps"))
                steps.Add("- The engine wasn't found, so nothing was built.");
            if (has(@"project file generation failed"))
                steps.Add("- Generating the Visual Studio project files failed.");
            if (has(@"build FAILED"))
                steps.Add("- The build failed, so the asset resave and plugin check were skipped.");
            var badPlugins = BuildErrorPlugins(lines);
            if (badPlugins != null) steps.Add(badPlugins);
            if (has(@"^\s+(SKIPPED|FAILED)\s"))
                steps.Add("- Some assets couldn't be resaved (listed in the log). Open them in the editor, fix them and save them.");
            if (has(@"^\s+FAIL\s") || has(@"FAILED: the editor didn't start"))
                steps.Add("- The plugin check found plugins that didn't load (marked FAIL in the log).");
            if (has(@"^\s+cook FAILED"))
                steps.Add("- Cooking the content failed. Look for \"Error:\" lines in the log.");
            if (has(@"^\s+packaging FAILED"))
                steps.Add("- Cooking or packaging the build failed. Look for \"Error:\" lines in the log.");
            if (steps.Count == 0)
                steps.Add("- A step after the rename reported a problem. The output log has the details.");
            return "The project was renamed, but some steps after the rename need attention:\n\n" +
                   string.Join("\n", steps) +
                   "\n\nThe renamed files are kept. Fix the problem, then build the project in Visual Studio or Unreal.";
        }

        /// <summary>Save / Copy Output Log: only when there's a log and nothing is running.</summary>
        void UpdateSaveLogButton()
        {
            saveLogButton.Enabled = copyLogButton.Enabled = log.TextLength > 0 && (running == null || running.HasExited);
        }

        /// <summary>Copy Output Log: the whole log as plain text (Windows line endings) on the clipboard.</summary>
        void CopyLog()
        {
            if (!copyLogButton.Enabled) return;
            try
            {
                // Another app can hold the clipboard for a moment: retry a few times.
                Clipboard.SetDataObject(string.Join(Environment.NewLine, log.Lines), true, 10, 100);
                status.Text = "Output log copied to the clipboard.";
            }
            catch (Exception ex)
            {
                Notify(this, "Couldn't copy the log", ex.Message);
            }
        }

        /// <summary>Hook so tests can pick the file instead of the Save dialog. Returns null to cancel.</summary>
        internal static Func<IWin32Window, string, string, string> AskSavePath = (owner, folder, fileName) =>
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "Save output log",
                Filter = "Text file (*.txt)|*.txt",
                DefaultExt = "txt",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = fileName,
                InitialDirectory = folder,
            })
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
        };

        void SaveLog()
        {
            if (!saveLogButton.Enabled) return;
            var name = OldName.Length > 0 ? OldName : "VFSUnrealProjectBuildTool";
            var folder = lastResultFolder ?? (File.Exists(ProjectPath) ? Path.GetDirectoryName(ProjectPath)
                                                                       : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            var path = AskSavePath(this, folder, name + "_log_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".txt");
            if (path == null) return;
            try
            {
                File.WriteAllText(path, string.Join(Environment.NewLine, log.Lines), new UTF8Encoding(false)); // Windows line endings
                status.Text = "Output log saved to " + path;
            }
            catch (Exception ex)
            {
                Notify(this, "Couldn't save the log", ex.Message);
            }
        }

        void BrowseProject()
        {
            if (!projectBrowse.Enabled) return;
            using (var dlg = new OpenFileDialog { Filter = "Unreal project (*.uproject)|*.uproject", Title = "Choose the project to rename" })
                if (dlg.ShowDialog(this) == DialogResult.OK) projectBox.Text = dlg.FileName;
        }

        /// <summary>The main button: Build / Rebuild / Package / Rename.</summary>
        void StartAction()
        {
            // Building or renaming while the project is open fails (locked DLLs, Live Coding, files rewritten
            // underneath an IDE).
            while (true)
            {
                var open = Engines.ProgramsUsing(ProjectPath, OldName);
                if (open.Count == 0) break;
                if (!DarkDialog.Ask(this, "Project is open",
                        "\"" + OldName + "\" is open in " + string.Join(" and ", open) + ".\n\n" +
                        "Close it there first, then click Check again.", true, "Check again", "Cancel"))
                    return;
            }

            if (CurrentAction == Act.Rename)
            {
                string msg = "This renames \"" + OldName + "\" to \"" + nameBox.Text + "\" in place.\n\n" +
                      "It changes files in the project and deletes Binaries and Intermediate (they are rebuilt). " +
                      "The project folder keeps its name.\n\n" +
                      "Close the project in Unreal and Visual Studio, and make sure it is committed or backed up.\n\nContinue?";
                if (!DarkDialog.Ask(this, "Rename project", msg, true, "Rename", "Cancel"))
                    return;
            }
            if (CurrentAction == Act.Upgrade)
            {
                string msg = "This switches \"" + OldName + "\" from " +
                      (Regex.IsMatch(currentAssociation, @"^\d+\.\d+") ? "UE " + currentAssociation : Short(currentAssociation)) +
                      " to " + engineBox.SelectedItem + ".\n\n" +
                      "It deletes .vs, Binaries, DerivedDataCache, Intermediate, .vsconfig and the .sln / .slnx files, " +
                      "regenerates the Visual Studio project files and builds with the new engine.\n\n" +
                      "Once assets are saved in the newer engine they can't be opened in the old one, so make sure the " +
                      "project is committed or backed up.\n\nContinue?";
                if (!AskUser(this, "Upgrade project version", msg, "Upgrade", "Cancel"))
                    return;
            }

            // Packaging goes to the Build location field.
            packageDir = PackageConfig() != null ? ChosenBuildLocation() : null;
            SaveFields(CurrentAction); // remember what this run used
            Run(false);
        }

        /// <summary>Folder picker, replaceable so tests can choose a folder. Returns null when cancelled.</summary>
        internal static Func<IWin32Window, string, string, string> AskFolder = (owner, title, start) =>
        {
            using (var dlg = new FolderBrowserDialog { Description = title, ShowNewFolderButton = true })
            {
                var folder = NearestExistingFolder(start);
                if (folder != null) dlg.SelectedPath = folder;
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
            }
        };

        /// <summary>The folder itself if it exists, else its nearest existing parent; null for an empty or
        /// invalid path (e.g. nothing picked yet, or a typed path with characters Windows doesn't allow).</summary>
        internal static string NearestExistingFolder(string path)
        {
            try
            {
                path = (path ?? "").Trim();
                while (path.Length > 0)
                {
                    if (Directory.Exists(path)) return path;
                    var parent = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(parent) || parent == path) break;
                    path = parent;
                }
            }
            catch (ArgumentException) { }       // characters not allowed in a path
            catch (NotSupportedException) { }   // e.g. a stray ':' in the middle
            catch (PathTooLongException) { }
            return null;
        }

        string WhenDone { get { return whenDoneBox.SelectedItem as string ?? DoNothing; } }

        /// <summary>Script arguments for the Action's packaging and the On Finish Success choice. Package Build
        /// packages the Build configuration; Cook content ticked = clean cook, unticked = recook only what changed.</summary>
        string[] WhenDoneArgs(string choice)
        {
            var args = new List<string>();
            var config = PackageConfig();
            if (config != null)
            {
                args.AddRange(new[] { "--package", config });
                if (cookContentBox.Checked) args.Add("--clean-cook"); // otherwise only what changed is recooked
            }
            if (choice == OpenEditor) args.Add("--open-editor");
            if (choice == LaunchExe && config != null) args.Add("--launch");
            return args.ToArray();
        }

        /// <summary>The Build field's configuration ("Development" / "Shipping"), whatever the action.</summary>
        string BuildConfigName { get { return buildConfigBox.SelectedItem as string ?? BuildConfigs[0]; } }

        /// <summary>The red "not for the public" line: Package Build with Development only.</summary>
        void UpdateBuildConfigWarning()
        {
            buildConfigWarning.Visible = CurrentAction == Act.Package && BuildConfigName == "Development";
        }

        bool CleanCookFor(string config)
        {
            bool on;
            return cleanCookByConfig.TryGetValue(config, out on) ? on : CleanCookDefaults.TryGetValue(config, out on) && on;
        }

        /// <summary>Ticks Clean cook content as remembered for the selected Build configuration.</summary>
        void ShowCleanCook()
        {
            showingCleanCook = true;
            cookContentBox.Checked = CleanCookFor(BuildConfigName);
            showingCleanCook = false;
        }

        /// <summary>"Development" / "Shipping" (the Build field) for Package Build, otherwise null.</summary>
        string PackageConfig()
        {
            return CurrentAction == Act.Package ? buildConfigBox.SelectedItem as string ?? BuildConfigs[0] : null;
        }

        // ------------------------------------------------------------------ running the script

        static string ExtractScript()
        {
            // A fresh file per run, so two copies of the app never overwrite each other's script.
            var dir = Path.Combine(Path.GetTempPath(), "VFSUnrealProjectBuildTool");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "rename_" + Guid.NewGuid().ToString("N") + ".py");
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("VFSUnrealProjectBuildTool.py"))
            using (var file = File.Create(path))
                res.CopyTo(file);
            return path;
        }

        static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        void Run(bool dryRun)
        {
            var python = Engines.FindPython();
            if (python == null)
            {
                DarkDialog.Show(this, Text, "Couldn't find Python. This tool uses the Python that comes with Unreal Engine; " +
                    "install Unreal Engine (or Python 3) and try again.");
                return;
            }

            var act = CurrentAction;
            bool rename = act == Act.Rename;
            dryRun = dryRun && rename; // only a rename has a preview
            runScript = ExtractScript();
            var args = new List<string> { "-u", "-I", runScript, ProjectPath };
            var downgrade = DowngradeMessage(engineBox.SelectedIndex);
            if (downgrade != null)
            {
                try { File.Delete(runScript); } catch { }
                runScript = null;
                Notify(this, "Engine downgrade blocked", downgrade);
                return;
            }
            if (rename)
            {
                args.Add(nameBox.Text);
                if (dryRun) args.Add("--dry-run"); else args.Add("--yes");
                if (!dryRun && allowCommonName) args.Add("--allow-common-name");
                // The app always renames in place and keeps the folder name.
                args.Add("--in-place");
                args.Add("--keep-folder-name");
            }
            else
            {
                args.Add("--action");
                args.Add(act == Act.Build ? "build" : act == Act.Regenerate ? "regenerate" : act == Act.Upgrade ? "upgrade" : "package");
                if (Rebuilding) args.Add("--rebuild");
                args.Add("--yes");
            }
            if (act == Act.Upgrade && engineBox.SelectedIndex > 0) // only Upgrade switches the engine
            {
                args.Add("--engine-association");
                args.Add(((string)engineBox.SelectedItem).Substring(3));
            }
            if (!dryRun)
            {
                var vs = vsBox.SelectedItem as Engines.VisualStudio;
                if (vs != null)
                {
                    args.Add("--visual-studio"); args.Add(vs.Year);
                    args.Add("--visual-studio-path"); args.Add(vs.Path);
                    var req = SelectedRequirements();
                    var toolset = req != null ? req.UsableToolset(vs) : null;
                    if (toolset != null) { args.Add("--visual-studio-msvc"); args.Add(toolset); }
                }
                if (rename)
                    // Every rename finishes the job: project files, build, resave, redirect cleanup, plugin check.
                    args.AddRange(new[] { "--generate-project-files", "--build", "--resave-assets",
                                          "--remove-unused-redirects", "--verify-plugins" });
                else
                    args.AddRange(new[] { "--generate-project-files", "--build" });
                args.AddRange(WhenDoneArgs(WhenDone));
                if (PackageConfig() != null && packageDir != null)
                {
                    args.Add("--package-dir"); args.Add(packageDir);
                }
            }

            var psi = new ProcessStartInfo(python, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            // Each run starts with an empty log; lines still arriving from an earlier run are dropped.
            int id = ++runId;
            log.Clear();
            inEditorChecks = false;
            lastResultFolder = null;
            packagedFolder = null;
            runIsPreview = dryRun;
            runAction = act;
            runRebuild = Rebuilding;
            upgradedTo = act == Act.Upgrade ? engineBox.SelectedItem as string : null;
            cancelled = false;
            // A real rename can't be stopped until its files are renamed; builds and packaging can be stopped anytime.
            cancellable = dryRun || !rename;
            status.Text = dryRun ? "Previewing..."
                : rename ? "Renaming, then building and checking assets... this can take several minutes."
                : act == Act.Package ? (runRebuild ? "Rebuilding (clean, then build) and packaging... this can take a long time."
                                                   : "Building and packaging... this can take a long time.")
                : act == Act.Upgrade ? "Switching to " + engineBox.SelectedItem + ", regenerating project files, then building... this can take several minutes."
                : act == Act.Regenerate ? "Deleting generated files, regenerating project files, then building... this can take several minutes."
                : runRebuild ? "Rebuilding (clean, then build)... this can take several minutes."
                : "Building... this can take several minutes.";
            running = new Process { StartInfo = psi, EnableRaisingEvents = true };
            running.OutputDataReceived += (s, e) => { if (e.Data != null) AppendFromRun(e.Data, id); };
            running.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendFromRun(e.Data, id); };
            running.Exited += (s, e) => BeginInvoke(new Action(() => Finished(dryRun)));
            try
            {
                running.Start();
                running.StandardInput.Close();
                running.BeginOutputReadLine();
                running.BeginErrorReadLine();
                // Run starting (not for Preview, and not again when a common name is confirmed).
                if (!dryRun && !allowCommonName) PlaySound(Sfx.Kind.Start);
            }
            catch (Exception ex)
            {
                running = null;
                Append("Couldn't start Python: " + ex.Message);
            }
            UpdateState();
        }

        /// <summary>Output from run number <paramref name="id"/>; ignored once a newer run has started.</summary>
        void AppendFromRun(string line, int id)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, int>(AppendFromRun), line, id); return; }
            if (id == runId) Append(line);
        }

        void Append(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), line); return; }
            // The script ends with a "CHECK IN UNREAL EDITOR" list between ===== lines: show it in yellow.
            if (line.StartsWith("CHECK IN UNREAL EDITOR") || line.StartsWith("CONFIRM WORKING BUILD")) inEditorChecks = true;
            bool yellow = inEditorChecks || line.StartsWith("=====");
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.SelectionColor = yellow ? Theme.Highlight : log.ForeColor;
            log.AppendText(line + Environment.NewLine);
            log.SelectionColor = log.ForeColor;
            log.ScrollToCaret();
            var m = Regex.Match(line, @"^Done: (.+\.uproject)\s*$");
            if (m.Success) lastResultFolder = Path.GetDirectoryName(m.Groups[1].Value);
            var pk = Regex.Match(line, @"^\s+Packaged: (.+?)\s*$");
            if (pk.Success) packagedFolder = pk.Groups[1].Value;

            // Stopping is only safe while nothing is being written: not while files are renamed (before
            // "Done:"), not while assets are saved, not while redirects are removed, and not once the editor
            // has been launched (stopping the process tree would close it too).
            if (!runIsPreview)
            {
                bool was = cancellable;
                // Cooking and packaging only write cooked data and the build folder, so they can be stopped.
                if (m.Success || line.StartsWith("Verifying plugins") || line.StartsWith("Cooking")) cancellable = true;
                else if (Regex.IsMatch(line, @"^\s+resaving \d+ asset") || line.StartsWith("Removing redirects")
                         || line.StartsWith("Opening Unreal Editor") || line.StartsWith("Launching ")) cancellable = false;
                if (was != cancellable) UpdateState();
            }
        }

        void CancelRun()
        {
            if (running == null || running.HasExited || !cancellable) return;
            // Builds and packaging stop without asking (nothing is lost; run it again). A rename asks first.
            if (!runIsPreview && runAction == Act.Rename && !AskUser(this, "Stop", "Stop the remaining steps?\n\nThe project is already renamed. " +
                    "Stopping now skips whatever is left (build, asset resave, plugin check, cook or packaging). You can finish later by " +
                    "building the project in Visual Studio or Unreal.", "Stop", "Keep going"))
                return;
            if (running == null || running.HasExited || !cancellable) return; // it moved on while the dialog was open
            cancelled = true;
            KillRun();
        }

        /// <summary>End the script and everything it started (UnrealBuildTool, compilers, headless editors).</summary>
        void KillRun()
        {
            if (running == null || running.HasExited) return;
            try
            {
                using (var k = Process.Start(new ProcessStartInfo("taskkill", "/PID " + running.Id + " /T /F")
                       { UseShellExecute = false, CreateNoWindow = true }))
                    k.WaitForExit(10000);
            }
            catch { }
            try { if (!running.HasExited) running.Kill(); } catch { }
        }

        void Finished(bool dryRun)
        {
            running.WaitForExit(); // flush remaining output events
            int code = running.ExitCode;
            running = null;
            cancellable = false;
            try { if (runScript != null) File.Delete(runScript); } catch { }
            runScript = null;
            UpdateState();
            if (runAction != Act.Rename)
            {
                FinishedAction(code);
                return;
            }
            bool renamed = !dryRun && lastResultFolder != null;
            // Point at the renamed project first: changing the box refreshes the status line.
            if (renamed)
                projectBox.Text = Path.Combine(lastResultFolder, nameBox.Text + ".uproject");

            // Exit code 3: the old name is a common word and the script wants confirmation (nothing was written).
            if (!dryRun && !cancelled && code == 3)
            {
                var m = Regex.Match(log.Text, @"warning: COMMON NAME: (.+)");
                var why = m.Success ? m.Groups[1].Value.Trim() : "The old name is a common word.";
                if (AskUser(this, "Common project name", why + "\n\nRename anyway?", "Rename anyway", "Cancel"))
                {
                    allowCommonName = true;
                    Run(false);
                    allowCommonName = false;
                    return;
                }
                status.Text = "Not renamed - nothing was changed.";
                return;
            }

            if (cancelled)
            {
                log.AppendText(Environment.NewLine + "Stopped by you." + Environment.NewLine);
                status.Text = dryRun ? "Preview stopped." :
                    "Stopped. The project is renamed; build it in Visual Studio or Unreal to finish.";
            }
            else if (dryRun)
                status.Text = code != 0 ? "Failed - see the messages above." :
                              log.Text.Contains("COMMON NAME:") ? "Preview done - note the COMMON NAME warning above. Nothing was changed." :
                              "Preview done - nothing was changed. Click Rename to apply.";
            else if (!renamed)
            {
                status.Text = "Failed - see the messages above.";
                PlaySound(Sfx.Kind.Failure);
                ShowFailure("Rename failed", FailureMessage(false));
            }
            else if (code != 0)
            {
                status.Text = "Renamed, but a follow-up step needs attention - see the messages above.";
                PlaySound(Sfx.Kind.Failure);
                ShowFailure("Rename needs attention", FailureMessage(true));
            }
            else
            {
                status.Text = packagedFolder != null ? "Renamed, built and packaged. All steps finished. Open folder shows the build."
                            : log.Text.Contains("  Cooked: ") ? "Renamed, built and cooked. All steps finished."
                            : "Renamed and built. All steps finished.";
                PlaySound(Sfx.Kind.Success);
            }
        }

        /// <summary>End of a Build / Rebuild / Package run (no rename).</summary>
        void FinishedAction(int code)
        {
            if (runAction == Act.Upgrade) ProjectChanged(); // re-read the project's (new) engine
            lastResultFolder = File.Exists(ProjectPath) ? Path.GetDirectoryName(ProjectPath) : null;
            var verb = Verb(runAction, runRebuild);
            if (cancelled)
            {
                log.AppendText(Environment.NewLine + "Stopped by you." + Environment.NewLine);
                status.Text = "Stopped. Nothing was lost; run it again whenever you like.";
            }
            else if (code == 0)
            {
                bool launched = log.Text.Contains("\nLaunching ");
                var built = runAction == Act.Upgrade ? "Upgraded to " + upgradedTo + ", regenerated project files and built"
                          : runAction == Act.Regenerate ? "Regenerated project files and built" : runRebuild ? "Rebuilt" : "Built";
                status.Text = packagedFolder != null ? built + " and packaged" + (launched ? ", and the game was started" : "") +
                                                       ". All steps finished. Open folder shows the build."
                            : log.Text.Contains("  Cooked: ") ? built + " and cooked. All steps finished."
                            : built + ". All steps finished.";
                PlaySound(Sfx.Kind.Success);
            }
            else
            {
                status.Text = verb + " failed - see the messages above.";
                PlaySound(Sfx.Kind.Failure);
                ShowFailure(verb + " failed", ActionFailureMessage());
            }
        }

        /// <summary>What went wrong in a Build / Rebuild / Package run, from the script's output.</summary>
        /// <summary>"- The build errors are in these plugins: ..." from the script's "errors in plugin: X (n errors)"
        /// lines, or null when no project plugin had errors.</summary>
        static string BuildErrorPlugins(string[] lines)
        {
            var names = lines.Select(l => Regex.Match(l, @"^\s+errors in plugin: (\S+)"))
                             .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList();
            if (names.Count == 0) return null;
            return "- The build errors are in " + (names.Count == 1 ? "the plugin " : "the plugins ") + string.Join(", ", names) +
                   ", which may not support this engine version yet. Replace " + (names.Count == 1 ? "it" : "them") +
                   " in the project's Plugins folder with " + (names.Count == 1 ? "its" : "their") +
                   " version for this engine, then run again.";
        }

        string ActionFailureMessage()
        {
            var lines = log.Lines;
            Func<string, bool> has = pattern => lines.Any(l => Regex.IsMatch(l, pattern));
            var steps = new List<string>();
            foreach (var l in lines.Where(l => l.StartsWith("error: ")).Distinct().Take(3))
                steps.Add("- " + l.Substring(7).Trim());
            var locked = lines.Where(l => Regex.IsMatch(l, @"^\s+warning: couldn't delete ")).Select(l => l.Trim().Substring(24)).ToList();
            if (locked.Count > 0)
                steps.Add("- Some generated files couldn't be deleted (in use - close Visual Studio, Rider or the editor and " +
                          "run it again): " + string.Join(", ", locked.Select(x => x.Split(':')[0]).Distinct().Take(5)));
            if (has(@"couldn't find the engine for the follow-up steps") && steps.Count == 0)
                steps.Add("- The engine wasn't found, so nothing was built.");
            if (has(@"project file generation failed"))
                steps.Add("- Generating the Visual Studio project files failed.");
            if (has(@"build FAILED"))
                steps.Add("- The build failed" + (runAction == Act.Package ? ", so nothing was cooked or packaged." : "."));
            var badPlugins = BuildErrorPlugins(lines);
            if (badPlugins != null) steps.Add(badPlugins);
            if (has(@"^\s+cook FAILED"))
                steps.Add("- Cooking the content failed.");
            if (has(@"^\s+packaging FAILED"))
                steps.Add("- Cooking or packaging the build failed.");
            if (has(@"^Traceback "))
                steps.Add("- The tool stopped with an unexpected error.");
            if (steps.Count == 0)
                steps.Add("- A step reported a problem.");
            return "The " + Verb(runAction, runRebuild).ToLower() + " didn't finish:\n\n" + string.Join("\n", steps) +
                   "\n\nLook for \"error\" lines in the output log for the details.";
        }
    }
}
