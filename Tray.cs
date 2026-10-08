// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Diagnostics;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace ScreenX;

/// The resident part: the tray icon, the hotkeys, and what happens to a
/// capture once it is taken.
sealed class Tray : ApplicationContext
{
    const int RegionId = 1, ScreenId = 2, ScrollId = 3;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly NotifyIcon icon;
    readonly HotkeyWindow hotkeys = new();
    readonly Overlay overlay = new();
    Settings settings = new();
    string hint = "";
    string? lastSaved;
    /// What clicking the notification on screen opens. Cleared by any other
    /// notification, so clicking an error does not open a screenshot.
    string? balloonFile;
    bool busy;

    public Tray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Capture region", null, (_, _) => Later(CaptureRegion, 0));
        menu.Items.Add("Capture region in 3 seconds", null, (_, _) => Later(CaptureRegion, 3000));
        menu.Items.Add("Scrolling capture", null, (_, _) => Later(CaptureScrolling, 0));
        menu.Items.Add(new ToolStripSeparator());
        var edit = menu.Items.Add("Edit last screenshot", null, (_, _) => Edit(lastSaved));
        menu.Items.Add("Open screenshots folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Edit settings", null, (_, _) => Shell(Settings.FilePath));
        menu.Items.Add("Reload settings", null, (_, _) => Reload());
        var startup = new ToolStripMenuItem("Start with Windows");
        startup.Click += (_, _) => SetStartup(!startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        menu.Opening += (_, _) =>
        {
            edit.Enabled = lastSaved != null && File.Exists(lastSaved);
            startup.Checked = StartsWithWindows();
        };

        icon = new NotifyIcon
        {
            Icon = Program.Icon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => OpenFolder();
        icon.BalloonTipClicked += (_, _) => Edit(balloonFile);
        hotkeys.Pressed += id =>
        {
            if (id == RegionId) CaptureRegion();
            else if (id == ScreenId) CaptureScreen();
            else if (id == ScrollId) CaptureScrolling();
        };
        // Created now, while nobody is waiting, so the first capture does not
        // pay for it.
        _ = overlay.Handle;
        Reload();
        // Start-up touches far more than idle ever needs: about 50 MB of
        // working set until the first capture trimmed it. Trimmed once, as
        // soon as the message loop has nothing to do.
        Application.Idle += TrimOnce;
    }

    static void TrimOnce(object? sender, EventArgs e)
    {
        Application.Idle -= TrimOnce;
        Capture.GiveMemoryBack();
    }

    /// From the tray menu: wait for the menu to finish closing, or it is in
    /// the photograph. The delayed entry is for capturing another program's
    /// open menu, which closes if anything takes the keyboard.
    static async void Later(Action action, int delay)
    {
        await Task.Delay(Math.Max(delay, 250));
        action();
    }

    void Reload()
    {
        (settings, var error) = Settings.Load();
        var problems = new List<string>();
        if (error != null) problems.Add(error);
        foreach (var (id, text) in new[]
                 {
                     (RegionId, settings.RegionHotkey), (ScreenId, settings.ScreenHotkey), (ScrollId, settings.ScrollHotkey),
                 })
        {
            var problem = hotkeys.Register(id, text);
            if (problem != null) problems.Add(problem);
        }
        hint = $"{settings.RegionHotkey} region\n{settings.ScreenHotkey} screen\n{settings.ScrollHotkey} scrolling";
        SetTooltip(null);
        problems.ForEach(Log);
        if (problems.Count > 0)
            Notify("ScreenX settings", string.Join("\n", problems), ToolTipIcon.Warning);
    }

    /// The tooltip carries the hotkeys, and the last capture's timing so the
    /// latency can be read without a debugger.
    void SetTooltip(string? timing)
    {
        var text = timing == null ? $"ScreenX\n{hint}" : $"ScreenX\n{hint}\n{timing}";
        icon.Text = text.Length > 127 ? text[..127] : text;
    }

    /// Freeze the screen and let the user choose part of it. Null if they
    /// cancelled; otherwise the caller owns the frame. `area` is relative to
    /// the frame, and `screen` is where the frame was read from.
    (Frame shot, Rectangle area, Rectangle screen)? Choose()
    {
        var clock = Stopwatch.StartNew();
        // Read once: everything below uses this, not a fresh reading that
        // could disagree with the pixels.
        var screen = SystemInformation.VirtualScreen;
        // Before the overlay exists on screen, or it is in its own photograph.
        var shot = Capture.Grab(screen);
        try
        {
            var windows = Capture.Windows(screen);
            var grabbed = clock.ElapsedMilliseconds;
            var chosen = overlay.Choose(shot, windows, clock, screen);
            var timing = $"last: capture {grabbed} ms, overlay {overlay.ShownAfterMs} ms";
            SetTooltip(timing);
            Log(timing);
            if (chosen is Rectangle area) return (shot, area, screen);
        }
        catch
        {
            shot.Dispose();
            throw;
        }
        shot.Dispose();
        return null;
    }

    void CaptureRegion()
    {
        // The overlay runs a modal loop, so the hotkey can arrive again inside it.
        if (busy) return;
        busy = true;
        try
        {
            if (Choose() is not { } picked) return;
            using (picked.shot)
                Deliver(picked.shot, picked.area, null);
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

    void CaptureScreen()
    {
        if (busy) return;
        busy = true;
        try
        {
            using var shot = Capture.Grab(Screen.FromPoint(Cursor.Position).Bounds);
            Deliver(shot, new Rectangle(0, 0, shot.Width, shot.Height), null);
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

    /// Choose the area as for a region (a hover picks the window), then scroll
    /// it until it stops moving. Esc or moving the mouse stops it early.
    async void CaptureScrolling()
    {
        if (busy) return;
        busy = true;
        try
        {
            if (Choose() is not { } picked) return;
            picked.shot.Dispose();
            var area = picked.area;
            area.Offset(picked.screen.Location);
            var outcome = await Scrolling.Run(area, Log);
            if (outcome == null)
            {
                Notify("Scrolling capture", "That area is too small to scroll.", ToolTipIcon.Warning);
                return;
            }
            using (outcome.Image)
                Deliver(outcome.Image, new Rectangle(0, 0, outcome.Image.Width, outcome.Image.Height),
                    $"{outcome.Image.Height} px tall, stopped because {outcome.Ended}");
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
        Notify("ScreenX", e.Message, ToolTipIcon.Error);
    }

    /// Opt-in: create %TEMP%\screenx.log to start recording, delete it to stop.
    static void Log(string line)
    {
        var path = Path.Combine(Path.GetTempPath(), "screenx.log");
        if (File.Exists(path)) File.AppendAllText(path, $"{DateTime.Now:s} {line}\n");
    }

    void Deliver(Frame shot, Rectangle rect, string? note)
    {
        var (copy, save, edit) = Actions(settings.AfterCapture);
        if (edit)
        {
            using var image = shot.ToBitmap(rect);
            new Editor(image, settings.Folder, null).Show();
            return;
        }
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
        var detail = note ?? $"{rect.Width} x {rect.Height}";
        // Only a saved file can be reopened; the editor reads it back from disk
        // rather than this process keeping a copy of every screenshot in case.
        if (path != null) Notify(title, $"{detail}\nClick to annotate", ToolTipIcon.None, path);
        else Notify(title, detail, ToolTipIcon.None);
    }

    /// What `AfterCapture` asks for. Case does not matter, and anything
    /// unrecognised saves: losing a screenshot to a typo in the settings is
    /// worse than an unwanted file.
    internal static (bool copy, bool save, bool edit) Actions(string? afterCapture) =>
        afterCapture?.Trim().ToLowerInvariant() switch
        {
            "editor" => (false, false, true),
            "copy" => (true, false, false),
            "both" => (true, true, false),
            _ => (false, true, false),
        };

    void Notify(string title, string text, ToolTipIcon kind, string? file = null)
    {
        balloonFile = file;
        icon.ShowBalloonTip(kind == ToolTipIcon.None ? 3000 : 5000, title, text, kind);
    }

    void Edit(string? file)
    {
        // Not over a scrolling capture in progress, which would photograph it.
        if (busy || file == null || !File.Exists(file)) return;
        try
        {
            using var image = Images.Open(file);
            new Editor(image, settings.Folder, file).Show();
        }
        catch (Exception e) when (Images.IsLoadFailure(e))
        {
            Notify("ScreenX", $"Could not open {Path.GetFileName(file)}: {e.Message}", ToolTipIcon.Error);
        }
    }

    /// ExitThread alone tears the thread down without asking the editors, so
    /// unsaved annotations were lost without the prompt. Each is asked to
    /// close first, and any one of them can keep the program running.
    void Quit()
    {
        // A scrolling capture still has a continuation waiting; ending the
        // thread under it loses the capture and strands the cursor. Reaching
        // the tray menu moved the mouse, which already stops it.
        if (busy)
        {
            Later(Quit, 500);
            return;
        }
        foreach (var editor in Application.OpenForms.OfType<Editor>().ToList())
        {
            editor.Close();
            if (!editor.IsDisposed) return;
        }
        ExitThread();
    }

    public static string UniquePath(string folder, string stem)
    {
        var path = Path.Combine(folder, stem + ".png");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}).png");
        return path;
    }

    /// Through explorer.exe with the path as one argument, rather than handing
    /// the path to the shell to interpret: a folder named like a shell
    /// junction would otherwise open whatever the junction points at.
    void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { settings.Folder } })?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Notify("ScreenX", $"Could not open the screenshots folder: {e.Message}", ToolTipIcon.Error);
        }
    }

    /// The settings file in its associated editor, or Notepad if .json has no
    /// association.
    void Shell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path } })?.Dispose();
        }
    }

    static string RunCommand => $"\"{Environment.ProcessPath}\"";

    /// Only true when the entry points at this copy, so a build in another
    /// folder does not show a tick for an entry that starts something else.
    static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue("ScreenX") as string, RunCommand, StringComparison.OrdinalIgnoreCase);
    }

    void SetStartup(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue("ScreenX", RunCommand);
            else key.DeleteValue("ScreenX", false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Notify("ScreenX", $"Could not change the startup entry: {e.Message}", ToolTipIcon.Error);
        }
    }

    protected override void ExitThreadCore()
    {
        icon.Visible = false;
        hotkeys.DestroyHandle();
        base.ExitThreadCore();
    }
}
