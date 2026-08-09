// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

// A tray application has no console to write to, and Windows opens one for a
// console subsystem binary — a black window that sits behind the tray icon for
// the life of the process. Debug builds keep it, because that is where the
// eprintln! diagnostics go.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

//! `screenx`: the part that is always running.
//!
//! It owns the tray icon, the two global shortcuts and the settings file, and
//! it spawns `screenx-capture` to take a screenshot. It draws nothing, opens no
//! window, and never links a renderer — which is the whole point. A GL context
//! is not freed by anything short of process exit, so the only way to give that
//! memory back between screenshots is for the process holding it to end.
//!
//! An event loop is still needed even with no window. Both `tray-icon` and
//! `global-hotkey` create hidden windows on Windows and post to the thread's
//! message queue, and on macOS the tray needs a running `NSApplication`. winit
//! provides both without a renderer attached.

mod tray;

use std::process::{Child, Command};

use global_hotkey::{
    hotkey::{Code, HotKey, Modifiers},
    GlobalHotKeyEvent, GlobalHotKeyManager,
};
use screenx_core::settings;
use tray_icon::{menu::MenuEvent, TrayIconEvent};
use winit::application::ApplicationHandler;
use winit::event::WindowEvent;
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::window::WindowId;

/// How often the tray and shortcut channels are drained.
///
/// Both crates post to a channel from a window procedure rather than through
/// winit, so there is nothing to wake the loop for them. 30 ms is below what a
/// keypress can be noticed at and costs nothing measurable while idle.
const POLL: std::time::Duration = std::time::Duration::from_millis(30);

struct Listener {
    region: Option<u32>,
    fullscreen: Option<u32>,
    /// The screenshot in progress, if there is one.
    ///
    /// Held so a second shortcut press cannot stack a second overlay on top of
    /// the first. The single-process build got this from its `Mode`; here the
    /// state lives in another process, so the handle is the state.
    working: Option<Child>,
}

impl Listener {
    /// Start a screenshot, unless one is already on screen.
    fn spawn(&mut self, kind: &str, delayed: bool) {
        if let Some(child) = &mut self.working {
            match child.try_wait() {
                Ok(Some(_)) => self.working = None,
                // Still going: this press is the user asking again for what is
                // already in front of them.
                Ok(None) => return,
                Err(_) => self.working = None,
            }
        }

        let Some(exe) = worker_path() else {
            eprintln!("[screenx] cannot find screenx-capture next to this program");
            return;
        };
        let mut command = Command::new(exe);
        command.arg(kind);
        if delayed {
            command.arg("--delayed");
        }
        match command.spawn() {
            Ok(child) => self.working = Some(child),
            Err(err) => eprintln!("[screenx] could not start the capture: {err}"),
        }
    }

    /// Drain both channels. Called from `about_to_wait`, on the loop's thread.
    fn poll(&mut self, event_loop: &ActiveEventLoop) {
        while let Ok(event) = GlobalHotKeyEvent::receiver().try_recv() {
            if event.state != global_hotkey::HotKeyState::Pressed {
                continue;
            }
            let id = Some(event.id);
            if id == self.region {
                self.spawn("--region", false);
            } else if id == self.fullscreen {
                self.spawn("--fullscreen", false);
            }
        }

        while let Ok(event) = MenuEvent::receiver().try_recv() {
            match event.id.as_ref() {
                "folder" => tray::open_path(&tray::screenshot_folder()),
                "settings" => tray::open_settings(),
                "quit" => event_loop.exit(),
                _ => {}
            }
        }

        // Windows users expect a tray icon to open something on double click,
        // and the folder is the only thing worth opening. macOS shows the menu
        // on any click, so this never fires there.
        while let Ok(event) = TrayIconEvent::receiver().try_recv() {
            if let TrayIconEvent::DoubleClick { .. } = event {
                tray::open_path(&tray::screenshot_folder());
            }
        }
    }
}

impl ApplicationHandler for Listener {
    // No window is ever created, so neither of these has anything to do. They
    // are required by the trait.
    fn resumed(&mut self, _event_loop: &ActiveEventLoop) {}

    fn window_event(&mut self, _: &ActiveEventLoop, _: WindowId, _: WindowEvent) {}

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        self.poll(event_loop);
        event_loop.set_control_flow(ControlFlow::WaitUntil(std::time::Instant::now() + POLL));
    }
}

/// `screenx-capture`, beside this executable.
///
/// Resolved from `current_exe` rather than the working directory or the PATH,
/// so the pair stays together wherever the user puts them.
fn worker_path() -> Option<std::path::PathBuf> {
    let mut path = std::env::current_exe().ok()?;
    path.pop();
    path.push(if cfg!(windows) {
        "screenx-capture.exe"
    } else {
        "screenx-capture"
    });
    path.exists().then_some(path)
}

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

fn main() {
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
    let region = register(&configured.capture_region);
    let fullscreen = register(&configured.capture_fullscreen);

    // Held for the life of the process: dropping it removes the icon.
    let _tray = tray::build();

    let event_loop = match EventLoop::new() {
        Ok(loop_) => loop_,
        Err(err) => {
            eprintln!("[screenx] no event loop: {err}");
            return;
        }
    };
    // Nothing is animating and nothing is drawn; the loop exists to pump the
    // tray and shortcut messages and must sleep between them.
    event_loop.set_control_flow(ControlFlow::Wait);

    let mut listener = Listener {
        region,
        fullscreen,
        working: None,
    };
    if let Err(err) = event_loop.run_app(&mut listener) {
        eprintln!("[screenx] {err}");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn modifiers_are_taken_literally() {
        let parsed = parse_hotkey("Control+Shift+Q").unwrap();
        assert_eq!(parsed, HotKey::new(Some(Modifiers::CONTROL | Modifiers::SHIFT), Code::KeyQ));
    }

    #[test]
    fn a_shortcut_with_no_modifier_is_refused() {
        // It would take that key from every other application on the machine.
        assert!(parse_hotkey("Q").is_none());
        assert!(parse_hotkey("").is_none());
    }

    #[test]
    fn an_unknown_key_is_refused_rather_than_guessed() {
        assert!(parse_hotkey("Control+Shift+F13").is_none());
        assert!(parse_hotkey("Control+Shift").is_none());
    }
}
