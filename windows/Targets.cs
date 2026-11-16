using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace ShowTell;

public sealed record InstalledTarget(TargetApp Target, string? AppId, string DisplayName)
{
    public override string ToString() => Target.Name;
}

/// <summary>Finds which supported AI apps are installed (Start menu / MSIX) or running.</summary>
public static class AppCatalog
{
    public static List<(string Name, string AppId)> StartApps()
    {
        var list = new List<(string, string)>();
        // Shell COM must run on an STA thread.
        var t = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application")!;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name;
                    string path = item.Path;
                    list.Add((name, path));
                }
            }
            catch (Exception ex) { Log.Write("AppsFolder enumeration failed: " + ex.Message); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(10000);
        return list;
    }

    public static List<InstalledTarget> Detect(Settings s)
    {
        var apps = StartApps();
        var found = new List<InstalledTarget>();
        foreach (var target in s.Targets)
        {
            var nameRx = string.IsNullOrWhiteSpace(target.DisplayNamePattern) ? null : new Regex(target.DisplayNamePattern, RegexOptions.IgnoreCase);
            var idRx = string.IsNullOrWhiteSpace(target.AppIdPattern) ? null : new Regex(target.AppIdPattern, RegexOptions.IgnoreCase);
            var match = apps.FirstOrDefault(a => (nameRx?.IsMatch(a.Name) ?? false) || (idRx?.IsMatch(a.AppId) ?? false));
            if (match.AppId != null)
                found.Add(new InstalledTarget(target, match.AppId, match.Name));
            else if (Automation.FindMainWindow(target) != IntPtr.Zero)
                found.Add(new InstalledTarget(target, null, target.Name)); // running portable copy
        }
        Log.Write("Detected targets: " + string.Join(", ", found.Select(f => $"{f.Target.Name}={f.AppId}")));
        return found;
    }
}

public enum ThreadKind { NewChat, Current, Existing }

public sealed record ThreadChoice(ThreadKind Kind, string Label, string? Name = null)
{
    public override string ToString() => Label;
}

/// <summary>UI Automation helpers that drive a target app's window: list threads, open one, focus the composer, paste.</summary>
public static class Automation
{
    // ---------- windows ----------

    public static IntPtr FindMainWindow(TargetApp t)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcesses())
        {
            try { if (t.ProcessNames.Any(n => string.Equals(n, p.ProcessName, StringComparison.OrdinalIgnoreCase))) pids.Add((uint)p.Id); }
            catch { }
            finally { p.Dispose(); }
        }
        if (pids.Count == 0) return IntPtr.Zero;

        IntPtr best = IntPtr.Zero; long bestArea = 0;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            var sb = new StringBuilder(256);
            if (Native.GetWindowText(h, sb, sb.Capacity) == 0) return true;
            Native.GetWindowRect(h, out var r);
            long area = (long)r.Width * r.Height;
            if (Native.IsIconic(h)) area = 1; // minimized windows still count, just lowest priority
            if (area > bestArea) { best = h; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static bool IsTargetForeground(IntPtr hwnd)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == hwnd) return true;
        Native.GetWindowThreadProcessId(fg, out var a);
        Native.GetWindowThreadProcessId(hwnd, out var b);
        return a == b;
    }

    public static async Task<IntPtr> LaunchAndWaitAsync(InstalledTarget t, IProgress<string>? status, CancellationToken ct)
    {
        var hwnd = FindMainWindow(t.Target);
        if (hwnd != IntPtr.Zero) return hwnd;
        if (t.AppId == null) throw new InvalidOperationException($"{t.Target.Name} isn't running and couldn't be launched.");
        status?.Report($"Opening {t.Target.Name}…");
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{t.AppId}") { UseShellExecute = true });
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500, ct);
            hwnd = FindMainWindow(t.Target);
            if (hwnd != IntPtr.Zero)
            {
                await Task.Delay(2500, ct); // let the web UI finish loading
                return hwnd;
            }
        }
        throw new TimeoutException($"{t.Target.Name} didn't open a window within 30 seconds.");
    }

    // ---------- threads ----------

    static readonly HashSet<string> NotThreads = new(StringComparer.OrdinalIgnoreCase)
    {
        "New", "New chat", "New thread", "New task", "New conversation", "New session", "Search", "Search chats", "Artifacts", "Customize",
        "Projects", "Recents", "Recent", "Chats", "Code", "Library", "Settings", "Home", "Images", "Explore",
        "Plugins", "Scheduled", "Show more", "Show less", "View all", "See all", "More navigation items", "This computer",
        "Starred", "Pinned", "Today", "Yesterday", "Previous 7 Days", "Previous 30 Days", "Upgrade", "Upgrade plan",
        "Help", "Menu", "Back", "Forward", "Hide sidebar", "Show sidebar", "Open sidebar", "Close sidebar", "Toggle sidebar",
        "Skip to content", "Tasks", "Workspaces", "Automations", "Skills", "Voice", "Imagine", "History", "Companions", "Files",
    };

    static readonly Regex NotThreadPattern = new(
        @"^(new\b|open profile|switch mode|chat sidebar|project (actions|sidebar)|start new|more options|add new|group and sort|update available|pin chat|archive chat|delete|rename)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    record Candidate(AutomationElement Element, string Name, System.Windows.Rect Rect);

    static readonly Condition ClickableCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

    static AutomationElementCollection FindClickables(AutomationElement root)
    {
        var all = root.FindAll(TreeScope.Descendants, ClickableCondition);
        if (all.Count < 5)
        {
            // Chromium/Electron builds its accessibility tree lazily; the first query wakes it up.
            Thread.Sleep(1200);
            all = root.FindAll(TreeScope.Descendants, ClickableCondition);
        }
        return all;
    }

    static List<Candidate> SidebarItems(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var wr = root.Current.BoundingRectangle;
        double sidebarRight = wr.Left + Math.Min(wr.Width * 0.4, 520);
        var walker = TreeWalker.ControlViewWalker;
        var result = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (AutomationElement e in FindClickables(root))
        {
            AutomationElement.AutomationElementInformation c;
            try { c = e.Current; } catch { continue; }
            if (c.ControlType == ControlType.ListItem) continue; // use the clickable child instead
            var r = c.BoundingRectangle;
            if (r.IsEmpty || r.Width < 140 || r.Height < 16 || r.Height > 72) continue;
            if (r.Left > sidebarRight || r.Right > sidebarRight + 40) continue;
            if (r.Top < wr.Top + 40 || r.Bottom > wr.Bottom - 4) continue; // title bar / offscreen
            var name = (c.Name ?? "").Trim();
            if (name.Length < 2 || name.Length > 160) continue;
            if (NotThreads.Contains(name) || NotThreadPattern.IsMatch(name)) continue;

            // Skip project/folder headers: a parent list item whose name is longer than ours (it concatenates its children).
            if (IsGroupHeader(walker, e, name)) continue;

            if (!seen.Add(name)) continue;
            result.Add(new Candidate(e, name, r));
        }
        return result.OrderBy(x => x.Rect.Top).ToList();
    }

    /// <summary>True when an ancestor list item's name is ours plus its children's (a project / folder header, not a chat).</summary>
    static bool IsGroupHeader(TreeWalker walker, AutomationElement e, string name)
    {
        try
        {
            var node = e;
            for (int depth = 0; depth < 3; depth++)
            {
                node = walker.GetParent(node);
                if (node == null) return false;
                if (node.Current.ControlType != ControlType.ListItem) continue;
                var pn = node.Current.Name ?? "";
                return pn.Length > name.Length + 2 && pn.StartsWith(name, StringComparison.Ordinal);
            }
        }
        catch { }
        return false;
    }

    public static List<string> ListThreads(IntPtr hwnd)
    {
        try { return SidebarItems(hwnd).Select(c => c.Name).Take(40).ToList(); }
        catch (Exception ex) { Log.Write("ListThreads failed: " + ex.Message); return new(); }
    }

    public static string CurrentTitle(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var doc = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            var n = doc?.Current.Name?.Trim();
            if (!string.IsNullOrEmpty(n) && n.Length < 120) return n;
        }
        catch { }
        var sb = new StringBuilder(256);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static bool Activate(AutomationElement e)
    {
        try
        {
            if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return true; }
            if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) { ((SelectionItemPattern)sp).Select(); return true; }
        }
        catch (Exception ex) { Log.Write("Invoke failed, clicking instead: " + ex.Message); }
        var r = e.Current.BoundingRectangle;
        if (r.IsEmpty) return false;
        Native.GetCursorPos(out var old);
        Native.Click((int)(r.Left + Math.Min(r.Width / 2, 60)), (int)(r.Top + r.Height / 2));
        Native.SetCursorPos(old.X, old.Y);
        return true;
    }

    public static bool OpenThread(IntPtr hwnd, string name)
    {
        var item = SidebarItems(hwnd).FirstOrDefault(c => c.Name == name);
        return item != null && Activate(item.Element);
    }

    public static bool StartNewChat(IntPtr hwnd, TargetApp t)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var wr = root.Current.BoundingRectangle;
        var all = FindClickables(root).Cast<AutomationElement>().ToList();
        foreach (var wanted in t.NewChatButtonNames)
        {
            var hit = all
                .Select(e => { try { return (e, c: e.Current); } catch { return (e, c: (AutomationElement.AutomationElementInformation?)null); } })
                .Where(x => x.c is { } c && string.Equals(c.Name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
                            && !c.BoundingRectangle.IsEmpty && c.BoundingRectangle.Top >= wr.Top && c.BoundingRectangle.Bottom <= wr.Bottom)
                .OrderBy(x => x.c!.Value.BoundingRectangle.Top).ThenBy(x => x.c!.Value.BoundingRectangle.Left)
                .FirstOrDefault();
            if (hit.e != null && Activate(hit.e)) return true;
        }
        if (TryParseShortcut(t.NewChatShortcut, out var key, out var mods))
        {
            Native.SendChord(key, mods);
            return true;
        }
        return false;
    }

    static bool TryParseShortcut(string s, out ushort key, out ushort[] mods)
    {
        key = 0; mods = Array.Empty<ushort>();
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = new List<ushort>();
        foreach (var part in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": m.Add(Native.VK_CONTROL); break;
                case "shift": m.Add(Native.VK_SHIFT); break;
                case "alt": m.Add(Native.VK_MENU); break;
                case "win": m.Add(Native.VK_LWIN); break;
                default:
                    if (part.Length == 1) key = (ushort)char.ToUpperInvariant(part[0]);
                    else if (part.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[1..], out var f)) key = (ushort)(0x6F + f);
                    break;
            }
        }
        mods = m.ToArray();
        return key != 0;
    }

    /// <summary>The chat input: the lowest wide, focusable text field in the lower part of the window.</summary>
    public static AutomationElement? FindComposer(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var wr = root.Current.BoundingRectangle;
        var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        AutomationElement? best = null; double bestBottom = double.MinValue;
        foreach (AutomationElement e in edits)
        {
            try
            {
                var c = e.Current;
                var r = c.BoundingRectangle;
                if (r.IsEmpty || !c.IsKeyboardFocusable || r.Width < 200) continue;
                if (r.Top < wr.Top + wr.Height * 0.35 || r.Bottom > wr.Bottom) continue; // composers live in the lower part
                if (r.Bottom > bestBottom) { best = e; bestBottom = r.Bottom; }
            }
            catch { }
        }
        return best;
    }

    /// <summary>Put keyboard focus in the chat input.</summary>
    public static bool FocusComposer(IntPtr hwnd)
    {
        try
        {
            var best = FindComposer(hwnd);
            if (best == null) return false;
            best.SetFocus();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("FocusComposer failed: " + ex.Message);
            return false;
        }
    }

    // ---------- clipboard + paste ----------

    static void WithClipboardRetry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (System.Runtime.InteropServices.COMException) when (i < 10) { Thread.Sleep(60); }
            catch (System.Runtime.InteropServices.ExternalException) when (i < 10) { Thread.Sleep(60); }
        }
    }

    public static void SetClipboardImage(string path)
    {
        var png = new MemoryStream();
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(png);
        png.Position = 0;

        var data = new System.Windows.DataObject();
        data.SetImage(bmp);             // CF_DIB / CF_BITMAP for classic apps
        data.SetData("PNG", png);       // what Chromium/Electron prefers
        // (No file-drop entry: some apps would attach the image twice.)
        WithClipboardRetry(() => System.Windows.Clipboard.SetDataObject(data, true));
    }

    public static void SetClipboardText(string text) =>
        WithClipboardRetry(() => System.Windows.Clipboard.SetDataObject(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, text), true));

    public static void PressPaste() => Native.SendChord(Native.VK_V, Native.VK_CONTROL);

    /// <summary>Block until the user has physically let go of Enter / modifiers so nothing leaks into the target app.</summary>
    public static async Task WaitForKeysReleasedAsync()
    {
        int[] keys = { Native.VK_RETURN, Native.VK_SHIFT, Native.VK_CONTROL, Native.VK_MENU, Native.VK_LWIN };
        for (int i = 0; i < 100 && keys.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0); i++)
            await Task.Delay(20);
    }
}
