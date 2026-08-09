// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! Every preference lives in one JSON file next to the app's config directory.
//! Unknown keys are ignored and missing keys fall back to the default, so an
//! older or newer file never stops the app from starting.

use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};

use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase", default)]
pub struct Features {
    pub capture_fullscreen: bool,
    /// Covers window capture too: the region overlay highlights whatever window
    /// the pointer rests on, so there is no separate window picker.
    pub capture_region: bool,
    pub editor: bool,
}

impl Default for Features {
    fn default() -> Self {
        Self {
            capture_fullscreen: true,
            capture_region: true,
            editor: true,
        }
    }
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase", default)]
pub struct Hotkeys {
    pub capture_fullscreen: String,
    pub capture_region: String,
}

impl Default for Hotkeys {
    fn default() -> Self {
        // Rare combinations: a global hotkey takes the key from every other app.
        Self {
            capture_fullscreen: "Control+Alt+F".into(),
            capture_region: "Control+Alt+A".into(),
        }
    }
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    pub screenshot_folder: String,
    pub screenshot_name_pattern: String,
    /// "png" or "jpg"
    pub image_format: String,
    pub jpeg_quality: u8,
    /// "editor", "save", "copy" or "saveCopy"
    pub after_capture: String,
    pub copy_path_after_save: bool,
    /// Put the image on the clipboard as well as writing it, when the editor's
    /// Save is used. Saving and copying are usually wanted together.
    pub copy_image_on_save: bool,
    pub auto_increment_number: u64,
    /// How long the pointer must rest before a window lights up, in
    /// milliseconds. 0 highlights immediately.
    pub window_highlight_delay_ms: u64,
    /// How long to wait after the hotkey before the screen is read, in
    /// milliseconds. 0 captures at once. This is the only way to capture an
    /// open menu; see `capture_delay` in lib.rs.
    pub capture_delay_ms: u64,
    /// Where the editor window was last left, in points.
    ///
    /// This lives on disk rather than in memory because the editor is its own
    /// process and does not outlive one screenshot. Nothing else in the file is
    /// window state; it is here because there is nowhere else for it to go.
    pub editor_position: Option<[f32; 2]>,
    pub features: Features,
    pub hotkeys: Hotkeys,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            screenshot_folder: default_folder().to_string_lossy().to_string(),
            screenshot_name_pattern: "ScreenX_%y-%mo-%d_%h-%mi-%s".into(),
            image_format: "png".into(),
            jpeg_quality: 90,
            after_capture: "editor".into(),
            copy_path_after_save: false,
            copy_image_on_save: false,
            auto_increment_number: 0,
            window_highlight_delay_ms: 400,
            capture_delay_ms: 0,
            editor_position: None,
            features: Features::default(),
            hotkeys: Hotkeys::default(),
        }
    }
}

pub fn default_folder() -> PathBuf {
    let base = dirs::picture_dir()
        .or_else(dirs::home_dir)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("ScreenX").join("Screenshots")
}

fn config_path() -> PathBuf {
    let base = dirs::config_dir()
        .or_else(dirs::home_dir)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("ScreenX").join("settings.json")
}

static STORE: OnceLock<Mutex<Settings>> = OnceLock::new();

fn store() -> &'static Mutex<Settings> {
    STORE.get_or_init(|| {
        let loaded = std::fs::read_to_string(config_path())
            .ok()
            .and_then(|text| serde_json::from_str::<Settings>(&text).ok())
            .unwrap_or_default();
        Mutex::new(loaded)
    })
}

pub fn get() -> Settings {
    store().lock().unwrap().clone()
}

pub fn set(next: Settings) -> Settings {
    let mut guard = store().lock().unwrap();
    *guard = next;
    let snapshot = guard.clone();
    drop(guard);
    write(&snapshot);
    snapshot
}

/// Remember where the editor was left, without disturbing anything else.
///
/// Read-modify-write of the whole file, like `advance_counter`. The editor
/// process writes this as it exits while the listener may be reading it, so it
/// must not clobber a setting the user changed in between.
pub fn remember_editor_position(position: [f32; 2]) {
    let mut guard = store().lock().unwrap();
    guard.editor_position = Some(position);
    let snapshot = guard.clone();
    drop(guard);
    write(&snapshot);
}

/// Bump the counter used by `%i` without disturbing anything else.
pub fn advance_counter() {
    let mut guard = store().lock().unwrap();
    guard.auto_increment_number = guard.auto_increment_number.saturating_add(1);
    let snapshot = guard.clone();
    drop(guard);
    write(&snapshot);
}

fn write(settings: &Settings) {
    let path = config_path();
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    match serde_json::to_string_pretty(settings) {
        Ok(text) => {
            if let Err(err) = std::fs::write(&path, text) {
                eprintln!("[settings] could not write {}: {err}", path.display());
            }
        }
        Err(err) => eprintln!("[settings] could not serialise: {err}"),
    }
}

pub fn file_path() -> PathBuf {
    config_path()
}

/// Where screenshots go, created on demand.
pub fn screenshot_folder(settings: &Settings) -> PathBuf {
    let folder = if settings.screenshot_folder.trim().is_empty() {
        default_folder()
    } else {
        PathBuf::from(&settings.screenshot_folder)
    };
    let _ = std::fs::create_dir_all(&folder);
    folder
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn missing_keys_fall_back_to_defaults() {
        let partial = r#"{"imageFormat":"jpg","features":{"captureFullscreen":false}}"#;
        let parsed: Settings = serde_json::from_str(partial).unwrap();
        assert_eq!(parsed.image_format, "jpg");
        assert!(!parsed.features.capture_fullscreen);
        // Untouched keys keep their defaults rather than becoming empty.
        assert!(parsed.features.capture_region);
        assert_eq!(parsed.screenshot_name_pattern, "ScreenX_%y-%mo-%d_%h-%mi-%s");
        assert_eq!(parsed.hotkeys.capture_region, "Control+Alt+A");
        // A file written before the delay existed must not start waiting.
        assert_eq!(parsed.capture_delay_ms, 0);
    }

    #[test]
    fn unknown_keys_are_ignored() {
        let future = r#"{"imageFormat":"png","somethingNew":42,"gif":{"fps":15}}"#;
        let parsed: Settings = serde_json::from_str(future).unwrap();
        assert_eq!(parsed.image_format, "png");
    }

    #[test]
    fn round_trips_through_json() {
        let mut original = Settings::default();
        original.jpeg_quality = 77;
        original.window_highlight_delay_ms = 250;
        original.capture_delay_ms = 5000;
        original.hotkeys.capture_region = "Control+Shift+Q".into();
        let text = serde_json::to_string(&original).unwrap();
        let parsed: Settings = serde_json::from_str(&text).unwrap();
        assert_eq!(parsed.jpeg_quality, 77);
        assert_eq!(parsed.window_highlight_delay_ms, 250);
        assert_eq!(parsed.capture_delay_ms, 5000);
        assert_eq!(parsed.hotkeys.capture_region, "Control+Shift+Q");
    }

    #[test]
    fn a_corrupt_file_does_not_produce_settings() {
        assert!(serde_json::from_str::<Settings>("{ not json").is_err());
    }
}
