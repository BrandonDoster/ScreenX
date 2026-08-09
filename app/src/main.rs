// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

// A tray application has no console to write to, and Windows opens one for a
// console subsystem binary — a black window that sits behind the tray icon for
// the life of the process. Debug builds keep it, because that is where the
// eprintln! diagnostics go.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! ScreenX: local screen capture, drawn natively.
//!
//! One process, one event loop, one window. The window is hidden until a
//! capture asks for it, and becomes the selection overlay in place — there is
//! no per-capture window creation, which is what made the webview build slow,
//! and no browser process tree, which is what made it heavy.
//!
//! A winit event loop can only be run once per process on macOS, so the design
//! is not optional: the app is persistent and the overlay is a state it enters.

mod edits;
mod editor;
mod overlay;
mod render;
mod tray;

use std::sync::mpsc::{Receiver, Sender};

use eframe::egui;
use global_hotkey::{
    hotkey::{Code, HotKey, Modifiers},
    GlobalHotKeyEvent, GlobalHotKeyManager,
};
use screenx_core::{capture, settings};
use tray_icon::{menu::MenuEvent, TrayIconEvent};

use editor::Editor;
use overlay::{Outcome, Overlay};

/// Something asked for a capture. Sent from the tray and hotkey threads.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Request {
    Region { delayed: bool },
    Fullscreen { delayed: bool },
    Quit,
}

/// What the window is currently for.
enum Mode {
    /// Hidden, waiting for a hotkey.
    Idle,
    Selecting(Overlay),
    Editing(Box<Editor>),
}

struct App {
    mode: Mode,
    requests: Receiver<Request>,
    /// Shown in the overlay's place after a save, briefly.
    status: Option<(String, std::time::Instant)>,
    /// The idle state has to be applied from inside the loop, not from the
    /// options: on Windows the window arrives visible, decorated and 800x600
    /// whatever `ViewportBuilder` asked for, which is the black box on screen
    /// at launch.
    settled: bool,
    /// Where the editor was last dragged to, so the next one opens there.
    editor_pos: Option<egui::Pos2>,
    /// Set once the user has actually asked to leave.
    ///
    /// The editor cancels the close it is sent, because closing the editor must
    /// not take the app down with it — but `Close` arrives by the same route, so
    /// without this a quit issued while the editor was open was cancelled too,
    /// and the only way out was Task Manager.
    quitting: bool,
}

impl App {
    fn new(cc: &eframe::CreationContext<'_>, requests: Receiver<Request>) -> Self {
        // The hotkey arrives on another thread, so it needs a way to wake a
        // loop that is otherwise asleep with nothing to draw.
        let ctx = cc.egui_ctx.clone();
        std::thread::spawn(move || loop {
            std::thread::sleep(std::time::Duration::from_millis(60));
            ctx.request_repaint();
        });

        Self {
            mode: Mode::Idle,
            requests,
            status: None,
            settled: false,
            editor_pos: None,
            quitting: false,
        }
    }

    /// Wait before reading the screen, when asked to.
    ///
    /// Photographing a menu needs this: the keyboard belongs to the menu's
    /// tracking loop, so the hotkey does not arrive until the menu closes.
    /// Only the delayed entries pay it — charging every capture for the delay
    /// made the ordinary one feel slow.
    fn wait(delayed: bool) {
        let ms = settings::get().capture_delay_ms;
        if delayed && ms > 0 {
            std::thread::sleep(std::time::Duration::from_millis(ms));
        }
    }

    /// Capture whichever monitor the work is for.
    ///
    /// ponytail: primary monitor only. A second monitor is a second viewport
    /// rather than a different design; add it when the overlay needs to span
    /// them. Reading only the one is also what keeps the wait short — the
    /// capture is a full framebuffer copy per display.
    fn shoot(&mut self, delayed: bool) -> Option<capture::MonitorShot> {
        Self::wait(delayed);
        match capture::capture_primary() {
            Ok(shot) => Some(shot),
            Err(err) => {
                self.report(err);
                None
            }
        }
    }

    /// Capture a whole monitor and save it, with no overlay in between.
    fn capture_fullscreen(&mut self, ctx: &egui::Context, delayed: bool) {
        let Some(shot) = self.shoot(delayed) else { return };
        let (image, name, density) = (shot.image, shot.name, shot.scale);
        self.deliver(ctx, image, name, density);
    }

    /// Read the screen and enter the overlay.
    fn begin_region(&mut self, ctx: &egui::Context, delayed: bool) {
        let Some(shot) = self.shoot(delayed) else { return };
        let bounds = shot.bounds;
        self.mode = Mode::Selecting(Overlay::new(ctx, shot));

        ctx.send_viewport_cmd(egui::ViewportCommand::OuterPosition(egui::pos2(
            bounds.x as f32,
            bounds.y as f32,
        )));
        ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(
            bounds.width as f32,
            bounds.height as f32,
        )));
        raise_and_activate(ctx);
        ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
        // Focus goes last, always. The commands are applied in the order they
        // are queued, and anything touching the window level afterwards carries
        // `SWP_NOACTIVATE` — which silently takes the activation back.
        ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
    }

    /// Leave the overlay and give the capture back.
    fn end_selection(&mut self, ctx: &egui::Context, outcome: Outcome) {
        let previous = std::mem::replace(&mut self.mode, Mode::Idle);
        self.go_idle(ctx);

        let Mode::Selecting(overlay) = previous else {
            return;
        };
        let Outcome::Selected(rect) = outcome else {
            return;
        };

        let Some(image) = capture::crop_to_rect(overlay.shot(), &rect) else {
            return self.report("that selection was too small to capture".into());
        };
        let density = overlay.shot().scale;
        self.deliver(ctx, image, overlay.shot().name.clone(), density);
    }

    /// What happens to a finished capture, per the settings file.
    fn deliver(&mut self, ctx: &egui::Context, image: image::RgbaImage, title: String, density: f32) {
        match settings::get().after_capture.as_str() {
            "editor" => self.open_editor(ctx, image, title, density),
            "clipboard" => self.report(match capture::copy_to_clipboard(&image) {
                Ok(()) => "copied".into(),
                Err(err) => err,
            }),
            _ => match capture::save_image(&image, &title) {
                Ok(path) => self.report(format!(
                    "saved {}",
                    path.file_name().unwrap_or_default().to_string_lossy()
                )),
                Err(err) => self.report(err),
            },
        }
    }

    fn open_editor(&mut self, ctx: &egui::Context, image: image::RgbaImage, title: String, density: f32) {
        let density = if density > 0.0 { density } else { 1.0 };
        let (width, height) = (image.width() as f32 / density, image.height() as f32 / density);
        self.mode = Mode::Editing(Box::new(Editor::new(image, title, density)));

        // Leave room for the toolbar without running off the screen.
        let bounds = capture::monitor_bounds();
        let (max_w, max_h) = bounds
            .first()
            .map(|b| (b.width as f32 * 0.95, b.height as f32 * 0.88))
            .unwrap_or((1400.0, 900.0));
        let scale = (max_w / width).min(max_h / height).min(1.0);

        let size = egui::vec2(
            (width * scale).max(560.0),
            (height * scale).max(360.0) + 72.0,
        );

        ctx.send_viewport_cmd(egui::ViewportCommand::Decorations(true));
        ctx.send_viewport_cmd(egui::ViewportCommand::Title("ScreenX Editor".into()));
        ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(size));
        ctx.send_viewport_cmd(egui::ViewportCommand::OuterPosition(editor_position(
            self.editor_pos,
            &bounds,
            size,
        )));
        raise_normal(ctx);
        ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
        // See `begin_region`: focus is only kept if nothing reorders the window
        // after it.
        ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
    }


    /// Put the window away and give back what it was holding.
    ///
    /// Dropping the mode releases the capture and its texture, but the window
    /// keeps a framebuffer the size it was last shown at — an editor left at
    /// full screen size held that for as long as the app ran. Shrinking it
    /// first is what actually returns the memory.
    fn go_idle(&mut self, ctx: &egui::Context) {
        self.mode = Mode::Idle;
        // Releases the uploaded capture on the GPU side. Dropping the handle
        // alone leaves it queued until egui next collects.
        ctx.forget_all_images();
        ctx.send_viewport_cmd(egui::ViewportCommand::Decorations(false));
        ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(1.0, 1.0)));
        park(ctx);
        // Textures are freed on the frame after their handle drops.
        ctx.request_repaint();
    }

    fn report(&mut self, message: String) {
        eprintln!("[screenx] {message}");
        self.status = Some((message, std::time::Instant::now()));
    }
}

impl eframe::App for App {
    /// Transparent, so nothing flashes while the window is hidden.
    fn clear_color(&self, _visuals: &egui::Visuals) -> [f32; 4] {
        [0.0, 0.0, 0.0, 0.0]
    }

    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        if !self.settled {
            self.settled = true;
            self.go_idle(ctx);
        }

        // Reads last frame's mode, which is a frame behind and invisible. The
        // call itself is free unless the answer changed.
        #[cfg(target_os = "windows")]
        taskbar::show(_frame, matches!(self.mode, Mode::Editing(_)));

        while let Ok(request) = self.requests.try_recv() {
            match request {
                Request::Quit => {
                    self.quitting = true;
                    ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                }
                // A second press while an overlay is up is the user asking
                // again for what is already on screen.
                Request::Region { delayed } => {
                    if matches!(self.mode, Mode::Idle) {
                        self.begin_region(ctx, delayed);
                    }
                }
                Request::Fullscreen { delayed } => {
                    if matches!(self.mode, Mode::Idle) {
                        self.capture_fullscreen(ctx, delayed);
                    }
                }
            }
        }

        match &mut self.mode {
            Mode::Selecting(selection) => {
                if let Some(outcome) = selection.update(ctx) {
                    self.end_selection(ctx, outcome);
                }
            }
            Mode::Editing(editor) => {
                editor.ui(ctx);
                // The window's close button asks the whole app to exit, because
                // this is the only viewport there is. ScreenX lives in the menu
                // bar, so closing the editor has to mean closing the editor.
                let closing = ctx.input(|i| i.viewport().close_requested());
                // A quit is the one close the editor must not swallow.
                if closing && self.quitting {
                    return;
                }
                if editor.done || closing {
                    if closing {
                        ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
                    }
                    // Read the position before the window is shrunk into its
                    // parking space, or the corner is all that gets remembered.
                    self.editor_pos = ctx.input(|i| i.viewport().outer_rect).map(|r| r.min);
                    self.go_idle(ctx);
                }
            }
            Mode::Idle => {}
        }
    }
}

/// The taskbar button, which should exist only while there is a window worth
/// clicking on.
///
/// egui cannot do this: `taskbar` is a `ViewportBuilder` field with no matching
/// `ViewportCommand`, and winit's version removes the button through
/// `ITaskbarList::DeleteTab` for the life of the process. The extended style is
/// the one route that can be turned back on.
#[cfg(target_os = "windows")]
mod taskbar {
    use std::cell::Cell;
    use std::ffi::c_void;

    use raw_window_handle::{HasWindowHandle, RawWindowHandle};

    #[repr(C)]
    struct Guid {
        data1: u32,
        data2: u16,
        data3: u16,
        data4: [u8; 8],
    }

    const CLSID_TASKBAR_LIST: Guid = Guid {
        data1: 0x56fd_f344,
        data2: 0xfd6d,
        data3: 0x11d0,
        data4: [0x95, 0x8a, 0x00, 0x60, 0x97, 0xc9, 0xa0, 0x90],
    };
    const IID_TASKBAR_LIST: Guid = Guid {
        data1: 0x56fd_f342,
        data2: 0xfd6d,
        data3: 0x11d0,
        data4: [0x95, 0x8a, 0x00, 0x60, 0x97, 0xc9, 0xa0, 0x90],
    };
    const CLSCTX_INPROC_SERVER: u32 = 1;
    const COINIT_APARTMENTTHREADED: u32 = 2;

    /// Only the first six entries are ever called, but the layout has to match
    /// the real vtable or the offsets are wrong.
    #[repr(C)]
    struct Vtbl {
        query_interface: unsafe extern "system" fn(*mut List, *const Guid, *mut *mut c_void) -> i32,
        add_ref: unsafe extern "system" fn(*mut List) -> u32,
        release: unsafe extern "system" fn(*mut List) -> u32,
        hr_init: unsafe extern "system" fn(*mut List) -> i32,
        add_tab: unsafe extern "system" fn(*mut List, isize) -> i32,
        delete_tab: unsafe extern "system" fn(*mut List, isize) -> i32,
        activate_tab: unsafe extern "system" fn(*mut List, isize) -> i32,
        set_active_alt: unsafe extern "system" fn(*mut List, isize) -> i32,
    }

    #[repr(C)]
    struct List {
        vtbl: *const Vtbl,
    }

    #[link(name = "ole32")]
    extern "system" {
        fn CoInitializeEx(reserved: *mut c_void, model: u32) -> i32;
        fn CoCreateInstance(
            clsid: *const Guid,
            outer: *mut c_void,
            context: u32,
            iid: *const Guid,
            out: *mut *mut c_void,
        ) -> i32;
    }

    thread_local! {
        /// Built once and kept. `HrInit` is the expensive part and it only
        /// needs doing when the shell connection is first made.
        static LIST: Cell<*mut List> = const { Cell::new(std::ptr::null_mut()) };
        static WANTED: Cell<Option<bool>> = const { Cell::new(None) };
    }

    fn list() -> *mut List {
        LIST.with(|cached| {
            if !cached.get().is_null() {
                return cached.get();
            }
            let mut raw: *mut c_void = std::ptr::null_mut();
            unsafe {
                // Already initialised by winit on this thread; a second call
                // returns S_FALSE and is harmless.
                CoInitializeEx(std::ptr::null_mut(), COINIT_APARTMENTTHREADED);
                if CoCreateInstance(
                    &CLSID_TASKBAR_LIST,
                    std::ptr::null_mut(),
                    CLSCTX_INPROC_SERVER,
                    &IID_TASKBAR_LIST,
                    &mut raw,
                ) < 0
                {
                    return std::ptr::null_mut();
                }
                let taskbar = raw.cast::<List>();
                if ((*(*taskbar).vtbl).hr_init)(taskbar) < 0 {
                    ((*(*taskbar).vtbl).release)(taskbar);
                    return std::ptr::null_mut();
                }
                cached.set(taskbar);
                taskbar
            }
        })
    }

    /// Add or remove the taskbar button.
    ///
    /// Not done through `WS_EX_APPWINDOW`: winit recomputes the whole extended
    /// style from its own flags and writes it back whenever decorations, the
    /// window level, visibility or mouse passthrough change, which erases any
    /// bit it does not know about. Opening the editor changes four of those, so
    /// a style-based version is overwritten every time. This is the same route
    /// winit itself uses, and it is independent of the style.
    pub fn show(frame: &eframe::Frame, wanted: bool) {
        // Two COM calls per mode change, none per frame.
        if WANTED.with(|w| w.get()) == Some(wanted) {
            return;
        }
        let Ok(handle) = frame.window_handle() else {
            return;
        };
        let RawWindowHandle::Win32(win32) = handle.as_raw() else {
            return;
        };
        let taskbar = list();
        if taskbar.is_null() {
            return;
        }
        let hwnd = win32.hwnd.get();
        unsafe {
            let vtbl = &*(*taskbar).vtbl;
            if wanted {
                (vtbl.add_tab)(taskbar, hwnd);
            } else {
                (vtbl.delete_tab)(taskbar, hwnd);
            }
        }
        WANTED.with(|w| w.set(Some(wanted)));
    }
}

/// Where to put the editor: where it was last left, if that is still a place on
/// somebody's screen, and centred on the primary display if not.
///
/// The old placement was the top left corner of the primary monitor every time,
/// which on a wide desktop is a long way from wherever the work is.
fn editor_position(
    last: Option<egui::Pos2>,
    bounds: &[capture::Rect],
    size: egui::Vec2,
) -> egui::Pos2 {
    // A monitor can be unplugged between one capture and the next, so a
    // remembered position is only reused while it still lands on a screen.
    if let Some(last) = last {
        if bounds.iter().any(|b| b.contains(last.x as i32, last.y as i32)) {
            return last;
        }
    }
    bounds
        .first()
        .map(|b| {
            egui::pos2(
                b.x as f32 + (b.width as f32 - size.x).max(0.0) / 2.0,
                b.y as f32 + (b.height as f32 - size.y).max(0.0) / 2.0,
            )
        })
        .unwrap_or(egui::pos2(80.0, 80.0))
}

/// Where the window waits between captures.
///
/// Windows never paints a window it considers hidden, and a window that is not
/// painted gets no `RedrawRequested` — which is the only thing that runs
/// `update`. Hiding the window therefore stopped the app reading its own
/// request channel: the hotkey still fired, the system still delivered it, and
/// nothing was left listening. It worked exactly once per launch, until the
/// first capture finished and put the window away.
///
/// So on Windows the window stays nominally visible and is parked instead: one
/// point across, in the far corner of the primary display, underneath every
/// other window and transparent to the mouse. Nothing can see it or click it,
/// and the event loop keeps running.
#[cfg(target_os = "windows")]
fn park(ctx: &egui::Context) {
    let corner = capture::monitor_bounds()
        .first()
        .map(|b| egui::pos2((b.right() - 1) as f32, (b.bottom() - 1) as f32))
        .unwrap_or(egui::pos2(0.0, 0.0));
    ctx.send_viewport_cmd(egui::ViewportCommand::OuterPosition(corner));
    // Deliberately `Normal` and not `AlwaysOnBottom`. Leaving the level alone
    // is what keeps the editor's activation from being undone: winit applies a
    // level change as an *asynchronous* `SetWindowPos` carrying
    // `SWP_NOACTIVATE`, which lands after the frame that asked for focus and
    // pushes the window straight back down. A click-through window one point
    // across needs no help staying out of the way.
    ctx.send_viewport_cmd(egui::ViewportCommand::WindowLevel(egui::WindowLevel::Normal));
    ctx.send_viewport_cmd(egui::ViewportCommand::MousePassthrough(true));
    ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
}

/// Everywhere else a hidden window still gets its draw callback, so it can
/// simply go away.
#[cfg(not(target_os = "windows"))]
fn park(ctx: &egui::Context) {
    ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));
}

/// Put the overlay above the menu bar and make the app active.
///
/// Both were real bugs in the webview build. An always-on-top window still sits
/// below the menu bar, which is a higher level of its own, and an accessory app
/// that merely orders a window front never becomes *key* — so the overlay
/// arrived inert, with an arrow cursor and its first click spent activating.
/// `activate()` is cooperative on macOS 14 and later and gets deferred, so this
/// uses the deprecated call that does not ask.
#[cfg(target_os = "macos")]
fn raise_and_activate(_ctx: &egui::Context) {
    use objc2_app_kit::{NSApplication, NSWindow};
    let Some(mtm) = objc2::MainThreadMarker::new() else {
        return;
    };
    let app = NSApplication::sharedApplication(mtm);
    #[allow(deprecated)]
    app.activateIgnoringOtherApps(true);
    for window in app.windows().iter() {
        let window: &NSWindow = &window;
        // NSScreenSaverWindowLevel. The menu bar is 24 and the dock is 20.
        window.setLevel(1000);
        window.makeKeyAndOrderFront(None);
    }
}

/// The editor is an ordinary window, so it must come back below the menu bar.
#[cfg(target_os = "macos")]
fn raise_normal(_ctx: &egui::Context) {
    use objc2_app_kit::{NSApplication, NSWindow};
    let Some(mtm) = objc2::MainThreadMarker::new() else {
        return;
    };
    let app = NSApplication::sharedApplication(mtm);
    #[allow(deprecated)]
    app.activateIgnoringOtherApps(true);
    for window in app.windows().iter() {
        let window: &NSWindow = &window;
        window.setLevel(0);
        window.makeKeyAndOrderFront(None);
    }
}

/// Undo the parking. Both of these have to clear the click-through, or the
/// overlay comes back unable to receive the drag it exists to collect.
#[cfg(target_os = "windows")]
fn raise_and_activate(ctx: &egui::Context) {
    ctx.send_viewport_cmd(egui::ViewportCommand::MousePassthrough(false));
    ctx.send_viewport_cmd(egui::ViewportCommand::WindowLevel(
        egui::WindowLevel::AlwaysOnTop,
    ));
}

#[cfg(target_os = "windows")]
fn raise_normal(ctx: &egui::Context) {
    ctx.send_viewport_cmd(egui::ViewportCommand::MousePassthrough(false));
    ctx.send_viewport_cmd(egui::ViewportCommand::WindowLevel(egui::WindowLevel::Normal));
}

#[cfg(not(any(target_os = "macos", target_os = "windows")))]
fn raise_and_activate(_ctx: &egui::Context) {}

#[cfg(not(any(target_os = "macos", target_os = "windows")))]
fn raise_normal(_ctx: &egui::Context) {}

/// Parse the configured accelerator, e.g. `Control+Shift+Q`.
///
/// Modifiers are taken literally, as they were in the old build: `Control`
/// means Control everywhere. A "CommandOrControl" that resolves to Command on
/// macOS silently registered a different shortcut than the user typed.
fn parse_hotkey(accelerator: &str) -> Option<HotKey> {
    let mut modifiers = Modifiers::empty();
    let mut code = None;
    for part in accelerator.split('+') {
        match part.trim().to_ascii_lowercase().as_str() {
            "control" | "ctrl" => modifiers |= Modifiers::CONTROL,
            "shift" => modifiers |= Modifiers::SHIFT,
            "alt" | "option" => modifiers |= Modifiers::ALT,
            "command" | "cmd" | "super" | "meta" => modifiers |= Modifiers::META,
            key => {
                code = match key {
                    "a" => Some(Code::KeyA), "b" => Some(Code::KeyB), "c" => Some(Code::KeyC),
                    "d" => Some(Code::KeyD), "e" => Some(Code::KeyE), "f" => Some(Code::KeyF),
                    "g" => Some(Code::KeyG), "h" => Some(Code::KeyH), "i" => Some(Code::KeyI),
                    "j" => Some(Code::KeyJ), "k" => Some(Code::KeyK), "l" => Some(Code::KeyL),
                    "m" => Some(Code::KeyM), "n" => Some(Code::KeyN), "o" => Some(Code::KeyO),
                    "p" => Some(Code::KeyP), "q" => Some(Code::KeyQ), "r" => Some(Code::KeyR),
                    "s" => Some(Code::KeyS), "t" => Some(Code::KeyT), "u" => Some(Code::KeyU),
                    "v" => Some(Code::KeyV), "w" => Some(Code::KeyW), "x" => Some(Code::KeyX),
                    "y" => Some(Code::KeyY), "z" => Some(Code::KeyZ),
                    _ => None,
                };
            }
        }
    }
    // A modifier-less global hotkey takes that key from every application.
    if modifiers.is_empty() {
        return None;
    }
    Some(HotKey::new(Some(modifiers), code?))
}

/// Stand the overlay up on a synthetic capture and hold it, so its memory can
/// be measured from outside with `ps`.
///
/// The cost of the overlay is its texture and its framebuffer, neither of which
/// cares where the pixels came from — so this measures the real thing without
/// needing Screen Recording, which a shell cannot be granted.
fn memcheck(size: (u32, u32), seconds: u64) -> eframe::Result<()> {
    let (width, height) = size;
    let image = image::RgbaImage::from_fn(width, height, |x, y| {
        image::Rgba([(x % 256) as u8, (y % 256) as u8, ((x + y) % 256) as u8, 255])
    });
    let shot = capture::MonitorShot {
        id: 0,
        bounds: capture::Rect { x: 0, y: 0, width: width / 2, height: height / 2 },
        scale: 2.0,
        name: "memcheck".into(),
        image,
    };
    eprintln!("[memcheck] {width}x{height} for {seconds}s, pid {}", std::process::id());

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size(egui::vec2(width as f32, height as f32))
            .with_decorations(false)
            .with_resizable(false),
        ..Default::default()
    };
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(seconds);
    if std::env::args().any(|a| a == "--editor") {
        // The editor holds the image, its texture and the history, which is the
        // combination worth measuring — the overlay only ever holds one frame.
        let image = shot.image.clone();
        let spare_image = shot.image.clone();
        return eframe::run_native(
            "ScreenX memcheck",
            eframe::NativeOptions::default(),
            Box::new(move |_cc| {
                let mut editor = Some(editor::Editor::new(image, "memcheck".into(), 2.0));
                // Halfway through, put the editor away exactly as closing it
                // does. Whether the memory comes back is the thing worth
                // measuring: the editor holding on after it closed was a bug.
                let close_at = std::time::Instant::now()
                    + std::time::Duration::from_secs(seconds / 3);
                let reopen_at = std::time::Instant::now()
                    + std::time::Duration::from_secs(seconds * 2 / 3);
                let mut reopened = false;
                let mut spare = Some(spare_image);
                Ok(Box::new(Held {
                    draw: Box::new(move |ctx| {
                        let now = std::time::Instant::now();
                        if now >= close_at && editor.is_some() {
                            editor = None;
                            eprintln!("[memcheck] editor closed");
                            ctx.forget_all_images();
                            ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));
                            ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(
                                egui::vec2(1.0, 1.0),
                            ));
                        }
                        // Reopen once, to tell a leak apart from memory that
                        // was freed and handed straight back out again.
                        if now >= reopen_at && editor.is_none() && !reopened {
                            reopened = true;
                            eprintln!("[memcheck] editor reopened");
                            editor = Some(editor::Editor::new(
                                spare.take().unwrap(),
                                "memcheck".into(),
                                2.0,
                            ));
                            ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
                        }
                        if let Some(editor) = &mut editor {
                            editor.ui(ctx);
                        }
                        ctx.request_repaint();
                        if now >= deadline {
                            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                        }
                    }),
                }))
            }),
        );
    }
    eframe::run_native(
        "ScreenX memcheck",
        options,
        Box::new(move |cc| {
            let started = std::time::Instant::now();
            let mut overlay = Overlay::new(&cc.egui_ctx, shot);
            eprintln!("[memcheck] texture ready in {:?}", started.elapsed());
            let mut first_frame = Some(std::time::Instant::now());
            Ok(Box::new(Held {
                draw: Box::new(move |ctx| {
                    if let Some(at) = first_frame.take() {
                        eprintln!("[memcheck] first frame at {:?}", at.elapsed());
                    }
                    overlay.update(ctx);
                    ctx.request_repaint();
                    if std::time::Instant::now() >= deadline {
                        ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                    }
                }),
            }))
        }),
    )
}

/// Runs one closure per frame. Only the measurement uses it.
struct Held {
    draw: Box<dyn FnMut(&egui::Context)>,
}

impl eframe::App for Held {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        (self.draw)(ctx);
    }
}

fn main() -> eframe::Result<()> {
    let args: Vec<String> = std::env::args().collect();
    if let Some(index) = args.iter().position(|a| a == "--memcheck") {
        let parse = |i: usize, fallback: u32| {
            args.get(i).and_then(|v| v.parse().ok()).unwrap_or(fallback)
        };
        let width = parse(index + 1, 3584);
        let height = parse(index + 2, 2240);
        let seconds = parse(index + 3, 8) as u64;
        return memcheck((width, height), seconds);
    }

    let (sender, receiver) = std::sync::mpsc::channel();

    // Held for the life of the process: dropping either unregisters it.
    let hotkeys = GlobalHotKeyManager::new().ok();
    let register = |accelerator: &str| -> Option<u32> {
        let manager = hotkeys.as_ref()?;
        let hotkey = parse_hotkey(accelerator)?;
        match manager.register(hotkey) {
            Ok(()) => Some(hotkey.id()),
            Err(err) => {
                eprintln!("[screenx] the system refused {accelerator}: {err}");
                None
            }
        }
    };
    let configured = settings::get().hotkeys;
    let region_hotkey = register(&configured.capture_region);
    let fullscreen_hotkey = register(&configured.capture_fullscreen);

    listen_for_events(sender.clone(), region_hotkey, fullscreen_hotkey);
    // Held for the life of the process: dropping it removes the icon.
    let _tray = tray::build();

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_decorations(false)
            .with_transparent(true)
            .with_resizable(false)
            // The taskbar button stays. There is no `ViewportCommand` for it —
            // `taskbar` can only be set when the window is built — and winit
            // implements it as `ITaskbarList::DeleteTab`, which removes the
            // button for the life of the process. That left the editor with no
            // way back once it was minimised or buried.
            // Built hidden. It becomes the overlay when a capture asks for it.
            .with_visible(false),
        ..Default::default()
    };

    eframe::run_native(
        "ScreenX",
        options,
        Box::new(move |cc| Ok(Box::new(App::new(cc, receiver)))),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn screens() -> Vec<capture::Rect> {
        vec![
            capture::Rect { x: 0, y: 0, width: 5120, height: 1440 },
            capture::Rect { x: 1680, y: 1440, width: 1920, height: 1080 },
        ]
    }

    #[test]
    fn the_editor_reopens_where_it_was_left() {
        let size = egui::vec2(800.0, 600.0);
        // Somewhere on the primary display.
        let moved = egui::pos2(3000.0, 400.0);
        assert_eq!(editor_position(Some(moved), &screens(), size), moved);
        // And on the second one, which is the case a "centre it" rule loses.
        let elsewhere = egui::pos2(2000.0, 1800.0);
        assert_eq!(editor_position(Some(elsewhere), &screens(), size), elsewhere);
    }

    #[test]
    fn a_position_on_a_monitor_that_is_gone_is_not_reused() {
        let size = egui::vec2(800.0, 600.0);
        let unplugged = egui::pos2(9000.0, 3000.0);
        let placed = editor_position(Some(unplugged), &screens(), size);
        assert_eq!(placed, egui::pos2((5120.0 - 800.0) / 2.0, (1440.0 - 600.0) / 2.0));
    }

    #[test]
    fn the_first_editor_is_centred_rather_than_cornered() {
        let placed = editor_position(None, &screens(), egui::vec2(800.0, 600.0));
        assert_eq!(placed, egui::pos2(2160.0, 420.0));
    }

    #[test]
    fn an_editor_larger_than_the_screen_still_starts_on_it() {
        // The window is clamped to the display elsewhere, so the placement only
        // has to avoid pushing it off the top left.
        let placed = editor_position(None, &screens(), egui::vec2(9000.0, 4000.0));
        assert_eq!(placed, egui::pos2(0.0, 0.0));
    }

    #[test]
    fn with_no_monitors_at_all_it_still_returns_somewhere() {
        assert_eq!(
            editor_position(None, &[], egui::vec2(800.0, 600.0)),
            egui::pos2(80.0, 80.0)
        );
    }
}

/// Forward hotkey and tray menu events onto the app's channel.
fn listen_for_events(sender: Sender<Request>, region: Option<u32>, fullscreen: Option<u32>) {
    let hotkeys = GlobalHotKeyEvent::receiver().clone();
    let menu = MenuEvent::receiver().clone();
    std::thread::spawn(move || loop {
        if let Ok(event) = hotkeys.try_recv() {
            if event.state == global_hotkey::HotKeyState::Pressed {
                let id = Some(event.id);
                if id == region {
                    let _ = sender.send(Request::Region { delayed: false });
                } else if id == fullscreen {
                    let _ = sender.send(Request::Fullscreen { delayed: false });
                }
            }
        }
        if let Ok(event) = menu.try_recv() {
            match event.id.as_ref() {
                "folder" => tray::open_path(&tray::screenshot_folder()),
                "settings" => tray::open_settings(),
                "quit" => {
                    let _ = sender.send(Request::Quit);
                }
                _ => {}
            }
        }
        // Windows users expect a tray icon to open something on double click,
        // and the folder is the only thing worth opening. macOS shows the menu
        // on any click, so this never fires there.
        if let Ok(TrayIconEvent::DoubleClick { .. }) = TrayIconEvent::receiver().try_recv() {
            tray::open_path(&tray::screenshot_folder());
        }
        std::thread::sleep(std::time::Duration::from_millis(30));
    });
}
