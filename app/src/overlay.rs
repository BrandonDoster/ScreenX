// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! The selection overlay: a frozen capture, a dimmed screen, and a rectangle.

use eframe::egui;
use screenx_core::capture::{MonitorShot, Rect};

/// What the user did with the overlay.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Outcome {
    Selected(Rect),
    Cancelled,
}

/// The smallest drag that counts, in points. Below this it was a stray click.
const MIN_SIZE: f32 = 4.0;

pub struct Overlay {
    shot: MonitorShot,
    texture: egui::TextureHandle,
    drag_start: Option<egui::Pos2>,
    selection: Option<egui::Rect>,
}

impl Overlay {
    /// Uploads the capture as a texture. One copy, held until the overlay is
    /// dismissed; this is the whole of what the overlay costs in memory.
    pub fn new(ctx: &egui::Context, shot: MonitorShot) -> Self {
        let image = egui::ColorImage::from_rgba_unmultiplied(
            [
                shot.image.width() as usize,
                shot.image.height() as usize,
            ],
            shot.image.as_raw(),
        );
        let texture = ctx.load_texture("capture", image, egui::TextureOptions::LINEAR);
        Self {
            shot,
            texture,
            drag_start: None,
            selection: None,
        }
    }

    pub fn shot(&self) -> &MonitorShot {
        &self.shot
    }

    /// The overlay draws in points and the capture is in physical pixels.
    ///
    /// Returning the rectangle in points keeps the old build's rule intact:
    /// `crop_to_rect` stays the only place a scale factor is ever applied, so
    /// nothing here has to know what the scale is.
    fn to_points(rect: egui::Rect) -> Rect {
        Rect {
            x: rect.min.x.round() as i32,
            y: rect.min.y.round() as i32,
            width: rect.width().round().max(0.0) as u32,
            height: rect.height().round().max(0.0) as u32,
        }
    }

    /// Draws a frame and reports an outcome once there is one.
    pub fn update(&mut self, ctx: &egui::Context) -> Option<Outcome> {
        let mut outcome = None;

        egui::CentralPanel::default()
            .frame(egui::Frame::none())
            .show(ctx, |ui| {
                let screen = ui.max_rect();
                let response =
                    ui.interact(screen, ui.id().with("overlay"), egui::Sense::click_and_drag());

                if response.drag_started() {
                    self.drag_start = response.interact_pointer_pos();
                }
                if let (Some(start), Some(now)) =
                    (self.drag_start, response.interact_pointer_pos())
                {
                    self.selection = Some(egui::Rect::from_two_pos(start, now));
                }
                if response.drag_stopped() {
                    if let Some(sel) = self.selection {
                        if sel.width() >= MIN_SIZE && sel.height() >= MIN_SIZE {
                            outcome = Some(Outcome::Selected(Self::to_points(sel)));
                        }
                    }
                    self.drag_start = None;
                }

                let painter = ui.painter();
                painter.image(
                    self.texture.id(),
                    screen,
                    egui::Rect::from_min_max(egui::pos2(0.0, 0.0), egui::pos2(1.0, 1.0)),
                    egui::Color32::WHITE,
                );

                // Everything outside the selection is dimmed, so the chosen
                // area reads as the part that is not covered.
                let dim = egui::Color32::from_black_alpha(110);
                match self.selection {
                    None => {
                        painter.rect_filled(screen, 0.0, dim);
                    }
                    Some(sel) => {
                        for band in [
                            egui::Rect::from_min_max(
                                screen.min,
                                egui::pos2(screen.max.x, sel.min.y),
                            ),
                            egui::Rect::from_min_max(
                                egui::pos2(screen.min.x, sel.max.y),
                                screen.max,
                            ),
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
                        painter.text(
                            sel.left_top() + egui::vec2(2.0, -4.0),
                            egui::Align2::LEFT_BOTTOM,
                            format!("{} x {}", sel.width().round(), sel.height().round()),
                            egui::FontId::proportional(13.0),
                            egui::Color32::WHITE,
                        );
                    }
                }
            });

        // The overlay covers the menu bar, so the way out must not depend on
        // reaching anything else on screen.
        if ctx.input(|i| i.key_pressed(egui::Key::Escape)) {
            outcome = Some(Outcome::Cancelled);
        }
        if ctx.input(|i| i.pointer.secondary_clicked()) {
            outcome = Some(Outcome::Cancelled);
        }

        outcome
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_rectangle_is_reported_in_points() {
        let rect = Overlay::to_points(egui::Rect::from_min_max(
            egui::pos2(10.4, 20.6),
            egui::pos2(110.4, 100.6),
        ));
        assert_eq!(
            rect,
            Rect {
                x: 10,
                y: 21,
                width: 100,
                height: 80
            }
        );
    }

    #[test]
    fn a_backwards_drag_still_has_a_positive_size() {
        // from_two_pos normalises, which is what stops a right-to-left drag
        // reporting a negative width the way an unordered pair would.
        let rect = Overlay::to_points(egui::Rect::from_two_pos(
            egui::pos2(200.0, 150.0),
            egui::pos2(100.0, 50.0),
        ));
        assert_eq!(
            rect,
            Rect {
                x: 100,
                y: 50,
                width: 100,
                height: 100
            }
        );
    }
}
