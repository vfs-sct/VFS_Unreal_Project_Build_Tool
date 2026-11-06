// Unreal Project Renamer - Windows GUI for rename_unreal_project.py.
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
[assembly: AssemblyTitle("Unreal Project Renamer")]
[assembly: AssemblyProduct("Unreal Project Renamer")]
[assembly: AssemblyVersion(UnrealProjectRenamer.Program.Version + ".0")]
[assembly: AssemblyFileVersion(UnrealProjectRenamer.Program.Version + ".0")]
[assembly: AssemblyInformationalVersion(UnrealProjectRenamer.Program.Version)]

namespace UnrealProjectRenamer
{
    static class Program
    {
        // App version: bump this for every build you hand out (major.minor.patch).
        public const string Version = "0.11.0";

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
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

            public static Requirements Load(string engineDir, string engineName)
            {
                var r = new Requirements { EngineName = engineName };
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
                if (root.ForeColor != Warn) root.ForeColor = Text;
            }
            var tb = root as TextBox;
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

    class MainForm : Form
    {
        static readonly Regex ValidName = new Regex("^[A-Za-z][A-Za-z0-9_]*$");
        const string KeepEngine = "Keep current";

        readonly TextBox projectBox = new TextBox();
        readonly Button projectBrowse = new Button { Text = "Browse...", AutoSize = true };
        // Unreal recommends project names of 20 characters or fewer.
        readonly TextBox nameBox = new TextBox { MaxLength = 20 };
        readonly ComboBox engineBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label engineNote = new Label { AutoSize = true, ForeColor = Theme.Warn };
        readonly ComboBox vsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label vsNote = new Label { AutoSize = true, ForeColor = Theme.Warn, MaximumSize = new Size(700, 0) };
        readonly CheckBox openEditor = new DarkCheck { Text = "Open in Unreal Editor when done", AutoSize = true, Checked = true };
        readonly Button previewButton = new Button { Text = "Preview changes", AutoSize = true };
        readonly Button renameButton = new Button { Text = "Rename", AutoSize = true };
        readonly Button cancelButton = new Button { Text = "Cancel", AutoSize = true, Visible = false };
        readonly Button openButton = new Button { Text = "Open folder", AutoSize = true, Enabled = false };
        readonly Button saveLogButton = new Button { Text = "Save Output Log", AutoSize = true, Enabled = false };
        readonly TextBox log = new TextBox();
        readonly Label status = new Label { AutoSize = true, Text = "Pick a .uproject to start." };

        string currentAssociation = "";
        string lastResultFolder;
        Process running;
        string runScript;          // this run's private copy of the script (deleted afterwards)
        int lastEngineIndex;       // engine choice to go back to when a downgrade is refused
        bool settingEngine;        // engine choice is being set by code, not by the user
        bool allowCommonName;      // the user confirmed renaming a project with a common-word name
        int runId;                 // counts runs, so output from an earlier run can't reach the new log

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
            Text = "Unreal Project Renamer  v" + Program.Version;
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

            projectBox.Dock = DockStyle.Fill;
            AddRow(grid, "Project (.uproject)", projectBox, projectBrowse);

            nameBox.Dock = DockStyle.Fill;
            // Grey hint shown while the box is empty (also while it has focus).
            nameBox.HandleCreated += (s, e) => SendMessage(nameBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Maximum 20 characters");
            AddRow(grid, "New name", nameBox, null);

            var enginePanel = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            engineBox.Width = 260;
            enginePanel.Controls.Add(engineBox);
            engineNote.Margin = new Padding(8, 6, 0, 0);
            enginePanel.Controls.Add(engineNote);
            AddRow(grid, "Engine version", enginePanel, null);

            vsBox.Width = 360;
            vsBox.Margin = new Padding(3, 3, 3, 6);
            AddRow(grid, "Visual Studio", vsBox, null);
            vsNote.Margin = new Padding(3, 0, 3, 6);
            AddRow(grid, "", vsNote, null);

            AddRow(grid, "", openEditor, null);

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 8), WrapContents = false };
            buttons.Controls.Add(previewButton);
            buttons.Controls.Add(renameButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(openButton);
            buttons.Controls.Add(saveLogButton);
            AddRow(grid, "", buttons, null);

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Both;
            log.WordWrap = false;
            log.Font = new Font("Consolas", 9f);
            log.BackColor = SystemColors.Window;
            log.Dock = DockStyle.Fill;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(log, 0, grid.RowCount);
            grid.SetColumnSpan(log, 3);
            grid.RowCount++;

            grid.Controls.Add(status, 0, grid.RowCount);
            grid.SetColumnSpan(status, 3);
            grid.RowCount++;

            Controls.Add(grid);

            projectBrowse.Click += (s, e) => BrowseProject();
            projectBox.TextChanged += (s, e) => ProjectChanged();
            nameBox.TextChanged += (s, e) => UpdateState();
            engineBox.SelectedIndexChanged += (s, e) => EngineChanged();
            vsBox.SelectedIndexChanged += (s, e) => UpdateState();
            previewButton.Click += (s, e) => Run(true);
            renameButton.Click += (s, e) => ConfirmAndRename();
            cancelButton.Click += (s, e) => CancelRun();
            saveLogButton.Click += (s, e) => SaveLog();
            log.TextChanged += (s, e) => UpdateSaveLogButton();
            openButton.Click += (s, e) => { if (lastResultFolder != null && Directory.Exists(lastResultFolder)) Process.Start("explorer.exe", "\"" + lastResultFolder + "\""); };
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
                else if (DarkDialog.Ask(this, Text, "A rename is still running. Stop it and close?", true, "Stop and close", "Keep running"))
                {
                    cancelled = true; // stopped by the user: no failure sound or popup
                    KillRun();
                }
                else
                    e.Cancel = true;
            };

            // Names double as UI Automation ids (used by accessibility tools and tests).
            projectBox.Name = "projectBox"; nameBox.Name = "nameBox";
            engineBox.Name = "engineBox"; vsBox.Name = "vsBox"; openEditor.Name = "openEditor"; log.Name = "log"; status.Name = "status";
            previewButton.Name = "previewButton"; renameButton.Name = "renameButton"; openButton.Name = "openButton";
            cancelButton.Name = "cancelButton"; vsNote.Name = "vsNote"; saveLogButton.Name = "saveLogButton";

            Theme.Apply(this);
            Theme.StyleButton(renameButton, true);
            status.ForeColor = Theme.Muted;
            HandleCreated += (s, e) => Theme.DarkTitleBar(Handle);

            vsNote.ForeColor = Theme.Warn;

            FillEngines();
            FillVisualStudios();
            PickDefaultVisualStudio();
            UpdateState();
        }

        static void AddRow(TableLayoutPanel grid, string label, Control field, Control extra)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label.Length > 0)
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) }, 0, row);
            grid.Controls.Add(field, 1, row);
            if (extra != null) grid.Controls.Add(extra, 2, row);
            else grid.SetColumnSpan(field, 2);
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
            if (engineBox.SelectedIndex > 0)
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
                return "This project's engine (" + Short(currentAssociation) + ") isn't installed on this PC. Pick an installed engine under Engine version.";
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

        void ProjectChanged()
        {
            currentAssociation = "";
            if (File.Exists(ProjectPath))
            {
                try
                {
                    var m = Regex.Match(File.ReadAllText(ProjectPath), "\"EngineAssociation\"\\s*:\\s*\"([^\"]*)\"");
                    currentAssociation = m.Success ? m.Groups[1].Value : "";
                }
                catch { }
                engineBox.Items[0] = KeepEngine + " (" + (currentAssociation.Length > 0 ? currentAssociation : "none") + ")";
                // An association this PC doesn't know makes Unreal ask for a version on every open,
                // so suggest the newest installed engine instead (unless that would be a downgrade).
                if (!Engines.IsRegistered(currentAssociation) && engineBox.Items.Count > 1 && DowngradeMessage(1) == null)
                    SetEngineIndex(1);
                else
                    SetEngineIndex(0);
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

            string problem = null;
            if (!File.Exists(ProjectPath)) problem = "Pick a .uproject to start (or drag one onto this window).";
            else if (nameBox.Text.Length == 0) problem = "Type the new project name.";
            else if (!ValidName.IsMatch(nameBox.Text)) problem = "The name must start with a letter and use only letters, digits and underscores.";
            else if (nameBox.Text == old) problem = "The new name is the same as the current one.";

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
            UpdateSaveLogButton();
            if (!busy) status.Text = problem ?? (blocker != null ? "Can't rename yet - see the message above. Preview still works."
                                                                 : "Ready to rename \"" + old + "\" to \"" + nameBox.Text + "\". Preview first to see every change.");
        }

        /// <summary>Grey out the inputs while a preview or rename runs, so nothing can change under it.</summary>
        void SetInputsLocked(bool locked)
        {
            foreach (Control c in new Control[] { projectBox, projectBrowse, nameBox, engineBox, vsBox, openEditor })
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
            if (has(@"^\s+(SKIPPED|FAILED)\s"))
                steps.Add("- Some assets couldn't be resaved (listed in the log). Open them in the editor, fix them and save them.");
            if (has(@"^\s+FAIL\s") || has(@"FAILED: the editor didn't start"))
                steps.Add("- The plugin check found plugins that didn't load (marked FAIL in the log).");
            if (steps.Count == 0)
                steps.Add("- A step after the rename reported a problem. The output log has the details.");
            return "The project was renamed, but some steps after the rename need attention:\n\n" +
                   string.Join("\n", steps) +
                   "\n\nThe renamed files are kept. Fix the problem, then build the project in Visual Studio or Unreal.";
        }

        /// <summary>Save Output Log: only when there's a log and nothing is running.</summary>
        void UpdateSaveLogButton()
        {
            saveLogButton.Enabled = log.TextLength > 0 && (running == null || running.HasExited);
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
            var name = OldName.Length > 0 ? OldName : "UnrealProjectRenamer";
            var folder = lastResultFolder ?? (File.Exists(ProjectPath) ? Path.GetDirectoryName(ProjectPath)
                                                                       : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            var path = AskSavePath(this, folder, name + "_rename_log_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".txt");
            if (path == null) return;
            try
            {
                File.WriteAllText(path, log.Text, new UTF8Encoding(false));
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

        void ConfirmAndRename()
        {
            // Renaming while the project is open fails partway (locked DLLs, files rewritten underneath an IDE).
            while (true)
            {
                var open = Engines.ProgramsUsing(ProjectPath, OldName);
                if (open.Count == 0) break;
                if (!DarkDialog.Ask(this, "Project is open",
                        "\"" + OldName + "\" is open in " + string.Join(" and ", open) + ".\n\n" +
                        "Close it there first, then click Check again.", true, "Check again", "Cancel"))
                    return;
            }

            string msg = "This renames \"" + OldName + "\" to \"" + nameBox.Text + "\" in place.\n\n" +
                  "It changes files in the project and deletes Binaries and Intermediate (they are rebuilt). " +
                  "The project folder keeps its name.\n\n" +
                  "Close the project in Unreal and Visual Studio, and make sure it is committed or backed up.\n\nContinue?";
            if (DarkDialog.Ask(this, "Rename project", msg, true, "Rename", "Cancel"))
                Run(false);
        }

        // ------------------------------------------------------------------ running the script

        static string ExtractScript()
        {
            // A fresh file per run, so two copies of the app never overwrite each other's script.
            var dir = Path.Combine(Path.GetTempPath(), "UnrealProjectRenamer");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "rename_" + Guid.NewGuid().ToString("N") + ".py");
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("rename_unreal_project.py"))
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

            runScript = ExtractScript();
            var args = new List<string> { "-u", "-I", runScript, ProjectPath, nameBox.Text };
            var downgrade = DowngradeMessage(engineBox.SelectedIndex);
            if (downgrade != null)
            {
                try { File.Delete(runScript); } catch { }
                runScript = null;
                Notify(this, "Engine downgrade blocked", downgrade);
                return;
            }
            if (dryRun) args.Add("--dry-run"); else args.Add("--yes");
            if (!dryRun && allowCommonName) args.Add("--allow-common-name");
            // The app always renames in place and keeps the folder name.
            args.Add("--in-place");
            args.Add("--keep-folder-name");
            if (engineBox.SelectedIndex > 0)
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
                // Every rename finishes the job: project files, build, resave, redirect cleanup, plugin check.
                args.AddRange(new[] { "--generate-project-files", "--build", "--resave-assets",
                                      "--remove-unused-redirects", "--verify-plugins" });
                if (openEditor.Checked) args.Add("--open-editor");
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
            lastResultFolder = null;
            openButton.Enabled = false;
            runIsPreview = dryRun;
            cancelled = false;
            cancellable = dryRun; // a real rename can't be stopped until its files are renamed
            status.Text = dryRun ? "Previewing..." : "Renaming, then building and checking assets... this can take several minutes.";
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
                // Rename starting (not for Preview, and not again when a common name is confirmed).
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
            log.AppendText(line + Environment.NewLine);
            var m = Regex.Match(line, @"^Done: (.+\.uproject)\s*$");
            if (m.Success) lastResultFolder = Path.GetDirectoryName(m.Groups[1].Value);

            // Stopping is only safe while nothing is being written: not while files are renamed (before
            // "Done:"), not while assets are saved, not while redirects are removed, and not once the editor
            // has been launched (stopping the process tree would close it too).
            if (!runIsPreview)
            {
                bool was = cancellable;
                if (m.Success || line.StartsWith("Verifying plugins")) cancellable = true;
                else if (Regex.IsMatch(line, @"^\s+resaving \d+ asset") || line.StartsWith("Removing redirects")
                         || line.StartsWith("Opening Unreal Editor")) cancellable = false;
                if (was != cancellable) UpdateState();
            }
        }

        void CancelRun()
        {
            if (running == null || running.HasExited || !cancellable) return;
            if (!runIsPreview && !AskUser(this, "Stop", "Stop the remaining steps?\n\nThe project is already renamed. " +
                    "Stopping now skips whatever is left (build, asset resave or plugin check). You can finish later by " +
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
            bool renamed = !dryRun && lastResultFolder != null;
            // Point at the renamed project first: changing the box refreshes the status line.
            if (renamed)
                projectBox.Text = Path.Combine(lastResultFolder, nameBox.Text + ".uproject");
            openButton.Enabled = renamed;

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
                status.Text = "Renamed and built. All steps finished.";
                PlaySound(Sfx.Kind.Success);
            }
        }
    }
}
