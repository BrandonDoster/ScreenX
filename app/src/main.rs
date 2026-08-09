// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

// A tray application has no console to write to, and Windows opens one for a
// console subsystem binary — a black window that sits behind the tray icon for
// the life of the process. Debug builds keep it, because that is where the
// eprintln! diagnostics go.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! `screenx-capture`: one screenshot, then exit.
//!
//! This is the worker half of the split. It is spawned by the listener with the
//! kind of capture to take, reads the screen **before it opens any window**, and
//! lives only as long as that one screenshot's overlay and editor. When the
//! editor closes the process ends, which is what returns the renderer's memory
//! to the system — a thing a single long-lived process cannot do, because
//! nothing frees a GL context short of exiting.
//!
//! Reading the screen first is not an optimisation, it is a correctness rule:
//! anything this process draws would otherwise be in the photograph.
//!
//! The listener is `src/listener.rs` and holds no renderer at all.

mod edits;
mod editor;
mod overlay;
mod render;

use eframe::egui;
use screenx_core::{capture, settings};

use editor::Editor;
use overlay::{Outcome, Overlay};

/// What the window is currently for.
enum Mode {
    /// The work is finished and the process is on its way out.
    Idle,
    Selecting(Overlay),
    Editing(Box<Editor>),
}

struct App {
    mode: Mode,
    /// Shown in the overlay's place after a save, briefly.
    status: Option<(String, std::time::Instant)>,
    /// Where the editor was last dragged to. Read from the settings file rather
    /// than from memory, because the previous editor was a different process.
    editor_pos: Option<egui::Pos2>,
    /// Read once at start-up. Held rather than re-read so the tests can choose
    /// it without depending on whatever is in the user's settings file.
    after_capture: String,
}

impl App {
    /// Takes the capture that was already read before the window existed.
    fn new(cc: &eframe::CreationContext<'_>, start: Start) -> Self {
        // Nothing else wakes this loop, so it has to wake itself: the editor's
        // status line expires on a timer and the first frame has viewport
        // commands waiting on it.
        let ctx = cc.egui_ctx.clone();
        std::thread::spawn(move || loop {
            std::thread::sleep(std::time::Duration::from_millis(60));
            ctx.request_repaint();
        });

        let configured = settings::get();
        let mut app = Self::waiting(
            configured.editor_position.map(|[x, y]| egui::pos2(x, y)),
            configured.after_capture,
        );
        match start {
            Start::Region(shot) => app.begin_region(&cc.egui_ctx, shot),
            Start::Fullscreen(shot) => {
                let (image, name, density) = (shot.image, shot.name, shot.scale);
                app.deliver(&cc.egui_ctx, image, name, density);
            }
        }
        app
    }

    fn waiting(editor_pos: Option<egui::Pos2>, after_capture: String) -> Self {
        Self {
            mode: Mode::Idle,
            status: None,
            editor_pos,
            after_capture,
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

    /// Enter the overlay on a capture that has already been read.
    fn begin_region(&mut self, ctx: &egui::Context, shot: capture::MonitorShot) {
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
    ///
    /// Every path out of here either hands the work to `deliver` or ends the
    /// process, and it must not do both: `go_idle` used to park the window and
    /// now closes it, so calling it before delivering queued a close that fired
    /// at the end of the same frame and shut the editor as it opened.
    fn end_selection(&mut self, ctx: &egui::Context, outcome: Outcome) {
        let previous = std::mem::replace(&mut self.mode, Mode::Idle);

        let (Mode::Selecting(overlay), Outcome::Selected(rect)) = (previous, outcome) else {
            // Cancelled, so there is nothing left for this process to do.
            return self.go_idle(ctx);
        };

        let Some(image) = capture::crop_to_rect(overlay.shot(), &rect) else {
            self.report("that selection was too small to capture".into());
            return self.go_idle(ctx);
        };
        let density = overlay.shot().scale;
        self.deliver(ctx, image, overlay.shot().name.clone(), density);
    }

    /// What happens to a finished capture, per the settings file.
    ///
    /// Only the editor keeps this process alive. The other two write the
    /// screenshot somewhere and then there is nothing left to stay open for.
    fn deliver(&mut self, ctx: &egui::Context, image: image::RgbaImage, title: String, density: f32) {
        match self.after_capture.as_str() {
            "editor" => return self.open_editor(ctx, image, title, density),
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
        self.go_idle(ctx);
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


    /// The screenshot is finished, so the process is too.
    ///
    /// Hiding and shrinking the window used to be how the memory came back.
    /// Exiting does it properly: the renderer's warm-up, which measurement
    /// showed never returns while a GL context is alive, goes with the process.
    fn go_idle(&mut self, ctx: &egui::Context) {
        self.mode = Mode::Idle;
        ctx.send_viewport_cmd(egui::ViewportCommand::Close);
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
        // The overlay is a bare full-screen window and has no business in the
        // taskbar; the editor is an ordinary window and needs to be reachable
        // from it. Reads last frame's mode, and costs nothing unless it changed.
        #[cfg(target_os = "windows")]
        taskbar::show(_frame, matches!(self.mode, Mode::Editing(_)));

        match &mut self.mode {
            Mode::Selecting(selection) => {
                if let Some(outcome) = selection.update(ctx) {
                    self.end_selection(ctx, outcome);
                }
            }
            Mode::Editing(editor) => {
                editor.ui(ctx);
                // Closing the editor is the end of the screenshot, and the end
                // of the screenshot is the end of this process — so the close
                // is allowed through rather than cancelled. The listener is
                // what survives, and it is a different program.
                let closing = ctx.input(|i| i.viewport().close_requested());
                if editor.done || closing {
                    // Read the position before the window goes anywhere. It is
                    // written to the settings file because the next editor will
                    // not share this process's memory.
                    if let Some(at) = ctx.input(|i| i.viewport().outer_rect).map(|r| r.min) {
                        settings::remember_editor_position([at.x, at.y]);
                    }
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

/// The overlay has to cover everything, including whatever was in front when
/// the shortcut was pressed.
#[cfg(target_os = "windows")]
fn raise_and_activate(ctx: &egui::Context) {
    ctx.send_viewport_cmd(egui::ViewportCommand::WindowLevel(
        egui::WindowLevel::AlwaysOnTop,
    ));
}

/// The editor is an ordinary window and must not float over the user's work.
#[cfg(target_os = "windows")]
fn raise_normal(ctx: &egui::Context) {
    ctx.send_viewport_cmd(egui::ViewportCommand::WindowLevel(egui::WindowLevel::Normal));
}

#[cfg(not(any(target_os = "macos", target_os = "windows")))]
fn raise_and_activate(_ctx: &egui::Context) {}

#[cfg(not(any(target_os = "macos", target_os = "windows")))]
fn raise_normal(_ctx: &egui::Context) {}

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

    let fullscreen = args.iter().any(|a| a == "--fullscreen");
    let delayed = args.iter().any(|a| a == "--delayed");

    // Before the window. Everything below this line can be photographed, and
    // the point of the delay is to let a menu finish opening, so it is paid
    // here too.
    App::wait(delayed);
    let shot = match capture::capture_primary() {
        Ok(shot) => shot,
        Err(err) => {
            eprintln!("[screenx] {err}");
            return Ok(());
        }
    };

    // A whole-screen capture that is not going to be edited never needs a
    // window, a renderer or a GL context — write it and go.
    if fullscreen && settings::get().after_capture != "editor" {
        match settings::get().after_capture.as_str() {
            "clipboard" => {
                if let Err(err) = capture::copy_to_clipboard(&shot.image) {
                    eprintln!("[screenx] {err}");
                }
            }
            _ => match capture::save_image(&shot.image, &shot.name) {
                Ok(path) => eprintln!("[screenx] saved {}", path.display()),
                Err(err) => eprintln!("[screenx] {err}"),
            },
        }
        return Ok(());
    }

    let start = if fullscreen {
        Start::Fullscreen(shot)
    } else {
        Start::Region(shot)
    };

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_decorations(false)
            .with_transparent(true)
            .with_resizable(false)
            // Built hidden and shown by the first frame, so the window is never
            // seen at the default size before it is positioned. On Windows the
            // flag does not hold, which is why the first frame sets the
            // geometry rather than trusting the builder.
            .with_visible(false),
        ..Default::default()
    };

    eframe::run_native(
        "ScreenX",
        options,
        Box::new(move |cc| Ok(Box::new(App::new(cc, start)))),
    )
}

/// What this process was spawned to do, with the screen already read.
enum Start {
    Region(capture::MonitorShot),
    Fullscreen(capture::MonitorShot),
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

    fn shot(width: u32, height: u32) -> capture::MonitorShot {
        capture::MonitorShot {
            id: 0,
            bounds: capture::Rect { x: 0, y: 0, width, height },
            scale: 1.0,
            name: "test".into(),
            image: image::RgbaImage::from_pixel(width, height, image::Rgba([9, 9, 9, 255])),
        }
    }

    /// Run one frame and report what the app asked the window to do.
    fn commands_from(app: &mut App, run: impl FnOnce(&mut App, &egui::Context)) -> Vec<egui::ViewportCommand> {
        let ctx = egui::Context::default();
        // `run` takes an `FnMut`, so the one-shot closure needs somewhere to be
        // taken from.
        let mut once = Some(run);
        let output = ctx.run(Default::default(), |ctx| {
            if let Some(run) = once.take() {
                run(app, ctx);
            }
        });
        output.viewport_output[&egui::ViewportId::ROOT]
            .commands
            .clone()
    }

    #[test]
    fn choosing_a_region_opens_the_editor_rather_than_closing() {
        // The regression: `go_idle` used to park the window and now closes it,
        // so calling it on the way to the editor queued a close that fired at
        // the end of the same frame. The editor appeared and vanished. The mode
        // is `Editing` either way, so only the queued command shows it.
        let mut app = App::waiting(None, "editor".into());
        let commands = commands_from(&mut app, |app, ctx| {
            app.mode = Mode::Selecting(Overlay::new(ctx, shot(200, 200)));
            app.end_selection(ctx, Outcome::Selected(capture::Rect { x: 0, y: 0, width: 80, height: 60 }));
        });
        assert!(matches!(app.mode, Mode::Editing(_)), "the editor should be open");
        assert!(
            !commands.contains(&egui::ViewportCommand::Close),
            "the editor was opened and closed in the same frame: {commands:?}"
        );
    }

    #[test]
    fn cancelling_the_overlay_ends_the_process() {
        let mut app = App::waiting(None, "editor".into());
        let commands = commands_from(&mut app, |app, ctx| {
            app.mode = Mode::Selecting(Overlay::new(ctx, shot(200, 200)));
            app.end_selection(ctx, Outcome::Cancelled);
        });
        assert!(commands.contains(&egui::ViewportCommand::Close), "nothing left to do, so it must close");
    }

    #[test]
    fn a_selection_that_is_only_saved_ends_the_process() {
        // No editor means nothing to stay open for. This path had no close at
        // all when the editor stopped being the only outcome.
        let mut app = App::waiting(None, "clipboard".into());
        let commands = commands_from(&mut app, |app, ctx| {
            app.mode = Mode::Selecting(Overlay::new(ctx, shot(200, 200)));
            app.end_selection(ctx, Outcome::Selected(capture::Rect { x: 0, y: 0, width: 80, height: 60 }));
        });
        assert!(commands.contains(&egui::ViewportCommand::Close), "a copy is finished work: {commands:?}");
    }

    #[test]
    fn a_selection_too_small_to_crop_ends_the_process() {
        let mut app = App::waiting(None, "editor".into());
        let commands = commands_from(&mut app, |app, ctx| {
            app.mode = Mode::Selecting(Overlay::new(ctx, shot(200, 200)));
            // Entirely off the right edge, so nothing can be cropped from it.
            app.end_selection(ctx, Outcome::Selected(capture::Rect { x: 400, y: 0, width: 10, height: 10 }));
        });
        assert!(commands.contains(&egui::ViewportCommand::Close));
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

