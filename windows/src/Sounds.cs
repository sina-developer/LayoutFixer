using System.IO;
using System.Media;
using Microsoft.Win32;

namespace LayoutFixer;

/// The few user preferences the app remembers (a single flag in the current user's registry, nothing else).
static class Settings
{
    const string KeyPath = @"Software\LayoutFixer";

    /// Play a soft pop when the suggestion popup appears. On by default.
    public static bool PopSound
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue("PopSound") is not int v || v != 0;
            }
            catch { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue("PopSound", value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { /* the setting just won't persist */ }
        }
    }
}

/// The "pop" that tells you the popup appeared. It is generated here, so there is no sound file to ship.
static class Sounds
{
    static SoundPlayer? player;

    public static void Pop()
    {
        try
        {
            player ??= Create();
            player.Play(); // asynchronous: returns immediately
        }
        catch { /* no audio device: stay silent */ }
    }

    /// A short sine blip whose pitch drops quickly and fades out, like a bubble pop.
    static SoundPlayer Create()
    {
        const int rate = 44100;
        int samples = rate * 130 / 1000;
        var pcm = new short[samples];
        double phase = 0;
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / rate;
            double freq = 260 + 700 * Math.Exp(-t * 30);
            phase += 2 * Math.PI * freq / rate;
            double attack = Math.Min(1, t / 0.003);
            double release = Math.Min(1, (samples - i) / (rate * 0.015)); // short fade so the end never clicks
            double envelope = attack * release * Math.Exp(-t * 28);
            pcm[i] = (short)(Math.Sin(phase) * envelope * 0.38 * short.MaxValue);
        }

        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples * 2;
            w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(dataBytes);
            foreach (var s in pcm) w.Write(s);
        }
        stream.Position = 0;
        var p = new SoundPlayer(stream);
        p.Load();
        return p;
    }
}
