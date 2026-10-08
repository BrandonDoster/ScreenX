// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenX;

/// The settings window. Writes screenx.json only when Save is pressed.
///
/// While it is open the tray unregisters every hotkey, so pressing the
/// combination being recorded does not take a screenshot instead.
sealed class SettingsWindow : Form
{
    static readonly (string value, string label)[] Afters =
    [
        ("editor", "Open the editor"),
        ("both", "Copy and save"),
        ("copy", "Copy to the clipboard only"),
        ("save", "Save only"),
    ];

    readonly HotkeyBox region = new(), screen = new(), scroll = new();
    readonly ComboBox after = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly TextBox folder = new() { Width = 360 };
    readonly CheckBox startup = new() { Text = "Start ScreenX when I sign in to Windows", AutoSize = true };
    readonly Label problem = new() { AutoSize = true, ForeColor = Color.IndianRed, MaximumSize = new Size(520, 0) };
    readonly Settings original;

    /// Set when Save wrote the file.
    public bool Saved { get; private set; }

    public SettingsWindow(Settings current, bool startsWithWindows)
    {
        original = current;
        Text = "ScreenX settings";
        Icon = Program.Icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 4) });
            control.Margin = new Padding(0, 4, 0, 4);
            grid.Controls.Add(control);
        }

        region.Text = current.RegionHotkey;
        screen.Text = current.ScreenHotkey;
        scroll.Text = current.ScrollHotkey;
        Row("Region", region);
        Row("Whole screen", screen);
        Row("Scrolling", scroll);
        grid.Controls.Add(new Label());
        grid.Controls.Add(new Label
        {
            Text = "Click a box and press the keys. Backspace clears it.",
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 8),
        });

        foreach (var (_, label) in Afters) after.Items.Add(label);
        var index = Array.FindIndex(Afters, a => a.value.Equals(current.AfterCapture?.Trim(), StringComparison.OrdinalIgnoreCase));
        after.SelectedIndex = index < 0 ? 0 : index;
        Row("After a capture", after);

        folder.Text = current.Folder;
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += async (_, _) => await Browse();
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        folderRow.Controls.Add(folder);
        folderRow.Controls.Add(browse);
        Row("Screenshots folder", folderRow);

        startup.Checked = startsWithWindows;
        grid.Controls.Add(new Label());
        grid.Controls.Add(startup);

        // The default link blue is unreadable on the dark theme's grey.
        var dark = Application.IsDarkModeEnabled;
        var file = new LinkLabel
        {
            Text = "Edit the settings file instead", AutoSize = true, Margin = new Padding(0, 12, 0, 0),
            LinkColor = dark ? Color.FromArgb(0x6c, 0xb6, 0xff) : SystemColors.HotTrack,
            ActiveLinkColor = dark ? Color.FromArgb(0x9c, 0xce, 0xff) : SystemColors.HotTrack,
        };
        file.LinkClicked += (_, _) => Tray.OpenSettingsFile(Settings.FilePath);
        grid.Controls.Add(new Label());
        grid.Controls.Add(file);
        grid.Controls.Add(new Label());
        grid.Controls.Add(problem);

        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => TrySave();
        cancel.Click += (_, _) => Close();
        AcceptButton = save;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        Controls.Add(grid);
        Controls.Add(buttons);
    }

    public bool StartWithWindows => startup.Checked;

    void TrySave()
    {
        var chosen = new Settings
        {
            RegionHotkey = region.Text,
            ScreenHotkey = screen.Text,
            ScrollHotkey = scroll.Text,
            AfterCapture = Afters[Math.Max(0, after.SelectedIndex)].value,
            Folder = Environment.ExpandEnvironmentVariables(folder.Text.Trim()),
        };
        var error = Validate(chosen);
        if (error != null)
        {
            problem.Text = error;
            return;
        }
        try
        {
            Settings.Save(chosen);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem.Text = $"Could not save: {e.Message}";
            return;
        }
        Saved = true;
        // Close, not DialogResult: this window is modeless (the tray keeps
        // running under it), and DialogResult closes only a modal one.
        Close();
    }

    /// Why these settings cannot be saved, or null.
    internal static string? Validate(Settings s)
    {
        var keys = new[] { (s.RegionHotkey, "Region"), (s.ScreenHotkey, "Whole screen"), (s.ScrollHotkey, "Scrolling") };
        foreach (var (text, name) in keys)
            if (!string.IsNullOrWhiteSpace(text) && !HotkeyWindow.TryParse(text, out _, out _))
                return $"{name}: \"{text}\" cannot be a hotkey. Use at least one of Control, Alt, Shift or Win with it.";
        var used = keys.Where(k => !string.IsNullOrWhiteSpace(k.Item1))
            .GroupBy(k => Normalise(k.Item1)).FirstOrDefault(g => g.Count() > 1);
        if (used != null) return $"{string.Join(" and ", used.Select(k => k.Item2))} have the same hotkey.";
        if (!Path.IsPathFullyQualified(s.Folder)) return "The screenshots folder must be a full path, such as C:\\Users\\you\\Pictures\\ScreenX.";
        return null;
    }

    /// The same combination written differently ("ctrl+shift+q", "Shift+Control+Q").
    static string Normalise(string text)
    {
        HotkeyWindow.TryParse(text, out var mods, out var key);
        return $"{mods}:{key}";
    }

    /// The folder picker runs in a separate copy of ScreenX that exits as soon
    /// as it is closed: the shell dialog loads Explorer extensions into its
    /// process, and in the resident one they would stay loaded for good.
    async Task Browse()
    {
        Enabled = false;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { "--pick-folder", folder.Text },
                RedirectStandardOutput = true,
                // Both ends agree on UTF-8, or a folder name outside ASCII comes back garbled.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
            };
            using var picker = Process.Start(start);
            if (picker == null) return;
            var chosen = (await picker.StandardOutput.ReadToEndAsync()).Trim();
            await picker.WaitForExitAsync();
            if (chosen.Length > 0) folder.Text = chosen;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            problem.Text = $"Could not open the folder picker: {e.Message}";
        }
        finally
        {
            Enabled = true;
            Activate();
        }
    }

    /// `ScreenX.exe --pick-folder <start>`: the picker on its own, printing
    /// the chosen folder.
    public static int PickFolder(string start)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Where ScreenX saves screenshots",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(start) ? start : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog() != DialogResult.OK) return 1;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Out.Write(dialog.SelectedPath);
        Console.Out.Flush();
        return 0;
    }

    /// A box that shows a hotkey and records a new one when keys are pressed.
    sealed class HotkeyBox : TextBox
    {
        public HotkeyBox()
        {
            ReadOnly = true;
            Width = 260;
            PlaceholderText = "None";
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            var key = e.KeyCode;
            if (key is Keys.Back or Keys.Delete && !e.Control && !e.Alt && !e.Shift)
            {
                Text = "";
                return;
            }
            if (key is Keys.Tab && !e.Control && !e.Alt) { e.Handled = false; return; }
            Record(key, e.Control, e.Alt, e.Shift);
        }

        // PrintScreen only ever arrives as a key-up.
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PrintScreen) Record(Keys.PrintScreen, e.Control, e.Alt, e.Shift);
            base.OnKeyUp(e);
        }

        // Alt and F10 combinations are system keys; take them before the
        // form uses them for its menu or mnemonics.
        // Control combinations too: the text box would take Control+A as
        // select-all before it could be recorded.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            const int WM_SYSKEYDOWN = 0x0104;
            if (msg.Msg == WM_SYSKEYDOWN || (keyData & Keys.Control) != 0)
            {
                OnKeyDown(new KeyEventArgs(keyData));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Enter and Esc with a modifier are hotkeys to record; on their own
        // they still press Save and Cancel.
        protected override bool ProcessDialogKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key is Keys.Enter or Keys.Escape && (keyData & Keys.Modifiers) != 0)
            {
                OnKeyDown(new KeyEventArgs(keyData));
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        void Record(Keys key, bool control, bool alt, bool shift)
        {
            var win = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
            var parts = new List<string>();
            if (control) parts.Add("Control");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");
            // Only modifiers so far: show them, wait for the key.
            if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            {
                Text = string.Join("+", parts) + "+";
                return;
            }
            parts.Add(key.ToString());
            Text = string.Join("+", parts);
        }

        // A half-typed combination ("Control+") is not kept.
        protected override void OnLeave(EventArgs e)
        {
            if (Text.EndsWith('+')) Text = "";
            base.OnLeave(e);
        }

        [DllImport("user32.dll")] static extern short GetKeyState(int key);
    }
}
