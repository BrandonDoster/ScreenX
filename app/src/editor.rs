// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! The annotation editor.
//!
//! Annotations are shapes in image coordinates, drawn over the capture rather
//! than into it, so they stay editable until something forces them down. The
//! tools that rewrite pixels — crop, cut out, blur, pixelate — flatten the
//! shapes into the image first, which is what makes undo across them work.
//!
//! Memory is the constraint the webview build failed. History shares the image
//! through an `Arc`, so adding a shape costs a `Vec` entry and only a
//! destructive edit ever allocates a new one.

use std::sync::Arc;

use eframe::egui;
use image::RgbaImage;
use screenx_core::capture::{self, Rect};

use crate::edits;
use crate::render::{self, draw_shapes_into};

/// How many steps back undo goes.
const HISTORY_LIMIT: usize = 24;

/// How much history is allowed to hold in images, in bytes.
///
/// Counting steps is not enough: a step that adds a shape shares the image it
/// started from, but a crop or a blur allocates a new one. Twenty-four of those
/// on a full screen capture would be most of a gigabyte, which is the failure
/// this whole rewrite exists to avoid.
const HISTORY_BYTES: usize = 192 * 1024 * 1024;

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Tool {
    Select,
    Rect,
    Ellipse,
    Arrow,
    Line,
    Pen,
    Highlight,
    Blur,
    Pixelate,
    Text,
    Step,
    Crop,
    Cutout,
}

impl Tool {
    /// Tools that rewrite the image instead of adding a shape.
    fn destructive(self) -> bool {
        matches!(self, Tool::Blur | Tool::Pixelate | Tool::Crop | Tool::Cutout)
    }

    const ALL: [(Tool, &'static str, &'static str); 13] = [
        (Tool::Select, "Select", "Select and move"),
        (Tool::Rect, "Rect", "Rectangle"),
        (Tool::Ellipse, "Ellipse", "Ellipse"),
        (Tool::Arrow, "Arrow", "Arrow"),
        (Tool::Line, "Line", "Line"),
        (Tool::Pen, "Pen", "Freehand"),
        (Tool::Highlight, "Mark", "Highlighter"),
        (Tool::Blur, "Blur", "Blur to redact"),
        (Tool::Pixelate, "Pixel", "Pixelate to redact"),
        (Tool::Text, "Text", "Text"),
        (Tool::Step, "Step", "Step number"),
        (Tool::Crop, "Crop", "Crop"),
        (Tool::Cutout, "Cut", "Cut out a band and close the gap"),
    ];
}

#[derive(Clone)]
pub enum Shape {
    Rect { rect: Rect, filled: bool },
    Ellipse { rect: Rect, filled: bool },
    Arrow { from: [f32; 2], to: [f32; 2] },
    Line { from: [f32; 2], to: [f32; 2] },
    Pen { points: Vec<[f32; 2]> },
    Highlight { rect: Rect },
    Text { at: [f32; 2], text: String, size: f32 },
    Step { at: [f32; 2], number: u32 },
}

#[derive(Clone)]
pub struct Drawn {
    pub shape: Shape,
    pub colour: egui::Color32,
    pub width: f32,
}

#[derive(Clone)]
struct Snapshot {
    /// Shared: adding a shape does not copy the image.
    image: Arc<RgbaImage>,
    shapes: Vec<Drawn>,
    step: u32,
}

pub struct Editor {
    image: Arc<RgbaImage>,
    texture: Option<egui::TextureHandle>,
    shapes: Vec<Drawn>,
    history: Vec<Snapshot>,
    at: usize,
    pub title: String,

    tool: Tool,
    colour: egui::Color32,
    width: f32,
    font_size: f32,
    filled: bool,
    step: u32,

    drag_from: Option<[f32; 2]>,
    drag_to: Option<[f32; 2]>,
    pen: Vec<[f32; 2]>,
    typing: Option<([f32; 2], String)>,
    pub status: Option<String>,
    pub done: bool,
}

/// The swatches from the old build, kept so muscle memory carries over.
const SWATCHES: [egui::Color32; 8] = [
    egui::Color32::from_rgb(0xe5, 0x48, 0x4d),
    egui::Color32::from_rgb(0xf5, 0xa5, 0x24),
    egui::Color32::from_rgb(0xf7, 0xe7, 0x33),
    egui::Color32::from_rgb(0x37, 0xb2, 0x4d),
    egui::Color32::from_rgb(0x3d, 0x8b, 0xfd),
    egui::Color32::from_rgb(0x9b, 0x59, 0xf5),
    egui::Color32::WHITE,
    egui::Color32::BLACK,
];

impl Editor {
    pub fn new(image: RgbaImage, title: String) -> Self {
        let image = Arc::new(image);
        Self {
            history: vec![Snapshot {
                image: image.clone(),
                shapes: Vec::new(),
                step: 1,
            }],
            image,
            texture: None,
            shapes: Vec::new(),
            at: 0,
            title,
            tool: Tool::Rect,
            colour: SWATCHES[0],
            width: 3.0,
            font_size: 28.0,
            filled: false,
            step: 1,
            drag_from: None,
            drag_to: None,
            pen: Vec::new(),
            typing: None,
            status: None,
            done: false,
        }
    }

    fn push_history(&mut self) {
        self.history.truncate(self.at + 1);
        self.history.push(Snapshot {
            image: self.image.clone(),
            shapes: self.shapes.clone(),
            step: self.step,
        });
        while self.history.len() > HISTORY_LIMIT || self.history_bytes() > HISTORY_BYTES {
            if self.history.len() <= 2 {
                break;
            }
            self.history.remove(0);
        }
        self.at = self.history.len() - 1;
    }

    /// Bytes held by the distinct images in history. Snapshots that share one
    /// through the `Arc` are counted once, which is the point of sharing it.
    fn history_bytes(&self) -> usize {
        let mut seen: Vec<*const RgbaImage> = Vec::new();
        let mut total = 0;
        for snapshot in &self.history {
            let pointer = Arc::as_ptr(&snapshot.image);
            if !seen.contains(&pointer) {
                seen.push(pointer);
                total += snapshot.image.as_raw().len();
            }
        }
        total
    }

    fn restore(&mut self, index: usize) {
        let Some(snapshot) = self.history.get(index).cloned() else {
            return;
        };
        let changed = !Arc::ptr_eq(&self.image, &snapshot.image);
        self.image = snapshot.image;
        self.shapes = snapshot.shapes;
        self.step = snapshot.step;
        self.at = index;
        if changed {
            // The pixels moved, so the uploaded copy is stale.
            self.texture = None;
        }
    }

    fn undo(&mut self) {
        if self.at > 0 {
            self.restore(self.at - 1);
        }
    }

    fn redo(&mut self) {
        if self.at + 1 < self.history.len() {
            self.restore(self.at + 1);
        }
    }

    /// Bake every shape into the image. Needed before anything rewrites pixels,
    /// or a crop would move the capture out from under its annotations.
    fn flatten(&mut self) {
        if self.shapes.is_empty() {
            return;
        }
        let mut flat = (*self.image).clone();
        draw_shapes_into(&mut flat, &self.shapes);
        self.image = Arc::new(flat);
        self.shapes.clear();
        self.texture = None;
    }

    fn replace_image(&mut self, image: RgbaImage) {
        self.image = Arc::new(image);
        self.texture = None;
        self.push_history();
    }

    /// The finished picture: the capture with everything drawn onto it.
    pub fn composite(&self) -> RgbaImage {
        let mut flat = (*self.image).clone();
        draw_shapes_into(&mut flat, &self.shapes);
        flat
    }

    fn save(&mut self) {
        match capture::save_image(&self.composite(), &self.title) {
            Ok(path) => {
                self.status = Some(format!(
                    "saved {}",
                    path.file_name().unwrap_or_default().to_string_lossy()
                ));
                self.done = true;
            }
            Err(err) => self.status = Some(err),
        }
    }

    fn copy(&mut self) {
        self.status = Some(match capture::copy_to_clipboard(&self.composite()) {
            Ok(()) => "copied".into(),
            Err(err) => err,
        });
    }

    fn texture(&mut self, ctx: &egui::Context) -> egui::TextureHandle {
        if let Some(texture) = &self.texture {
            return texture.clone();
        }
        let colour = egui::ColorImage::from_rgba_unmultiplied(
            [self.image.width() as usize, self.image.height() as usize],
            self.image.as_raw(),
        );
        let handle = ctx.load_texture("editor", colour, egui::TextureOptions::LINEAR);
        self.texture = Some(handle.clone());
        handle
    }
}

/// A rectangle from two corners, in whichever order they were dragged.
fn rect_from(a: [f32; 2], b: [f32; 2]) -> Rect {
    Rect {
        x: a[0].min(b[0]).round() as i32,
        y: a[1].min(b[1]).round() as i32,
        width: (a[0] - b[0]).abs().round() as u32,
        height: (a[1] - b[1]).abs().round() as u32,
    }
}

impl Editor {
    /// Pointer position in image coordinates, clamped to the image.
    ///
    /// The pointer keeps reporting once it leaves the canvas, and every tool
    /// reads from here. Unclamped, dragging past an edge to cut the last 20px
    /// measured the whole 40 and took 20px of real image with it.
    fn point(&self, screen: egui::Rect, scale: f32, at: egui::Pos2) -> [f32; 2] {
        [
            ((at.x - screen.min.x) / scale).clamp(0.0, self.image.width() as f32),
            ((at.y - screen.min.y) / scale).clamp(0.0, self.image.height() as f32),
        ]
    }

    fn commit(&mut self, shape: Shape) {
        self.shapes.push(Drawn {
            shape,
            colour: self.colour,
            width: self.width,
        });
        self.push_history();
    }

    /// Apply a tool that rewrites pixels.
    fn apply_destructive(&mut self, rect: Rect) {
        // Annotations go into the image first, or a crop would move the capture
        // out from under them.
        self.flatten();
        let image = (*self.image).clone();
        let next = match self.tool {
            Tool::Crop => edits::crop(&image, &rect),
            Tool::Cutout => edits::cutout(&image, &rect),
            Tool::Blur => {
                let mut image = image;
                edits::blur(&mut image, &rect, 16);
                Some(image)
            }
            Tool::Pixelate => {
                let mut image = image;
                edits::pixelate(&mut image, &rect, 12);
                Some(image)
            }
            _ => None,
        };
        match next {
            Some(image) => self.replace_image(image),
            None => self.status = Some("that selection was too small".into()),
        }
    }

    fn toolbar(&mut self, ui: &mut egui::Ui) {
        ui.horizontal_wrapped(|ui| {
            for (tool, label, hint) in Tool::ALL {
                if ui
                    .selectable_label(self.tool == tool, label)
                    .on_hover_text(hint)
                    .clicked()
                {
                    self.tool = tool;
                }
            }
        });

        ui.horizontal_wrapped(|ui| {
            for swatch in SWATCHES {
                let (rect, response) =
                    ui.allocate_exact_size(egui::vec2(18.0, 18.0), egui::Sense::click());
                ui.painter().rect_filled(rect, 3.0, swatch);
                if self.colour == swatch {
                    ui.painter()
                        .rect_stroke(rect, 3.0, egui::Stroke::new(2.0, egui::Color32::GRAY));
                }
                if response.clicked() {
                    self.colour = swatch;
                }
            }
            ui.separator();
            ui.add(egui::Slider::new(&mut self.width, 1.0..=24.0).text("width"));
            if matches!(self.tool, Tool::Text) {
                ui.add(egui::Slider::new(&mut self.font_size, 8.0..=96.0).text("size"));
            }
            ui.checkbox(&mut self.filled, "fill");
            ui.separator();
            if ui.button("Undo").clicked() {
                self.undo();
            }
            if ui.button("Redo").clicked() {
                self.redo();
            }
            ui.separator();
            if ui.button("Save").clicked() {
                self.save();
            }
            if ui.button("Copy").clicked() {
                self.copy();
            }
            if ui.button("Close").clicked() {
                self.done = true;
            }
            if let Some(status) = &self.status {
                ui.separator();
                ui.label(status);
            }
        });
    }

    /// Draw the editor and handle its input. Returns once per frame.
    pub fn ui(&mut self, ctx: &egui::Context) {
        let texture = self.texture(ctx);

        egui::TopBottomPanel::top("tools").show(ctx, |ui| self.toolbar(ui));

        egui::CentralPanel::default().show(ctx, |ui| {
            let available = ui.available_size();
            let (iw, ih) = (self.image.width() as f32, self.image.height() as f32);
            // Fit, never enlarge: a small capture stays its own size.
            let scale = (available.x / iw).min(available.y / ih).min(1.0).max(0.05);
            let size = egui::vec2(iw * scale, ih * scale);

            let (screen, response) = ui.allocate_exact_size(size, egui::Sense::click_and_drag());
            ui.painter().image(
                texture.id(),
                screen,
                egui::Rect::from_min_max(egui::pos2(0.0, 0.0), egui::pos2(1.0, 1.0)),
                egui::Color32::WHITE,
            );

            if response.drag_started() {
                if let Some(at) = response.interact_pointer_pos() {
                    let point = self.point(screen, scale, at);
                    self.drag_from = Some(point);
                    self.pen.clear();
                    self.pen.push(point);
                }
            }
            if response.dragged() {
                if let Some(at) = response.interact_pointer_pos() {
                    let point = self.point(screen, scale, at);
                    self.drag_to = Some(point);
                    if matches!(self.tool, Tool::Pen) {
                        self.pen.push(point);
                    }
                }
            }
            if response.drag_stopped() {
                if let (Some(from), Some(to)) = (self.drag_from, self.drag_to) {
                    let rect = rect_from(from, to);
                    match self.tool {
                        Tool::Rect => self.commit(Shape::Rect { rect, filled: self.filled }),
                        Tool::Ellipse => {
                            self.commit(Shape::Ellipse { rect, filled: self.filled })
                        }
                        Tool::Highlight => self.commit(Shape::Highlight { rect }),
                        Tool::Line => self.commit(Shape::Line { from, to }),
                        Tool::Arrow => self.commit(Shape::Arrow { from, to }),
                        Tool::Pen => {
                            let points = std::mem::take(&mut self.pen);
                            if points.len() > 1 {
                                self.commit(Shape::Pen { points });
                            }
                        }
                        tool if tool.destructive() => self.apply_destructive(rect),
                        _ => {}
                    }
                }
                self.drag_from = None;
                self.drag_to = None;
            }

            // Click-placed tools, which have no drag to wait for.
            if response.clicked() {
                if let Some(at) = response.interact_pointer_pos() {
                    let point = self.point(screen, scale, at);
                    match self.tool {
                        Tool::Step => {
                            let number = self.step;
                            self.step += 1;
                            self.commit(Shape::Step { at: point, number });
                        }
                        Tool::Text => self.typing = Some((point, String::new())),
                        _ => {}
                    }
                }
            }

            let painter = ui.painter_at(screen);
            let to_screen = |p: [f32; 2]| {
                egui::pos2(screen.min.x + p[0] * scale, screen.min.y + p[1] * scale)
            };
            for drawn in &self.shapes {
                render::draw_on_screen(&painter, drawn, scale, &to_screen);
            }
            // The shape being dragged right now, so it can be seen forming.
            if let (Some(from), Some(to)) = (self.drag_from, self.drag_to) {
                let preview = Drawn {
                    shape: match self.tool {
                        Tool::Ellipse => Shape::Ellipse {
                            rect: rect_from(from, to),
                            filled: self.filled,
                        },
                        Tool::Line => Shape::Line { from, to },
                        Tool::Arrow => Shape::Arrow { from, to },
                        Tool::Pen => Shape::Pen { points: self.pen.clone() },
                        Tool::Highlight => Shape::Highlight { rect: rect_from(from, to) },
                        _ => Shape::Rect {
                            rect: rect_from(from, to),
                            filled: self.filled && !self.tool.destructive(),
                        },
                    },
                    colour: self.colour,
                    width: self.width,
                };
                render::draw_on_screen(&painter, &preview, scale, &to_screen);
            }

            // Text is typed straight onto the image, committed with Enter.
            if let Some((at, buffer)) = &mut self.typing {
                let at = *at;
                let mut text = buffer.clone();
                let response = ui.put(
                    egui::Rect::from_min_size(to_screen(at), egui::vec2(220.0, 24.0)),
                    egui::TextEdit::singleline(&mut text).hint_text("text, Enter to place"),
                );
                response.request_focus();
                *buffer = text.clone();
                if response.lost_focus() && ui.input(|i| i.key_pressed(egui::Key::Enter)) {
                    if !text.is_empty() {
                        let size = self.font_size;
                        self.typing = None;
                        self.commit(Shape::Text { at, text, size });
                    } else {
                        self.typing = None;
                    }
                }
            }
        });

        let typing = self.typing.is_some();
        ctx.input(|i| {
            if typing {
                return;
            }
            if i.key_pressed(egui::Key::Escape) {
                self.done = true;
            }
            let command = i.modifiers.command;
            if command && i.key_pressed(egui::Key::Z) {
                if i.modifiers.shift {
                    self.redo();
                } else {
                    self.undo();
                }
            }
        });
        if ctx.input(|i| i.modifiers.command && i.key_pressed(egui::Key::S)) && !typing {
            self.save();
        }
        if ctx.input(|i| i.modifiers.command && i.key_pressed(egui::Key::C)) && !typing {
            self.copy();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn editor(width: u32, height: u32) -> Editor {
        Editor::new(RgbaImage::new(width, height), "test".into())
    }

    #[test]
    fn adding_a_shape_does_not_copy_the_image() {
        let mut e = editor(400, 300);
        let before = e.history_bytes();
        for _ in 0..10 {
            e.commit(Shape::Rect {
                rect: Rect { x: 1, y: 1, width: 10, height: 10 },
                filled: false,
            });
        }
        assert_eq!(
            e.history_bytes(),
            before,
            "shapes are sharing nothing, so history grew by a whole image"
        );
    }

    #[test]
    fn history_stays_within_its_budget() {
        // Each of these replaces the image, so each one allocates.
        let mut e = editor(2048, 2048);
        for _ in 0..20 {
            let next = RgbaImage::new(2048, 2048);
            e.replace_image(next);
        }
        assert!(
            e.history_bytes() <= HISTORY_BYTES,
            "history held {} bytes",
            e.history_bytes()
        );
    }

    #[test]
    fn undo_walks_back_across_a_destructive_edit() {
        let mut e = editor(400, 300);
        e.commit(Shape::Rect {
            rect: Rect { x: 1, y: 1, width: 10, height: 10 },
            filled: false,
        });
        assert_eq!(e.shapes.len(), 1);

        e.tool = Tool::Crop;
        e.apply_destructive(Rect { x: 0, y: 0, width: 200, height: 150 });
        assert_eq!(e.image.dimensions(), (200, 150));
        // The shape was baked into the image, so it is no longer a live shape.
        assert_eq!(e.shapes.len(), 0);

        e.undo();
        assert_eq!(e.image.dimensions(), (400, 300), "the crop did not undo");
        assert_eq!(e.shapes.len(), 1, "the annotation did not come back");
    }
}
