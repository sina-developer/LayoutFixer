using System.IO;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LayoutFixer;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // LayoutFixer.exe --test "صشدف لقشذ"   checks detection without running the app.
        if (args.Length >= 2 && args[0] == "--test") return TestMode.Run(args[1]);

        using var mutex = new Mutex(true, "LayoutFixer.SingleInstance", out bool first);
        if (!first) return 0;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }
}

/// Tray icon + global keyboard/mouse hooks. The Windows counterpart of the Mac menu bar app.
sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly Control ui = new();
    readonly Fixer fixer;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem enabledItem = new("Enabled") { Checked = true };
    readonly ToolStripMenuItem soundItem = new("Pop sound when a fix is suggested");
    readonly ToolStripMenuItem startupItem = new("Start with Windows");
    readonly ToolStripMenuItem layoutsItem = new("Add both an English and a Persian keyboard layout") { Enabled = false };
    // Delegates are kept in fields so the garbage collector doesn't free them while Windows calls them.
    readonly Native.HookProc keyboardProc, mouseProc;
    readonly Native.WinEventProc foregroundProc;
    readonly IntPtr keyboardHook, mouseHook, foregroundHook;

    public TrayApp()
    {
        _ = ui.Handle; // create the handle so BeginInvoke works
        var dicts = new Dictionaries(Path.Combine(AppContext.BaseDirectory, "data"));
        var popup = new Popup { PopSound = Settings.PopSound };
        fixer = new Fixer(dicts, popup, ui);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("LayoutFixer") { Enabled = false });
        menu.Items.Add(layoutsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(enabledItem);
        menu.Items.Add(soundItem);
        menu.Items.Add(startupItem);
        soundItem.Checked = popup.PopSound;
        soundItem.Click += (_, _) =>
        {
            popup.PopSound = soundItem.Checked = !soundItem.Checked;
            Settings.PopSound = popup.PopSound;
            if (popup.PopSound) Sounds.Pop(); // preview what it sounds like
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit LayoutFixer", null, (_, _) => ExitThread());
        enabledItem.Click += (_, _) => { fixer.Enabled = enabledItem.Checked = !enabledItem.Checked; fixer.Reset(); };
        startupItem.Click += (_, _) => SetStartWithWindows(!startupItem.Checked);
        menu.Opening += (_, _) =>
        {
            layoutsItem.Visible = Layouts.Enabled().Count < 2;
            startupItem.Checked = StartsWithWindows();
        };

        tray = new NotifyIcon { Icon = LoadIcon(), Text = "LayoutFixer", ContextMenuStrip = menu, Visible = true };

        keyboardProc = OnKeyboard;
        mouseProc = OnMouse;
        foregroundProc = (_, _, _, _, _, _, _) => fixer.Reset();
        var module = Native.GetModuleHandle(null);
        keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
        foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundProc, 0, 0, 0);
    }

    IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN))
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            // Ignore what we type ourselves and Unicode packets from other tools.
            if (k.dwExtraInfo != Native.Marker && k.vkCode != Native.VK_PACKET)
            {
                try { if (fixer.HandleKey(k)) return 1; }
                catch { fixer.Reset(); }
            }
        }
        return Native.CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == Native.WM_LBUTTONDOWN || wParam == Native.WM_RBUTTONDOWN || wParam == Native.WM_MBUTTONDOWN))
            fixer.Reset();
        return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    static Icon LoadIcon()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("LayoutFixer.tray.ico");
        return stream != null ? new Icon(stream, SystemInformation.SmallIconSize) : SystemIcons.Application;
    }

    static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("LayoutFixer") is string;
    }

    static void SetStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue("LayoutFixer", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("LayoutFixer", false);
    }

    protected override void ExitThreadCore()
    {
        Native.UnhookWindowsHookEx(keyboardHook);
        Native.UnhookWindowsHookEx(mouseHook);
        Native.UnhookWinEvent(foregroundHook);
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

/// Command-line check of the detection rules, using the layouts installed on this PC.
static class TestMode
{
    public static int Run(string text)
    {
        Native.AttachConsole(-1);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var dicts = new Dictionaries(Path.Combine(AppContext.BaseDirectory, "data"));
        var layouts = Layouts.Enabled();
        Console.WriteLine("layouts: " + string.Join(", ", layouts.Select(l => $"{l.Key}={l.Value.Hkl:X}")));
        bool persian = text.Any(c => c >= 0x0600 && c <= 0x06FF);
        if (!layouts.TryGetValue(persian ? "fa" : "en", out var from) || !layouts.TryGetValue(persian ? "en" : "fa", out var to))
        {
            Console.WriteLine("need both an English and a Persian layout installed");
            return 1;
        }
        // Reverse map: character -> physical key on the source layout.
        var keyFor = new Dictionary<string, (uint vk, uint scan, bool shift)>();
        for (uint vk = 0x20; vk < 0xFF; vk++)
            foreach (var shift in new[] { false, true })
            {
                uint scan = Native.MapVirtualKeyEx(vk, 0, from.Hkl);
                if (from.Character(vk, scan, shift) is { } c) keyFor.TryAdd(c, (vk, scan, shift));
            }
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var converted = string.Concat(w.Select(ch =>
                keyFor.TryGetValue(ch.ToString(), out var k) ? to.Character(k.vk, k.scan, k.shift) ?? ch.ToString() : ch.ToString()));
            bool fix = dicts.IsMistake(w, from.Lang, converted), shortFix = dicts.IsMistake(w, from.Lang, converted, 1);
            Console.WriteLine($"{w} -> {converted}  {(fix ? "FIX" : shortFix ? "fix-if-next-is-wrong" : "ok")}");
        }
        return 0;
    }
}
