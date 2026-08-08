// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! ScreenX: local screen capture, drawn natively.
//!
//! One process, one event loop, one window. The window is hidden until a
//! capture asks for it, and becomes the selection overlay in place — there is
//! no per-capture window creation, which is what made the webview build slow,
//! and no browser process tree, which is what made it heavy.
//!
//! A winit event loop can only be run once per process on macOS, so the design
//! is not optional: the app is persistent and the overlay is a state it enters.

mod overlay;

use std::sync::mpsc::{Receiver, Sender};

use eframe::egui;
use global_hotkey::{
    hotkey::{Code, HotKey, Modifiers},
    GlobalHotKeyEvent, GlobalHotKeyManager,
};
use screenx_core::{capture, settings};
use tray_icon::{
    menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem},
    TrayIconBuilder,
};

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
}

struct App {
    mode: Mode,
    requests: Receiver<Request>,
    /// Shown in the overlay's place after a save, briefly.
    status: Option<(String, std::time::Instant)>,
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
    fn shoot(&mut self, delayed: bool) -> Option<capture::MonitorShot> {
        Self::wait(delayed);
        match capture::capture_monitors() {
            // ponytail: primary monitor only. A second monitor is a second
            // viewport rather than a different design; add it when someone has
            // two to test it on.
            Ok(shots) => match shots.into_iter().next() {
                Some(shot) => Some(shot),
                None => {
                    self.report("no monitors found".into());
                    None
                }
            },
            Err(err) => {
                self.report(err);
                None
            }
        }
    }

    /// Capture a whole monitor and save it, with no overlay in between.
    fn capture_fullscreen(&mut self, delayed: bool) {
        let Some(shot) = self.shoot(delayed) else { return };
        match capture::save_image(&shot.image, &shot.name) {
            Ok(path) => self.report(format!("saved {}", path.display())),
            Err(err) => self.report(err),
        }
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
        ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
        ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
        raise_and_activate();
    }

    /// Leave the overlay and give the capture back.
    fn end_selection(&mut self, ctx: &egui::Context, outcome: Outcome) {
        let previous = std::mem::replace(&mut self.mode, Mode::Idle);
        ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));

        let Mode::Selecting(overlay) = previous else {
            return;
        };
        let Outcome::Selected(rect) = outcome else {
            return;
        };

        let Some(image) = capture::crop_to_rect(overlay.shot(), &rect) else {
            return self.report("that selection was too small to capture".into());
        };
        match capture::save_image(&image, &overlay.shot().name) {
            Ok(path) => self.report(format!(
                "saved {}",
                path.file_name().unwrap_or_default().to_string_lossy()
            )),
            Err(err) => self.report(err),
        }
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
        while let Ok(request) = self.requests.try_recv() {
            match request {
                Request::Quit => ctx.send_viewport_cmd(egui::ViewportCommand::Close),
                // A second press while an overlay is up is the user asking
                // again for what is already on screen.
                Request::Region { delayed } => {
                    if matches!(self.mode, Mode::Idle) {
                        self.begin_region(ctx, delayed);
                    }
                }
                Request::Fullscreen { delayed } => {
                    if matches!(self.mode, Mode::Idle) {
                        self.capture_fullscreen(delayed);
                    }
                }
            }
        }

        if let Mode::Selecting(selection) = &mut self.mode {
            if let Some(outcome) = selection.update(ctx) {
                self.end_selection(ctx, outcome);
            }
        }
    }
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
fn raise_and_activate() {
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

#[cfg(not(target_os = "macos"))]
fn raise_and_activate() {}

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
    let region_hotkey = hotkeys.as_ref().and_then(|manager| {
        let accelerator = settings::get().hotkeys.capture_region;
        let hotkey = parse_hotkey(&accelerator)?;
        match manager.register(hotkey) {
            Ok(()) => Some(hotkey.id()),
            Err(err) => {
                eprintln!("[screenx] the system refused {accelerator}: {err}");
                None
            }
        }
    });

    listen_for_hotkeys(sender.clone(), region_hotkey);
    let _tray = build_tray(sender);

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_decorations(false)
            .with_transparent(true)
            .with_resizable(false)
            .with_taskbar(false)
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

/// Forward hotkey and tray menu events onto the app's channel.
fn listen_for_hotkeys(sender: Sender<Request>, region: Option<u32>) {
    let hotkeys = GlobalHotKeyEvent::receiver().clone();
    let menu = MenuEvent::receiver().clone();
    std::thread::spawn(move || loop {
        if let Ok(event) = hotkeys.try_recv() {
            if Some(event.id) == region && event.state == global_hotkey::HotKeyState::Pressed {
                let _ = sender.send(Request::Region { delayed: false });
            }
        }
        if let Ok(event) = menu.try_recv() {
            let request = match event.id.as_ref() {
                "region" => Some(Request::Region { delayed: false }),
                "fullscreen" => Some(Request::Fullscreen { delayed: false }),
                "region-delayed" => Some(Request::Region { delayed: true }),
                "fullscreen-delayed" => Some(Request::Fullscreen { delayed: true }),
                "quit" => Some(Request::Quit),
                _ => None,
            };
            if let Some(request) = request {
                let _ = sender.send(request);
            }
        }
        std::thread::sleep(std::time::Duration::from_millis(30));
    });
}

fn build_tray(_sender: Sender<Request>) -> Option<tray_icon::TrayIcon> {
    let menu = Menu::new();
    let region = MenuItem::with_id("region", "Capture Region", true, None);
    let fullscreen = MenuItem::with_id("fullscreen", "Capture Entire Screen", true, None);
    // The delay is what makes photographing a menu possible, so it stays
    // reachable — just not charged to every capture.
    let region_delayed =
        MenuItem::with_id("region-delayed", "Capture Region After Delay", true, None);
    let fullscreen_delayed = MenuItem::with_id(
        "fullscreen-delayed",
        "Capture Entire Screen After Delay",
        true,
        None,
    );
    let quit = MenuItem::with_id("quit", "Quit ScreenX", true, None);
    menu.append_items(&[
        &region,
        &fullscreen,
        &region_delayed,
        &fullscreen_delayed,
        &PredefinedMenuItem::separator(),
        &quit,
    ])
    .ok()?;

    TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_tooltip("ScreenX")
        .build()
        .ok()
}
