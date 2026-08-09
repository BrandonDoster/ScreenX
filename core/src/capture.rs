// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! Screen and window capture, plus everything that turns a captured image into
//! a file on disk.
//!
//! Two coordinate spaces meet here. Monitors and windows are reported in
//! device-independent pixels (what the overlay draws in), while captured images
//! are in physical pixels. Anything crossing that line goes through
//! `crop_to_rect`, which is the only place the scale factor is applied.

use std::path::{Path, PathBuf};

use image::{codecs::jpeg::JpegEncoder, ImageEncoder, RgbaImage};
use serde::{Deserialize, Serialize};

use crate::naming::{self, Context};
use crate::settings::{self, Settings};

/// A rectangle in device-independent pixels.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

impl Rect {
    pub fn right(&self) -> i32 {
        self.x + self.width as i32
    }
    pub fn bottom(&self) -> i32 {
        self.y + self.height as i32
    }
    pub fn contains(&self, px: i32, py: i32) -> bool {
        px >= self.x && px < self.right() && py >= self.y && py < self.bottom()
    }
    /// The overlapping part of two rectangles, if they overlap at all.
    pub fn intersect(&self, other: &Rect) -> Option<Rect> {
        let x = self.x.max(other.x);
        let y = self.y.max(other.y);
        let right = self.right().min(other.right());
        let bottom = self.bottom().min(other.bottom());
        if right <= x || bottom <= y {
            return None;
        }
        Some(Rect {
            x,
            y,
            width: (right - x) as u32,
            height: (bottom - y) as u32,
        })
    }
}

/// One monitor, frozen.
pub struct MonitorShot {
    pub id: u32,
    /// Position and size in device-independent pixels.
    pub bounds: Rect,
    pub scale: f32,
    pub name: String,
    pub image: RgbaImage,
}

#[derive(Serialize, Clone, Debug)]
pub struct WindowInfo {
    pub title: String,
    pub app: String,
    /// In device-independent pixels, same space as `MonitorShot::bounds`.
    pub rect: Rect,
}

/// Windows smaller than this are menu-bar strips, shadows and tooltips.
const MIN_WINDOW_SIDE: u32 = 48;

/// xcap reports macOS geometry in points but Windows geometry in physical
/// pixels. Everything downstream — overlay placement, hit testing, cropping —
/// works in device-independent pixels, so the conversion happens once, here.
///
/// ponytail: on Windows this divides positions by the scale of the monitor the
/// rectangle sits on, which is exact when every monitor shares a scale and
/// approximate when they do not. Getting mixed-DPI exactly right needs each
/// monitor's own origin; do that if anyone reports drift on such a setup.
fn to_dip(raw: Rect, scale: f32, raw_is_physical: bool) -> Rect {
    if !raw_is_physical || scale <= 0.0 || (scale - 1.0).abs() < f32::EPSILON {
        return raw;
    }
    Rect {
        x: (raw.x as f32 / scale).round() as i32,
        y: (raw.y as f32 / scale).round() as i32,
        width: (raw.width as f32 / scale).round() as u32,
        height: (raw.height as f32 / scale).round() as u32,
    }
}

/// True on the platforms where xcap hands back physical pixels.
const RAW_IS_PHYSICAL: bool = cfg!(windows);

/// Read a whole display straight out of the framebuffer.
///
/// xcap's `Monitor::capture_image` calls `CGWindowListCreateImage`, which
/// composites a *window list*. The menu bar background, the clock and every
/// status item are WindowServer and SystemUIServer surfaces that the list does
/// not hand to another process, so they come back missing while ordinary app
/// windows composite fine — a screenshot with the menu titles present but the
/// bar itself stripped. `CGDisplayCreateImage` reads the display instead of a
/// window list and gets the menu bar, the Dock and every other overlay.
///
/// ponytail: deprecated since macOS 14 in favour of ScreenCaptureKit, which is
/// async and a few hundred lines of bridging. Still works on 15; port when it
/// actually stops working.
#[cfg(target_os = "macos")]
#[allow(deprecated)]
fn display_image(display_id: u32) -> Result<RgbaImage, String> {
    use objc2_core_graphics::{CGDataProvider, CGDisplayCreateImage, CGImage};

    let image = CGDisplayCreateImage(display_id)
        .ok_or("could not read the display; check Screen Recording permission")?;
    let image = unsafe { image.as_ref() };

    let width = CGImage::width(Some(image));
    let height = CGImage::height(Some(image));
    let stride = CGImage::bytes_per_row(Some(image));
    let provider = CGImage::data_provider(Some(image));
    let data = CGDataProvider::data(provider.as_deref())
        .ok_or("could not read the display's pixels")?;
    // Borrowed rather than copied: a whole Retina display is 32 MB, and the
    // rows are about to be copied into the buffer anyway.
    // Safe: the CFData is alive for the rest of this function and nothing
    // mutates it.
    let data = unsafe { data.as_bytes_unchecked() };

    // Rows are padded to the hardware's alignment, so they are copied one at a
    // time rather than in bulk.
    let mut buffer = Vec::with_capacity(width * height * 4);
    for row in data.chunks_exact(stride) {
        buffer.extend_from_slice(&row[..width * 4]);
    }
    // The framebuffer is BGRA, and its alpha byte is "skip first" — undefined
    // rather than opaque, so it is set rather than trusted.
    for pixel in buffer.chunks_exact_mut(4) {
        pixel.swap(0, 2);
        pixel[3] = 255;
    }

    RgbaImage::from_raw(width as u32, height as u32, buffer)
        .ok_or_else(|| "the display returned an image of the wrong size".into())
}

/// Monitor geometry only, in device-independent pixels.
///
/// Warming the overlays needs to know where the monitors are, not what is on
/// them. Listing is cheap and, unlike a capture, does not ask the user for
/// Screen Recording permission — which must not happen just because the app
/// launched.
pub fn monitor_bounds() -> Vec<Rect> {
    let Ok(monitors) = xcap::Monitor::all() else {
        return Vec::new();
    };
    monitors
        .into_iter()
        .map(|monitor| {
            let scale = monitor.scale_factor().unwrap_or(1.0);
            let raw = Rect {
                x: monitor.x().unwrap_or(0),
                y: monitor.y().unwrap_or(0),
                width: monitor.width().unwrap_or(0),
                height: monitor.height().unwrap_or(0),
            };
            to_dip(raw, scale, RAW_IS_PHYSICAL)
        })
        .collect()
}

fn shoot(monitor: &xcap::Monitor) -> Result<MonitorShot, String> {
    let id = monitor.id().unwrap_or(0);
    // On macOS the id is the CGDirectDisplayID, which is what the framebuffer
    // read needs.
    #[cfg(target_os = "macos")]
    let image = display_image(id)?;
    #[cfg(not(target_os = "macos"))]
    let image = monitor
        .capture_image()
        .map_err(|e| format!("could not capture a monitor: {e}"))?;
    let scale = monitor.scale_factor().unwrap_or(1.0);
    let raw = Rect {
        x: monitor.x().unwrap_or(0),
        y: monitor.y().unwrap_or(0),
        width: monitor.width().unwrap_or(0),
        height: monitor.height().unwrap_or(0),
    };
    Ok(MonitorShot {
        id,
        bounds: to_dip(raw, scale, RAW_IS_PHYSICAL),
        scale,
        name: monitor.name().unwrap_or_default(),
        image,
    })
}

/// Read only the display the capture is actually going to use.
///
/// A capture is a whole framebuffer copy per monitor, and the overlay covers
/// one of them — so reading every display spent most of a second on a two
/// monitor desktop and then threw all but one of the images away.
pub fn capture_primary() -> Result<MonitorShot, String> {
    let monitors = xcap::Monitor::all().map_err(|e| format!("could not list monitors: {e}"))?;
    let monitor = monitors
        .iter()
        .find(|m| m.is_primary().unwrap_or(false))
        .or_else(|| monitors.first())
        .ok_or("no monitors found")?;
    shoot(monitor)
}

pub fn capture_monitors() -> Result<Vec<MonitorShot>, String> {
    let monitors = xcap::Monitor::all().map_err(|e| format!("could not list monitors: {e}"))?;
    if monitors.is_empty() {
        return Err("no monitors found".into());
    }
    monitors.iter().map(shoot).collect()
}

/// Visible windows, front-most first, excluding our own.
pub fn list_windows() -> Vec<WindowInfo> {
    let windows = match xcap::Window::all() {
        Ok(windows) => windows,
        Err(err) => {
            eprintln!("[capture] window list unavailable: {err}");
            return Vec::new();
        }
    };

    windows
        .into_iter()
        .filter(|w| !w.is_minimized().unwrap_or(false))
        .filter_map(|w| {
            let app = w.app_name().unwrap_or_default();
            if app.to_lowercase().contains("screenx") {
                return None;
            }
            let raw = Rect {
                x: w.x().ok()?,
                y: w.y().ok()?,
                width: w.width().ok()?,
                height: w.height().ok()?,
            };
            let scale = if RAW_IS_PHYSICAL {
                w.current_monitor()
                    .and_then(|m| m.scale_factor())
                    .unwrap_or(1.0)
            } else {
                1.0
            };
            let rect = to_dip(raw, scale, RAW_IS_PHYSICAL);
            if rect.width < MIN_WINDOW_SIDE || rect.height < MIN_WINDOW_SIDE {
                return None;
            }
            let title = w.title().unwrap_or_default();
            Some(WindowInfo {
                title: if title.trim().is_empty() { app.clone() } else { title },
                app,
                rect,
            })
        })
        .collect()
}

/// Clip a window list to one monitor and rebase it into that monitor's own
/// coordinates, which is what its overlay draws in.
pub fn windows_for_monitor(windows: &[WindowInfo], bounds: &Rect) -> Vec<WindowInfo> {
    windows
        .iter()
        .filter_map(|win| {
            let clipped = win.rect.intersect(bounds)?;
            if clipped.width < MIN_WINDOW_SIDE || clipped.height < MIN_WINDOW_SIDE {
                return None;
            }
            Some(WindowInfo {
                title: win.title.clone(),
                app: win.app.clone(),
                rect: Rect {
                    x: clipped.x - bounds.x,
                    y: clipped.y - bounds.y,
                    width: clipped.width,
                    height: clipped.height,
                },
            })
        })
        .collect()
}

/// Crop using a rectangle in device-independent pixels, relative to the
/// monitor's top-left corner.
pub fn crop_to_rect(shot: &MonitorShot, rect: &Rect) -> Option<RgbaImage> {
    // Derive the scale from the image itself; a monitor can report a scale that
    // does not match what the capture actually produced.
    let scale = if shot.bounds.width > 0 {
        shot.image.width() as f32 / shot.bounds.width as f32
    } else {
        shot.scale
    };

    let x = ((rect.x as f32 * scale).round().max(0.0)) as u32;
    let y = ((rect.y as f32 * scale).round().max(0.0)) as u32;
    let width = (rect.width as f32 * scale).round() as u32;
    let height = (rect.height as f32 * scale).round() as u32;

    let width = width.min(shot.image.width().saturating_sub(x));
    let height = height.min(shot.image.height().saturating_sub(y));
    if width < 1 || height < 1 {
        return None;
    }
    Some(image::imageops::crop_imm(&shot.image, x, y, width, height).to_image())
}

/// A frame for the overlay to look at, not to crop from.
///
/// The overlay canvas is sized in device-independent pixels, so on a Retina
/// panel the physical capture carries four times the pixels it can ever draw.
/// Both the encode here and the decode inside the webview are paid per pixel,
/// and the selection is still cropped from the untouched capture held in Rust,
/// so the preview is scaled to the size actually shown. The magnifier loupe is
/// the one thing that gives up detail for it.
pub fn preview(shot: &MonitorShot, quality: u8) -> Result<(Vec<u8>, &'static str), String> {
    if shot.image.width() == shot.bounds.width && shot.image.height() == shot.bounds.height {
        return encode(&shot.image, "jpg", quality);
    }
    let small = image::imageops::thumbnail(&shot.image, shot.bounds.width, shot.bounds.height);
    encode(&small, "jpg", quality)
}

pub fn encode(image: &RgbaImage, format: &str, quality: u8) -> Result<(Vec<u8>, &'static str), String> {
    let mut bytes = Vec::new();
    if format == "jpg" {
        // JPEG has no alpha channel. Built by hand rather than through
        // DynamicImage, which clones the whole RGBA buffer first — 32 MB of
        // copy on a Retina panel, for nothing.
        let mut rgb = image::RgbImage::new(image.width(), image.height());
        for (dst, src) in rgb.pixels_mut().zip(image.pixels()) {
            *dst = image::Rgb([src.0[0], src.0[1], src.0[2]]);
        }
        JpegEncoder::new_with_quality(&mut bytes, quality.clamp(10, 100))
            .write_image(rgb.as_raw(), rgb.width(), rgb.height(), image::ExtendedColorType::Rgb8)
            .map_err(|e| format!("could not encode JPEG: {e}"))?;
        Ok((bytes, "jpg"))
    } else {
        image::codecs::png::PngEncoder::new(&mut bytes)
            .write_image(
                image.as_raw(),
                image.width(),
                image.height(),
                image::ExtendedColorType::Rgba8,
            )
            .map_err(|e| format!("could not encode PNG: {e}"))?;
        Ok((bytes, "png"))
    }
}

/// Append " (2)", " (3)" ... until the path is free.
pub fn unique_path(folder: &Path, stem: &str, extension: &str) -> PathBuf {
    let mut candidate = folder.join(format!("{stem}.{extension}"));
    let mut n = 2;
    while candidate.exists() {
        candidate = folder.join(format!("{stem} ({n}).{extension}"));
        n += 1;
    }
    candidate
}

pub fn name_context(settings: &Settings, title: &str, width: u32, height: u32) -> Context {
    Context {
        counter: settings.auto_increment_number + 1,
        title: title.to_string(),
        width,
        height,
        ..Context::default()
    }
}

/// Encode and write an image using the configured folder and name pattern.
pub fn save_image(image: &RgbaImage, title: &str) -> Result<PathBuf, String> {
    let config = settings::get();
    let (bytes, extension) = encode(image, &config.image_format, config.jpeg_quality)?;
    let folder = settings::screenshot_folder(&config);
    let context = name_context(&config, title, image.width(), image.height());
    let stem = naming::parse_name(&config.screenshot_name_pattern, &context);
    let target = unique_path(&folder, &stem, extension);
    std::fs::write(&target, bytes).map_err(|e| format!("could not write {}: {e}", target.display()))?;
    settings::advance_counter();
    Ok(target)
}

pub fn copy_to_clipboard(image: &RgbaImage) -> Result<(), String> {
    let mut clipboard = arboard::Clipboard::new().map_err(|e| format!("no clipboard: {e}"))?;
    clipboard
        .set_image(arboard::ImageData {
            width: image.width() as usize,
            height: image.height() as usize,
            bytes: std::borrow::Cow::Borrowed(image.as_raw()),
        })
        .map_err(|e| format!("could not copy image: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rect(x: i32, y: i32, width: u32, height: u32) -> Rect {
        Rect { x, y, width, height }
    }

    #[test]
    fn intersect_finds_the_overlap() {
        let a = rect(0, 0, 100, 100);
        assert_eq!(a.intersect(&rect(50, 50, 100, 100)), Some(rect(50, 50, 50, 50)));
        assert_eq!(a.intersect(&rect(200, 0, 10, 10)), None);
        // Touching edges do not overlap.
        assert_eq!(a.intersect(&rect(100, 0, 10, 10)), None);
    }

    #[test]
    fn contains_excludes_the_far_edge() {
        let a = rect(10, 10, 20, 20);
        assert!(a.contains(10, 10));
        assert!(a.contains(29, 29));
        assert!(!a.contains(30, 30));
        assert!(!a.contains(9, 15));
    }

    #[test]
    fn windows_are_clipped_and_rebased_onto_a_monitor() {
        let monitor = rect(1000, 0, 800, 600);
        let windows = vec![
            WindowInfo { title: "Straddling".into(), app: "A".into(), rect: rect(900, -50, 400, 300) },
            WindowInfo { title: "Elsewhere".into(), app: "B".into(), rect: rect(0, 0, 500, 400) },
            WindowInfo { title: "Contained".into(), app: "C".into(), rect: rect(1100, 100, 200, 150) },
        ];
        let clipped = windows_for_monitor(&windows, &monitor);
        assert_eq!(clipped.len(), 2);
        assert_eq!(clipped[0].title, "Straddling");
        assert_eq!(clipped[0].rect, rect(0, 0, 300, 250));
        assert_eq!(clipped[1].title, "Contained");
        assert_eq!(clipped[1].rect, rect(100, 100, 200, 150));
    }

    #[test]
    fn slivers_of_a_window_are_not_offered() {
        let monitor = rect(0, 0, 800, 600);
        let windows = vec![WindowInfo {
            title: "Barely there".into(),
            app: "A".into(),
            rect: rect(-380, 100, 400, 300),
        }];
        assert!(windows_for_monitor(&windows, &monitor).is_empty());
    }

    fn shot(scale: f32) -> MonitorShot {
        let bounds = rect(0, 0, 400, 300);
        let physical = (400.0 * scale) as u32;
        MonitorShot {
            id: 1,
            bounds,
            scale,
            name: "test".into(),
            image: RgbaImage::from_pixel(physical, (300.0 * scale) as u32, image::Rgba([1, 2, 3, 255])),
        }
    }

    #[test]
    fn physical_geometry_is_converted_to_device_independent_pixels() {
        let raw = rect(2560, 0, 2560, 1440);
        // Windows hands back physical pixels, so a 150% display has to shrink.
        assert_eq!(to_dip(raw, 1.5, true), rect(1707, 0, 1707, 960));
        // macOS already reports points, so nothing moves.
        assert_eq!(to_dip(raw, 2.0, false), raw);
        // A 100% display needs no conversion on either platform.
        assert_eq!(to_dip(raw, 1.0, true), raw);
        // A nonsense scale must not produce a divide-by-zero rectangle.
        assert_eq!(to_dip(raw, 0.0, true), raw);
    }

    #[test]
    fn cropping_applies_the_scale_factor() {
        let retina = shot(2.0);
        let cropped = crop_to_rect(&retina, &rect(10, 20, 100, 50)).unwrap();
        assert_eq!((cropped.width(), cropped.height()), (200, 100));

        let plain = shot(1.0);
        let cropped = crop_to_rect(&plain, &rect(10, 20, 100, 50)).unwrap();
        assert_eq!((cropped.width(), cropped.height()), (100, 50));
    }

    #[test]
    fn cropping_is_clamped_to_the_image() {
        let retina = shot(2.0);
        // Runs off the right edge; the result stops at the edge instead of failing.
        let cropped = crop_to_rect(&retina, &rect(350, 0, 100, 50)).unwrap();
        assert_eq!(cropped.width(), 100);
        assert!(crop_to_rect(&retina, &rect(400, 0, 10, 10)).is_none());
    }

    #[test]
    fn png_and_jpeg_both_encode() {
        let image = RgbaImage::from_pixel(8, 8, image::Rgba([200, 100, 50, 255]));
        let (png, ext) = encode(&image, "png", 90).unwrap();
        assert_eq!(ext, "png");
        assert_eq!(&png[1..4], b"PNG");

        let (jpg, ext) = encode(&image, "jpg", 90).unwrap();
        assert_eq!(ext, "jpg");
        assert_eq!(&jpg[0..2], &[0xff, 0xd8]);
    }

    #[test]
    fn unique_path_never_overwrites() {
        let dir = std::env::temp_dir().join(format!("screenx-unique-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let first = unique_path(&dir, "shot", "png");
        assert_eq!(first.file_name().unwrap(), "shot.png");
        std::fs::write(&first, b"x").unwrap();
        let second = unique_path(&dir, "shot", "png");
        assert_eq!(second.file_name().unwrap(), "shot (2).png");
        std::fs::remove_dir_all(&dir).ok();
    }
}
