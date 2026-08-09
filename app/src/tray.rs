// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! The menu bar icon.
//!
//! Deliberately not a way to capture: the hotkeys do that. This is the handful
//! of things that need somewhere to live — where the files went, how to change
//! the settings, and how to quit.

use std::path::{Path, PathBuf};

use screenx_core::settings;
use tray_icon::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    Icon, TrayIcon, TrayIconBuilder,
};

/// 32px is what both platforms ask for; macOS scales it for Retina itself.
const ICON: &[u8] = include_bytes!("../../src-tauri/icons/32x32.png");

fn icon() -> Option<Icon> {
    let image = image::load_from_memory(ICON).ok()?.into_rgba8();
    let (width, height) = image.dimensions();
    Icon::from_rgba(image.into_raw(), width, height).ok()
}

pub fn screenshot_folder() -> PathBuf {
    settings::screenshot_folder(&settings::get())
}

/// Hand a path to the desktop to deal with.
///
/// ponytail: three lines of `Command` rather than a crate. Opening a folder is
/// one call per platform and none of them need a dependency to make it.
pub fn open_path(path: &Path) {
    // The folder may not exist yet if nothing has been saved.
    if !path.exists() {
        let _ = std::fs::create_dir_all(path);
    }
    let command = if cfg!(target_os = "macos") {
        "open"
    } else if cfg!(target_os = "windows") {
        "explorer"
    } else {
        "xdg-open"
    };
    if let Err(err) = std::process::Command::new(command).arg(path).spawn() {
        eprintln!("[screenx] could not open {}: {err}", path.display());
    }
}

/// Open the settings file itself.
///
/// There is no settings window. The file is small, documented in the README and
/// read fresh on every capture, so handing it to the user's editor does the
/// whole job without a second UI to build and maintain.
pub fn open_settings() {
    let path = settings::file_path();
    if !path.exists() {
        // Write the defaults out first, so there is something to edit.
        settings::set(settings::get());
    }
    open_path(&path);
}

pub fn build() -> Option<TrayIcon> {
    let menu = Menu::new();
    let folder = MenuItem::with_id("folder", "Open Screenshots Folder", true, None);
    let prefs = MenuItem::with_id("settings", "Settings...", true, None);
    let quit = MenuItem::with_id("quit", "Quit ScreenX", true, None);
    menu.append_items(&[
        &folder,
        &prefs,
        &PredefinedMenuItem::separator(),
        &quit,
    ])
    .ok()?;

    let mut builder = TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_tooltip("ScreenX");

    if let Some(icon) = icon() {
        builder = builder.with_icon(icon);
    }
    // On Windows the menu is the right-click action, which leaves the left
    // click free for the shortcut people expect from a tray icon. macOS shows
    // the menu on either button, so double-click never arrives there.
    #[cfg(target_os = "windows")]
    {
        builder = builder.with_menu_on_left_click(false);
    }

    match builder.build() {
        Ok(tray) => Some(tray),
        Err(err) => {
            eprintln!("[screenx] no menu bar icon: {err}");
            None
        }
    }
}
