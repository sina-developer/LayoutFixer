using System.Globalization;
using System.Runtime.InteropServices;

namespace LayoutFixer;

sealed record Key(uint Vk, uint Scan, bool Shift, string Typed);

/// Tracks what you type, spots wrong-layout words, and fixes them on Tab.
/// Same behaviour as the Mac version's Fixer.
sealed class Fixer
{
    const uint VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_DELETE = 0x2E;
    static readonly HashSet<uint> ResetKeys = [VK_RETURN, VK_ESCAPE, VK_DELETE, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];
    static readonly HashSet<uint> ModifierKeys = [0x10, 0x11, 0x12, 0x14, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    readonly Dictionaries dicts;
    readonly Popup popup;
    readonly Control ui;
    readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 10_000 };
    public bool Enabled = true;

    List<Key> word = [];
    /// The word finished just before `word`, if only a single space separates them.
    List<Key> previous = [];
    /// Keystrokes from the first wrong word up to now, while the popup is showing.
    List<Key> run = [];
    IntPtr runHkl;
    int runWords;

    public Fixer(Dictionaries dicts, Popup popup, Control ui)
    {
        this.dicts = dicts;
        this.popup = popup;
        this.ui = ui;
        hideTimer.Tick += (_, _) => Reset();
    }

    public void Reset()
    {
        word.Clear();
        previous.Clear();
        run.Clear();
        runHkl = IntPtr.Zero;
        runWords = 0;
        hideTimer.Stop();
        popup.HideIt();
    }

    /// Returns true to swallow the key.
    public bool HandleKey(Native.KBDLLHOOKSTRUCT k)
    {
        if (!Enabled) return false;
        uint vk = k.vkCode;
        if (ModifierKeys.Contains(vk)) return false;
        if (Native.IsDown(0x11) || Native.IsDown(0x12) || Native.IsDown(0x5B) || Native.IsDown(0x5C)) { Reset(); return false; }

        if (vk == VK_TAB)
        {
            if (run.Count > 0) { Apply(); return true; }
            Reset();
            return false;
        }
        if (ResetKeys.Contains(vk)) { Reset(); return false; }
        if (vk == VK_BACK)
        {
            if (word.Count > 0) word.RemoveAt(word.Count - 1);
            if (run.Count > 0)
            {
                run.RemoveAt(run.Count - 1);
                if (run.Count == 0) Reset(); else Refresh();
            }
            return false;
        }
        if (vk == VK_SPACE)
        {
            var space = new Key(vk, k.scanCode, false, " ");
            if (run.Count > 0) run.Add(space);
            FinishWord(space);
            return false;
        }

        var current = Layouts.Current();
        bool shift = Native.IsDown(0x10);
        var typed = current?.Character(vk, k.scanCode, shift);
        if (string.IsNullOrEmpty(typed) || typed.Any(char.IsControl)) { Reset(); return false; }
        var key = new Key(vk, k.scanCode, shift, typed);
        word.Add(key);
        if (run.Count > 0) { run.Add(key); Refresh(); }
        return false;
    }

    void FinishWord(Key space)
    {
        var finished = word.ToList();
        try
        {
            if (word.Count == 0 || Layouts.Current() is not { } current) return;
            if (run.Count > 0)
            {
                // Already suggesting: keep extending, unless you switched layouts.
                runWords++;
                if (current.Hkl != runHkl || runWords > 12) Reset(); else Refresh();
                return;
            }
            if (Layouts.Other(current) is not { } other) return;
            if (LooksWrong(word, current, other))
            {
                // A short word just before (like هد -> "in") is usually part of the same mistake.
                var before = LooksWrong(previous, current, other, 1) ? [.. previous, space] : new List<Key>();
                run = [.. before, .. word, space];
                runHkl = current.Hkl;
                runWords = 1;
                Refresh();
            }
        }
        finally
        {
            word.Clear();
            previous = finished;
        }
    }

    bool LooksWrong(List<Key> keys, Layout current, Layout other, int minLetters = 3) =>
        keys.Count > 0 && dicts.IsMistake(string.Concat(keys.Select(k => k.Typed)), current.Lang, Convert(keys, other), minLetters);

    static string Convert(IEnumerable<Key> keys, Layout layout) =>
        string.Concat(keys.Select(k => k.Vk == VK_SPACE ? " " : layout.Character(k.Vk, k.Scan, k.Shift) ?? k.Typed));

    Layout? Target() => Layouts.Current() is { } c ? Layouts.Other(c) : null;

    void Refresh()
    {
        if (Layouts.Current() is not { } current || Layouts.Other(current) is not { } target) return;
        var typed = string.Concat(run.Select(k => k.Typed)).Trim();
        popup.ShowSuggestion(typed, Convert(run, target).Trim(), current.Lang, target.Lang);
        hideTimer.Stop();
        hideTimer.Start();
    }

    void Apply()
    {
        if (Target() is not { } target) { Reset(); return; }
        var text = Convert(run, target);
        int deletes = run.Sum(k => new StringInfo(k.Typed).LengthInTextElements);
        var window = Native.GetForegroundWindow();
        Reset();
        // Inject after the hook returns, so the Tab is fully swallowed first.
        ui.BeginInvoke(() =>
        {
            Native.PostMessage(window, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, target.Hkl);
            var inputs = new List<Native.INPUT>();
            for (int i = 0; i < deletes; i++) { inputs.Add(KeyInput((ushort)VK_BACK, 0, 0)); inputs.Add(KeyInput((ushort)VK_BACK, 0, Native.KEYEVENTF_KEYUP)); }
            foreach (char ch in text)
            {
                inputs.Add(KeyInput(0, ch, Native.KEYEVENTF_UNICODE));
                inputs.Add(KeyInput(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
            }
            var array = inputs.ToArray();
            Native.SendInput((uint)array.Length, array, Marshal.SizeOf<Native.INPUT>());
        });
    }

    static Native.INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Native.Marker } },
    };
}
