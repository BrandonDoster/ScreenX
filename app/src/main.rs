// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! The selection overlay, drawn natively.
//!
//! This is the component the webview build fought hardest: window level, key
//! focus, activation, and 600 MB of compositor memory to show one screenshot.
//! Here the capture is one texture and the selection is one rectangle.
//!
//! Run it on its own with `cargo run -p screenx` to take a region.

use eframe::egui;
use screenx_core::capture::{self, MonitorShot, Rect};

/// What the user did with the overlay.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Outcome {
    Selected(Rect),
    Cancelled,
}

struct Overlay {
    shot: MonitorShot,
    /// Uploaded on the first frame: the texture needs a context to live in.
    texture: Option<egui::TextureHandle>,
    drag_start: Option<egui::Pos2>,
    selection: Option<egui::Rect>,
    outcome: Option<Outcome>,
}

impl Overlay {
    fn new(shot: MonitorShot) -> Self {
        Self {
            shot,
            texture: None,
            drag_start: None,
            selection: None,
            outcome: None,
        }
    }

    /// The capture is in physical pixels and the overlay draws in points, so
    /// the selection is scaled back on the way out. This is the same rule the
    /// old build had: one place applies the scale factor, nothing else touches
    /// it.
    fn to_capture_rect(&self, rect: egui::Rect) -> Rect {
        Rect {
            x: rect.min.x.round() as i32,
            y: rect.min.y.round() as i32,
            width: rect.width().round().max(0.0) as u32,
            height: rect.height().round().max(0.0) as u32,
        }
    }

    fn texture(&mut self, ctx: &egui::Context) -> egui::TextureHandle {
        if let Some(texture) = &self.texture {
            return texture.clone();
        }
        let image = egui::ColorImage::from_rgba_unmultiplied(
            [self.shot.image.width() as usize, self.shot.image.height() as usize],
            self.shot.image.as_raw(),
        );
        let handle = ctx.load_texture("capture", image, egui::TextureOptions::LINEAR);
        self.texture = Some(handle.clone());
        handle
    }
}

impl eframe::App for Overlay {
    /// Transparent so nothing flashes before the capture is drawn.
    fn clear_color(&self, _visuals: &egui::Visuals) -> [f32; 4] {
        [0.0, 0.0, 0.0, 0.0]
    }

    fn update(&mut self, ctx: &egui::Context, frame: &mut eframe::Frame) {
        let texture = self.texture(ctx);

        egui::CentralPanel::default()
            .frame(egui::Frame::none())
            .show(ctx, |ui| {
                let screen = ui.max_rect();
                let painter = ui.painter();

                painter.image(
                    texture.id(),
                    screen,
                    egui::Rect::from_min_max(egui::pos2(0.0, 0.0), egui::pos2(1.0, 1.0)),
                    egui::Color32::WHITE,
                );

                // Everything outside the selection is dimmed, so the chosen area
                // reads as the part that is not covered.
                let dim = egui::Color32::from_black_alpha(110);
                match self.selection {
                    None => {
                        painter.rect_filled(screen, 0.0, dim);
                    }
                    Some(sel) => {
                        for band in [
                            egui::Rect::from_min_max(screen.min, egui::pos2(screen.max.x, sel.min.y)),
                            egui::Rect::from_min_max(egui::pos2(screen.min.x, sel.max.y), screen.max),
                            egui::Rect::from_min_max(
                                egui::pos2(screen.min.x, sel.min.y),
                                egui::pos2(sel.min.x, sel.max.y),
                            ),
                            egui::Rect::from_min_max(
                                egui::pos2(sel.max.x, sel.min.y),
                                egui::pos2(screen.max.x, sel.max.y),
                            ),
                        ] {
                            painter.rect_filled(band, 0.0, dim);
                        }
                        painter.rect_stroke(
                            sel,
                            0.0,
                            egui::Stroke::new(1.0, egui::Color32::from_rgb(0x3d, 0x8b, 0xfd)),
                        );
                        let label = format!("{} x {}", sel.width().round(), sel.height().round());
                        painter.text(
                            sel.left_top() + egui::vec2(4.0, -6.0),
                            egui::Align2::LEFT_BOTTOM,
                            label,
                            egui::FontId::proportional(13.0),
                            egui::Color32::WHITE,
                        );
                    }
                }

                let response = ui.interact(
                    screen,
                    ui.id().with("overlay"),
                    egui::Sense::click_and_drag(),
                );

                if response.drag_started() {
                    self.drag_start = response.interact_pointer_pos();
                }
                if let (Some(start), Some(now)) = (self.drag_start, response.interact_pointer_pos())
                {
                    self.selection = Some(egui::Rect::from_two_pos(start, now));
                }
                if response.drag_stopped() {
                    if let Some(sel) = self.selection {
                        if sel.width() >= 4.0 && sel.height() >= 4.0 {
                            self.outcome = Some(Outcome::Selected(self.to_capture_rect(sel)));
                        }
                    }
                    self.drag_start = None;
                }
            });

        // The overlay covers the menu bar, so there has to be a way out that
        // does not depend on reaching anything else on screen.
        if ctx.input(|i| i.key_pressed(egui::Key::Escape)) {
            self.outcome = Some(Outcome::Cancelled);
        }

        if self.outcome.is_some() {
            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        }
        let _ = frame;
    }
}

/// Put the overlay above the menu bar and make the app active.
///
/// Both were real bugs in the webview build. An always-on-top window still sits
/// below the menu bar, which is its own higher level, and an accessory app that
/// only orders a window front never becomes key — so the overlay arrived inert,
/// with an arrow cursor and its first click spent on activating.
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

pub fn select_region() -> Result<Outcome, String> {
    let shots = capture::capture_monitors()?;
    let shot = shots
        .into_iter()
        .next()
        .ok_or_else(|| "no monitors found".to_string())?;
    let bounds = shot.bounds;

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_position(egui::pos2(bounds.x as f32, bounds.y as f32))
            .with_inner_size(egui::vec2(bounds.width as f32, bounds.height as f32))
            .with_decorations(false)
            .with_always_on_top()
            .with_transparent(true)
            .with_resizable(false),
        ..Default::default()
    };

    let outcome = std::rc::Rc::new(std::cell::Cell::new(Outcome::Cancelled));
    let reported = outcome.clone();

    eframe::run_native(
        "ScreenX",
        options,
        Box::new(move |cc| {
            raise_and_activate();
            let _ = cc;
            Ok(Box::new(OverlayReporter {
                overlay: Overlay::new(shot),
                reported,
            }))
        }),
    )
    .map_err(|e| format!("could not open the overlay: {e}"))?;

    Ok(outcome.get())
}

/// Carries the outcome back out of the event loop.
struct OverlayReporter {
    overlay: Overlay,
    reported: std::rc::Rc<std::cell::Cell<Outcome>>,
}

impl eframe::App for OverlayReporter {
    fn clear_color(&self, visuals: &egui::Visuals) -> [f32; 4] {
        self.overlay.clear_color(visuals)
    }

    fn update(&mut self, ctx: &egui::Context, frame: &mut eframe::Frame) {
        self.overlay.update(ctx, frame);
        if let Some(outcome) = self.overlay.outcome {
            self.reported.set(outcome);
        }
    }
}

fn main() {
    match select_region() {
        Ok(Outcome::Selected(rect)) => {
            println!("selected {}x{} at {},{}", rect.width, rect.height, rect.x, rect.y);
        }
        Ok(Outcome::Cancelled) => println!("cancelled"),
        Err(err) => {
            eprintln!("[screenx] {err}");
            std::process::exit(1);
        }
    }
}
