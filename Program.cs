// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

// ScreenX: one resident process. A tray icon, global hotkeys, and an overlay
// window that is created once at launch and shown on demand. Between
// screenshots nothing runs: no timers, no polling, no background GC thread.
// The process sits in GetMessage until Windows hands it a hotkey.

using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenX;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // An editor on its own, for "Open with". Not the resident copy, so it
        // ends when the window closes.
        if (args is ["--edit", var file])
        {
            Bitmap image;
            try
            {
                image = new Bitmap(file);
            }
            catch (Exception e) when (e is IOException or ArgumentException)
            {
                MessageBox.Show($"Could not open {file}: {e.Message}", "ScreenX", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            using (image)
                Application.Run(new Editor(image, Path.GetDirectoryName(Path.GetFullPath(file))!, file));
            return 0;
        }
        // A second copy would fail to register the hotkeys and sit there doing
        // nothing, which looks exactly like a broken first copy.
        using var single = new Mutex(true, @"Local\ScreenX", out bool first);
        if (!first) return 0;
        Application.Run(new Tray());
        return 0;
    }
}

sealed class Settings
{
    public string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenX");
    public string RegionHotkey { get; set; } = "Control+Alt+A";
    public string ScreenHotkey { get; set; } = "Control+Alt+F";
    public string ScrollHotkey { get; set; } = "Control+Alt+S";
    /// "both", "copy", "save" or "editor".
    public string AfterCapture { get; set; } = "both";

    // Not settings.json: the Rust build owns that name in the same folder and
    // rewrites it whole, which would erase these keys.
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenX", "screenx.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static (Settings, string? error) Load() => Load(FilePath);

    /// Never writes over an existing file. A typo in it gives the defaults for
    /// this run and an error, not a file silently replaced with the defaults.
    /// A missing file is created with the defaults, so there is something to
    /// open and edit.
    public static (Settings, string? error) Load(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = new Settings();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(fresh, Json));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (fresh, $"{Path.GetFileName(path)} could not be created: {e.Message}");
            }
            return (fresh, null);
        }
        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();
            // An explicit null in the file must not become a null folder.
            if (string.IsNullOrWhiteSpace(loaded.Folder)) loaded.Folder = new Settings().Folder;
            return (loaded, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return (new(), $"{Path.GetFileName(path)} could not be read, using defaults: {e.Message}");
        }
    }
}

/// A message-only window: it receives WM_HOTKEY and is never drawn.
sealed class HotkeyWindow : NativeWindow
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    public event Action<int>? Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE

    /// Returns why it failed, or null. An empty string means "no hotkey".
    public string? Register(int id, string text)
    {
        UnregisterHotKey(Handle, id);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!TryParse(text, out var mods, out var key))
            return $"\"{text}\" is not a hotkey. Use e.g. Control+Alt+A or PrintScreen.";
        if (!RegisterHotKey(Handle, id, mods | MOD_NOREPEAT, (uint)key))
            return $"{text} is already taken by another program.";
        return null;
    }

    /// "Control+Alt+A", "Shift+F5", "PrintScreen". Key names are WinForms
    /// `Keys` names, so every key on the keyboard is reachable.
    public static bool TryParse(string text, out uint mods, out Keys key)
    {
        mods = 0;
        key = Keys.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "control" or "ctrl": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    // Enum.TryParse also takes numbers and comma lists; neither
                    // is a key name anyone means.
                    if (key != Keys.None || !char.IsLetter(part[0]) || part.Contains(',')
                        || !Enum.TryParse(part, true, out key) || (key & Keys.Modifiers) != 0
                        || key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
                            or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey
                            or Keys.LMenu or Keys.RMenu)
                        return false;
                    break;
            }
        }
        // A bare letter would be taken from every other program. Keys nobody
        // types with are fine alone.
        return key != Keys.None
            && (mods != 0 || key is Keys.PrintScreen or Keys.Pause or (>= Keys.F13 and <= Keys.F24));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) Pressed?.Invoke((int)m.WParam);
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
