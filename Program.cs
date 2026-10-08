// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

// ScreenX: one resident process. A tray icon, global hotkeys, and an overlay
// window that is created once at launch and shown on demand. Between
// screenshots nothing runs: no timers, no polling, no background GC thread.
// The process sits in GetMessage until Windows hands it a hotkey.

using System.Runtime.InteropServices;
using System.Text.Json;

// Every native library this program uses is a Windows one. Loading them from
// System32 only means a DLL of the same name beside ScreenX.exe (Downloads,
// say) is never picked up instead.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ScreenX;

static class Program
{
    /// Loaded once and shared by the tray and every editor, rather than a new
    /// icon handle per window that only the finalizer would release.
    public static readonly Icon Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)!;

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Follow the Windows light or dark setting, so the editor and the tray
        // menu match everything else on the desktop.
        Application.SetColorMode(SystemColorMode.System);
        // The settings window's folder picker, run apart so the shell
        // extensions it loads leave with it. See SettingsWindow.Browse.
        if (args is ["--pick-folder", var start]) return SettingsWindow.PickFolder(start);
        // An editor on its own, for "Open with". Not the resident copy, so it
        // ends when the window closes.
        if (args is ["--edit", var file])
        {
            Editor editor;
            try
            {
                // The editor copies the pixels, so the decoded image goes as
                // soon as it is built.
                using var image = Images.Open(file);
                editor = new Editor(image, Path.GetDirectoryName(Path.GetFullPath(file))!, file);
            }
            catch (Exception e) when (Images.IsLoadFailure(e))
            {
                MessageBox.Show($"Could not open {file}: {e.Message}", "ScreenX", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            Application.Run(editor);
            return 0;
        }
        // A second copy would fail to register the hotkeys and sit there doing
        // nothing, which looks exactly like a broken first copy.
        using var single = new Mutex(true, @"Local\ScreenX", out bool first);
        // Starting it again while it runs opens its settings window: the usual
        // answer to "I launched it and nothing happened". Optional: if another
        // program holds the name as something else, this just goes without.
        EventWaitHandle? reopen = null;
        try
        {
            reopen = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ScreenX.Settings");
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
        }
        using (reopen)
        {
            if (!first)
            {
                reopen?.Set();
                return 0;
            }
            Application.Run(new Tray(reopen));
        }
        return 0;
    }
}

/// Opening image files from disk, which "Open with" makes reachable from any
/// downloaded file.
static class Images
{
    /// PNG, JPEG, BMP and GIF only, by their first bytes rather than the name.
    /// GDI+ would also parse EMF, WMF and TIFF, whose decoders have the
    /// longest record of security fixes, and nothing here needs them.
    ///
    /// The file is read once and decoded from that copy, so what was checked
    /// is what is decoded: opening it twice let a file that changed in
    /// between (a network share, another program) reach the other decoders.
    /// And the result is a copy, so the file is not held open: GDI+ keeps a
    /// file-backed image's file locked, and Save could not overwrite it.
    public static Bitmap Open(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException($"larger than {MaxFileBytes / (1 << 20)} MB");
        var bytes = File.ReadAllBytes(path);
        if (!IsSupported(bytes)) throw new InvalidDataException("not a PNG, JPEG, BMP or GIF image");
        using var stream = new MemoryStream(bytes, writable: false);
        using var decoded = new Bitmap(stream);
        // A small file can declare enormous dimensions; the editor would then
        // allocate two images that size.
        if ((long)decoded.Width * decoded.Height > MaxPixels) throw new InvalidDataException($"{decoded.Width} x {decoded.Height} is too large to edit");
        return new Bitmap(decoded);
    }

    const long MaxFileBytes = 512L << 20;
    /// Beyond a scrolling capture at its own limit, with room to spare.
    const long MaxPixels = 100_000_000;

    public static bool IsSupported(ReadOnlySpan<byte> head) =>
        head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G'])
        || head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
        || head.StartsWith("BM"u8)
        || head.StartsWith("GIF8"u8);

    /// GDI+ reports a corrupt image as OutOfMemoryException.
    public static bool IsLoadFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException
            or System.Runtime.InteropServices.ExternalException;
}

sealed class Settings
{
    public string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenX");
    public string RegionHotkey { get; set; } = "Control+Shift+Q";
    /// None by default: both are on the tray menu, and every global hotkey
    /// is a key taken from every other program.
    public string ScreenHotkey { get; set; } = "";
    public string ScrollHotkey { get; set; } = "";
    /// "editor", "both" (copy and save), "copy" or "save".
    public string AfterCapture { get; set; } = "editor";

    // Not settings.json: the Rust build owns that name in the same folder and
    // rewrites it whole, which would erase these keys.
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenX", "screenx.json");

    /// Relaxed escaping: the file is edited by hand, and the default writes
    /// every "+" in a hotkey as +.
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// Write settings the user chose in the settings window. The one place
    /// the file is written over, and only on an explicit Save.
    public static void Save(Settings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        File.WriteAllText(partial, JsonSerializer.Serialize(settings, Json));
        File.Move(partial, path, overwrite: true);
    }

    public static void Save(Settings settings) => Save(settings, FilePath);

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
            loaded.RegionHotkey ??= "";
            loaded.ScreenHotkey ??= "";
            loaded.ScrollHotkey ??= "";
            loaded.AfterCapture ??= "editor";
            // An explicit null in the file must not become a null folder, and
            // a relative one would land wherever the working directory is:
            // System32, when Windows starts the program.
            var folder = Environment.ExpandEnvironmentVariables(loaded.Folder ?? "");
            if (!Path.IsPathFullyQualified(folder))
            {
                var fallback = new Settings().Folder;
                var problem = string.IsNullOrWhiteSpace(folder) ? null
                    : $"Folder \"{loaded.Folder}\" is not a full path; using {fallback}.";
                loaded.Folder = fallback;
                return (loaded, problem);
            }
            loaded.Folder = folder;
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
