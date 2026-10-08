// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

// ScreenX: one resident process. A tray icon, two global hotkeys, and an
// overlay window that is created once at launch and shown on demand. Between
// screenshots nothing runs: no timers, no polling, no background GC thread.
// The process sits in GetMessage until Windows hands it a hotkey.

using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenX;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--selftest"]) return SelfTest();
        // A second copy would fail to register the hotkeys and sit there doing
        // nothing, which looks exactly like a broken first copy.
        using var single = new Mutex(true, @"Local\ScreenX", out bool first);
        if (!first) return 0;
        ApplicationConfiguration.Initialize();
        Application.Run(new Tray());
        return 0;
    }

    /// `ScreenX.exe --selftest`; the exit code is the number of failures.
    static int SelfTest()
    {
        var cases = new (string text, bool ok)[]
        {
            ("Control+Alt+A", true), ("ctrl + shift + F5", true), ("PrintScreen", true), ("Win+D1", true),
            ("A", false), ("", false), ("Control+Alt", false), ("Control+A+B", false),
            ("Control+65", false), ("Control+Banana", false),
        };
        var failures = cases.Count(c => HotkeyWindow.TryParse(c.text, out _, out _) != c.ok);
        HotkeyWindow.TryParse("Control+Alt+A", out var mods, out var key);
        if (mods != 3 || key != Keys.A) failures++;
        return failures;
    }
}

sealed class Settings
{
    public string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScreenX");
    public string RegionHotkey { get; set; } = "Control+Alt+A";
    public string ScreenHotkey { get; set; } = "Control+Alt+F";
    /// "both", "copy" or "save".
    public string AfterCapture { get; set; } = "both";

    // Not settings.json: the Rust build owns that name in the same folder and
    // rewrites it whole, which would erase these keys.
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenX", "screenx.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// Never writes over an existing file. A typo in it gives the defaults for
    /// this run and an error, not a file silently replaced with the defaults.
    public static (Settings, string? error) Load()
    {
        if (!File.Exists(FilePath))
        {
            var fresh = new Settings();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(fresh, Json));
            return (fresh, null);
        }
        try
        {
            return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(), null);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return (new(), $"screenx.json could not be read, using defaults: {e.Message}");
        }
    }
}

sealed class Tray : ApplicationContext
{
    const int RegionId = 1, ScreenId = 2;

    readonly NotifyIcon icon;
    readonly HotkeyWindow hotkeys = new();
    readonly Overlay overlay = new();
    Settings settings = new();
    string hint = "";
    string? lastSaved;
    bool busy;

    public Tray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open screenshots folder", null, (_, _) => OpenFolder());
        menu.Items.Add("Edit settings", null, (_, _) => Shell(Settings.FilePath));
        menu.Items.Add("Reload settings", null, (_, _) => Reload());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => OpenFolder();
        icon.BalloonTipClicked += (_, _) =>
        {
            if (lastSaved != null) Process.Start("explorer.exe", $"/select,\"{lastSaved}\"");
        };
        hotkeys.Pressed += id =>
        {
            if (id == RegionId) CaptureRegion();
            else if (id == ScreenId) CaptureScreen();
        };
        // Created now, while nobody is waiting, so the first capture does not
        // pay for it.
        _ = overlay.Handle;
        Reload();
    }

    void Reload()
    {
        (settings, var error) = Settings.Load();
        var problems = new List<string>();
        if (error != null) problems.Add(error);
        foreach (var (id, text) in new[] { (RegionId, settings.RegionHotkey), (ScreenId, settings.ScreenHotkey) })
        {
            var problem = hotkeys.Register(id, text);
            if (problem != null) problems.Add(problem);
        }
        hint = $"{settings.RegionHotkey} region, {settings.ScreenHotkey} screen";
        SetTooltip(null);
        problems.ForEach(Log);
        if (problems.Count > 0)
            icon.ShowBalloonTip(5000, "ScreenX settings", string.Join("\n", problems), ToolTipIcon.Warning);
    }

    /// The tooltip carries the hotkeys, and the last capture's timing so the
    /// latency can be read without a debugger.
    void SetTooltip(string? timing)
    {
        var text = timing == null ? $"ScreenX\n{hint}" : $"ScreenX\n{hint}\n{timing}";
        icon.Text = text.Length > 127 ? text[..127] : text;
    }

    void CaptureRegion()
    {
        // The overlay runs a modal loop, so the hotkey can arrive again inside it.
        if (busy) return;
        busy = true;
        var clock = Stopwatch.StartNew();
        try
        {
            var area = SystemInformation.VirtualScreen;
            // Before the overlay exists on screen, or it is in its own photograph.
            using var shot = Capture.Grab(area);
            var windows = Capture.Windows(area);
            var grabbed = clock.ElapsedMilliseconds;

            var chosen = overlay.Choose(shot, windows, clock);
            var timing = $"last: capture {grabbed} ms, overlay {overlay.ShownAfterMs} ms";
            SetTooltip(timing);
            Log(timing);
            if (chosen is Rectangle rect) Deliver(shot, rect);
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            busy = false;
            Capture.GiveMemoryBack();
        }
    }

    void Fail(Exception e)
    {
        Log(e.ToString());
        icon.ShowBalloonTip(5000, "ScreenX", e.Message, ToolTipIcon.Error);
    }

    /// Opt-in: create %TEMP%\screenx.log to start recording, delete it to stop.
    static void Log(string line)
    {
        var path = Path.Combine(Path.GetTempPath(), "screenx.log");
        if (File.Exists(path)) File.AppendAllText(path, $"{DateTime.Now:s} {line}\n");
    }

    void CaptureScreen()
    {
        if (busy) return;
        busy = true;
        try
        {
            using var shot = Capture.Grab(Screen.FromPoint(Cursor.Position).Bounds);
            Deliver(shot, new Rectangle(0, 0, shot.Width, shot.Height));
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            busy = false;
            Capture.GiveMemoryBack();
        }
    }

    void Deliver(Frame shot, Rectangle rect)
    {
        var copy = settings.AfterCapture is "both" or "copy";
        var save = settings.AfterCapture is "both" or "save";
        string? path = null;
        if (save)
        {
            Directory.CreateDirectory(settings.Folder);
            path = UniquePath(settings.Folder, $"ScreenX_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
            using var image = shot.ToBitmap(rect);
            image.Save(path, ImageFormat.Png);
            lastSaved = path;
        }
        if (copy) shot.CopyToClipboard(rect, hotkeys.Handle);

        var title = (copy, save) switch
        {
            (true, true) => "Copied and saved",
            (true, false) => "Copied",
            _ => "Saved",
        };
        icon.ShowBalloonTip(2000, title, path != null ? Path.GetFileName(path) : $"{rect.Width} x {rect.Height}", ToolTipIcon.None);
    }

    static string UniquePath(string folder, string stem)
    {
        var path = Path.Combine(folder, stem + ".png");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}).png");
        return path;
    }

    void OpenFolder()
    {
        Directory.CreateDirectory(settings.Folder);
        Shell(settings.Folder);
    }

    static void Shell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    protected override void ExitThreadCore()
    {
        icon.Visible = false;
        hotkeys.DestroyHandle();
        base.ExitThreadCore();
    }
}

/// A message-only window: it receives WM_HOTKEY and is never drawn.
sealed class HotkeyWindow : NativeWindow
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    public event Action<int>? Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE

    /// Returns why it failed, or null.
    public string? Register(int id, string text)
    {
        UnregisterHotKey(Handle, id);
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
                    if (key != Keys.None || int.TryParse(part, out _)
                        || !Enum.TryParse(part, true, out key) || (key & Keys.Modifiers) != 0)
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
