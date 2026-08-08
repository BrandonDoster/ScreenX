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
    Region,
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

    /// Read the screen and enter the overlay.
    fn begin_region(&mut self, ctx: &egui::Context) {
        let shots = match capture::capture_monitors() {
            Ok(shots) => shots,
            Err(err) => return self.report(err),
        };
        // ponytail: primary monitor only for now. One overlay per monitor is a
        // second viewport, not a different design; add it when the single
        // monitor path is proven.
        let Some(shot) = shots.into_iter().next() else {
            return self.report("no monitors found".into());
        };

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
                Request::Region => {
                    if matches!(self.mode, Mode::Idle) {
                        self.begin_region(ctx);
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

fn main() -> eframe::Result<()> {
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
                let _ = sender.send(Request::Region);
            }
        }
        if let Ok(event) = menu.try_recv() {
            let request = match event.id.as_ref() {
                "region" => Some(Request::Region),
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
    let quit = MenuItem::with_id("quit", "Quit ScreenX", true, None);
    menu.append_items(&[&region, &PredefinedMenuItem::separator(), &quit])
        .ok()?;

    TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_tooltip("ScreenX")
        .build()
        .ok()
}
