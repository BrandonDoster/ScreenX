// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! Drawing annotations into the image that gets saved.
//!
//! egui draws the editor on screen, but the file has to be produced without
//! reading pixels back off the GPU — that path is slow, and on some drivers it
//! does not work at all. So the shapes are rasterised here a second time.
//!
//! The two must agree. Anything added to `Shape` needs a case in both.

use ab_glyph::{Font, FontRef, PxScale, ScaleFont};
use eframe::egui;
use image::{Rgba, RgbaImage};

use crate::editor::{Drawn, Shape};

/// The font egui already embeds, rather than shipping a second copy of one.
///
/// `FontDefinitions::default()` hands back the bytes epaint compiled in, which
/// are `'static`, so they can be handed straight to ab_glyph and cached.
fn font() -> Option<&'static FontRef<'static>> {
    static FONT: std::sync::OnceLock<Option<FontRef<'static>>> = std::sync::OnceLock::new();
    FONT.get_or_init(|| {
        let definitions = egui::FontDefinitions::default();
        let data = definitions.font_data.get("Ubuntu-Light")?;
        match &data.font {
            std::borrow::Cow::Borrowed(bytes) => FontRef::try_from_slice(bytes).ok(),
            std::borrow::Cow::Owned(_) => None,
        }
    })
    .as_ref()
}

fn rgba(colour: egui::Color32) -> Rgba<u8> {
    let [r, g, b, a] = colour.to_array();
    Rgba([r, g, b, a])
}

/// Alpha-blend one pixel, so a highlighter reads as translucent.
fn blend(image: &mut RgbaImage, x: i32, y: i32, colour: Rgba<u8>) {
    if x < 0 || y < 0 || x >= image.width() as i32 || y >= image.height() as i32 {
        return;
    }
    let alpha = colour.0[3] as u32;
    if alpha == 0 {
        return;
    }
    let under = image.get_pixel(x as u32, y as u32).0;
    let mix = |a: u8, b: u8| (((a as u32 * alpha) + (b as u32 * (255 - alpha))) / 255) as u8;
    image.put_pixel(
        x as u32,
        y as u32,
        Rgba([
            mix(colour.0[0], under[0]),
            mix(colour.0[1], under[1]),
            mix(colour.0[2], under[2]),
            255,
        ]),
    );
}

/// A filled disc, which is how a stroke of a given width gets its thickness.
fn dot(image: &mut RgbaImage, at: [f32; 2], radius: f32, colour: Rgba<u8>) {
    let r = radius.max(0.5);
    let (cx, cy) = (at[0], at[1]);
    for y in (cy - r).floor() as i32..=(cy + r).ceil() as i32 {
        for x in (cx - r).floor() as i32..=(cx + r).ceil() as i32 {
            let dx = x as f32 + 0.5 - cx;
            let dy = y as f32 + 0.5 - cy;
            if dx * dx + dy * dy <= r * r {
                blend(image, x, y, colour);
            }
        }
    }
}

fn line(image: &mut RgbaImage, from: [f32; 2], to: [f32; 2], width: f32, colour: Rgba<u8>) {
    let dx = to[0] - from[0];
    let dy = to[1] - from[1];
    let steps = dx.abs().max(dy.abs()).max(1.0).ceil() as i32;
    for i in 0..=steps {
        let t = i as f32 / steps as f32;
        dot(
            image,
            [from[0] + dx * t, from[1] + dy * t],
            width / 2.0,
            colour,
        );
    }
}

fn rect_outline(image: &mut RgbaImage, r: &screenx_core::capture::Rect, width: f32, colour: Rgba<u8>) {
    let (x0, y0) = (r.x as f32, r.y as f32);
    let (x1, y1) = (x0 + r.width as f32, y0 + r.height as f32);
    line(image, [x0, y0], [x1, y0], width, colour);
    line(image, [x1, y0], [x1, y1], width, colour);
    line(image, [x1, y1], [x0, y1], width, colour);
    line(image, [x0, y1], [x0, y0], width, colour);
}

fn rect_filled(image: &mut RgbaImage, r: &screenx_core::capture::Rect, colour: Rgba<u8>) {
    for y in r.y..r.y + r.height as i32 {
        for x in r.x..r.x + r.width as i32 {
            blend(image, x, y, colour);
        }
    }
}

fn ellipse(
    image: &mut RgbaImage,
    r: &screenx_core::capture::Rect,
    width: f32,
    colour: Rgba<u8>,
    filled: bool,
) {
    let rx = r.width as f32 / 2.0;
    let ry = r.height as f32 / 2.0;
    if rx <= 0.0 || ry <= 0.0 {
        return;
    }
    let cx = r.x as f32 + rx;
    let cy = r.y as f32 + ry;

    if filled {
        for y in r.y..r.y + r.height as i32 {
            for x in r.x..r.x + r.width as i32 {
                let nx = (x as f32 + 0.5 - cx) / rx;
                let ny = (y as f32 + 0.5 - cy) / ry;
                if nx * nx + ny * ny <= 1.0 {
                    blend(image, x, y, colour);
                }
            }
        }
        return;
    }

    let steps = ((rx + ry) * 4.0).max(32.0) as i32;
    let mut previous = None;
    for i in 0..=steps {
        let angle = i as f32 / steps as f32 * std::f32::consts::TAU;
        let point = [cx + rx * angle.cos(), cy + ry * angle.sin()];
        if let Some(previous) = previous {
            line(image, previous, point, width, colour);
        }
        previous = Some(point);
    }
}

/// A line with a head, sized from the stroke so it stays in proportion.
fn arrow(image: &mut RgbaImage, from: [f32; 2], to: [f32; 2], width: f32, colour: Rgba<u8>) {
    line(image, from, to, width, colour);
    let dx = to[0] - from[0];
    let dy = to[1] - from[1];
    let length = (dx * dx + dy * dy).sqrt();
    if length < 1.0 {
        return;
    }
    let head = (width * 4.0).min(length);
    let angle = dy.atan2(dx);
    for side in [0.5, -0.5] {
        let a = angle + std::f32::consts::PI + side;
        line(
            image,
            to,
            [to[0] + head * a.cos(), to[1] + head * a.sin()],
            width,
            colour,
        );
    }
}

fn text(image: &mut RgbaImage, at: [f32; 2], string: &str, size: f32, colour: Rgba<u8>) {
    let Some(font) = font() else { return };
    let scaled = font.as_scaled(PxScale::from(size));
    let mut caret = at[0];
    // `at` is the top-left of the run, so the baseline sits an ascent below it.
    let baseline = at[1] + scaled.ascent();

    for character in string.chars() {
        let glyph_id = font.glyph_id(character);
        let glyph = glyph_id.with_scale_and_position(size, ab_glyph::point(caret, baseline));
        if let Some(outline) = font.outline_glyph(glyph) {
            let bounds = outline.px_bounds();
            outline.draw(|gx, gy, coverage| {
                if coverage <= 0.0 {
                    return;
                }
                let mut shade = colour;
                shade.0[3] = (colour.0[3] as f32 * coverage) as u8;
                blend(
                    image,
                    bounds.min.x as i32 + gx as i32,
                    bounds.min.y as i32 + gy as i32,
                    shade,
                );
            });
        }
        caret += scaled.h_advance(glyph_id);
    }
}

/// Roughly how wide a run of text will be, for placing the step number's disc.
pub fn text_width(string: &str, size: f32) -> f32 {
    let Some(font) = font() else {
        return string.len() as f32 * size * 0.5;
    };
    let scaled = font.as_scaled(PxScale::from(size));
    string
        .chars()
        .map(|c| scaled.h_advance(font.glyph_id(c)))
        .sum()
}

pub fn draw_shapes_into(image: &mut RgbaImage, shapes: &[Drawn]) {
    for drawn in shapes {
        let colour = rgba(drawn.colour);
        match &drawn.shape {
            Shape::Rect { rect, filled } => {
                if *filled {
                    rect_filled(image, rect, colour);
                } else {
                    rect_outline(image, rect, drawn.width, colour);
                }
            }
            Shape::Ellipse { rect, filled } => {
                ellipse(image, rect, drawn.width, colour, *filled)
            }
            Shape::Line { from, to } => line(image, *from, *to, drawn.width, colour),
            Shape::Arrow { from, to } => arrow(image, *from, *to, drawn.width, colour),
            Shape::Pen { points } => {
                for pair in points.windows(2) {
                    line(image, pair[0], pair[1], drawn.width, colour);
                }
            }
            // Translucent, so what is underneath still reads through it.
            Shape::Highlight { rect } => {
                let mut wash = colour;
                wash.0[3] = 90;
                rect_filled(image, rect, wash);
            }
            Shape::Text { at, text: string, size } => {
                text(image, *at, string, *size, colour)
            }
            Shape::Step { at, number } => {
                let label = number.to_string();
                let size = drawn.width * 6.0;
                let radius = size * 0.75;
                dot(image, *at, radius, colour);
                let width = text_width(&label, size);
                text(
                    image,
                    [at[0] - width / 2.0, at[1] - size * 0.62],
                    &label,
                    size,
                    Rgba([255, 255, 255, 255]),
                );
            }
        }
    }
}

/// The same shapes, drawn with egui for the screen.
///
/// This and `draw_shapes_into` must agree: one is what the user sees, the other
/// is what gets written to the file. A shape added to one and not the other
/// looks like the editor lying about the result.
pub fn draw_on_screen(
    painter: &egui::Painter,
    drawn: &Drawn,
    scale: f32,
    to_screen: &dyn Fn([f32; 2]) -> egui::Pos2,
) {
    let colour = drawn.colour;
    let width = drawn.width * scale;
    let stroke = egui::Stroke::new(width.max(1.0), colour);
    let corners = |r: &screenx_core::capture::Rect| {
        egui::Rect::from_min_max(
            to_screen([r.x as f32, r.y as f32]),
            to_screen([
                r.x as f32 + r.width as f32,
                r.y as f32 + r.height as f32,
            ]),
        )
    };

    match &drawn.shape {
        Shape::Rect { rect, filled } => {
            if *filled {
                painter.rect_filled(corners(rect), 0.0, colour);
            } else {
                painter.rect_stroke(corners(rect), 0.0, stroke);
            }
        }
        Shape::Ellipse { rect, filled } => {
            let bounds = corners(rect);
            let centre = bounds.center();
            let radius = bounds.size() / 2.0;
            // egui has no ellipse primitive, so it is a closed polyline —
            // the same approximation the rasteriser uses.
            let points: Vec<egui::Pos2> = (0..=64)
                .map(|i| {
                    let angle = i as f32 / 64.0 * std::f32::consts::TAU;
                    egui::pos2(
                        centre.x + radius.x * angle.cos(),
                        centre.y + radius.y * angle.sin(),
                    )
                })
                .collect();
            if *filled {
                painter.add(egui::Shape::convex_polygon(points, colour, egui::Stroke::NONE));
            } else {
                painter.add(egui::Shape::line(points, stroke));
            }
        }
        Shape::Line { from, to } => {
            painter.line_segment([to_screen(*from), to_screen(*to)], stroke);
        }
        Shape::Arrow { from, to } => {
            let (a, b) = (to_screen(*from), to_screen(*to));
            painter.line_segment([a, b], stroke);
            let delta = b - a;
            let length = delta.length();
            if length >= 1.0 {
                let head = (width * 4.0).min(length);
                let angle = delta.y.atan2(delta.x);
                for side in [0.5f32, -0.5] {
                    let a2 = angle + std::f32::consts::PI + side;
                    painter.line_segment(
                        [b, b + egui::vec2(head * a2.cos(), head * a2.sin())],
                        stroke,
                    );
                }
            }
        }
        Shape::Pen { points } => {
            painter.add(egui::Shape::line(
                points.iter().map(|p| to_screen(*p)).collect(),
                stroke,
            ));
        }
        Shape::Highlight { rect } => {
            painter.rect_filled(corners(rect), 0.0, colour.gamma_multiply(0.35));
        }
        Shape::Text { at, text, size } => {
            painter.text(
                to_screen(*at),
                egui::Align2::LEFT_TOP,
                text,
                egui::FontId::proportional(size * scale),
                colour,
            );
        }
        Shape::Step { at, number } => {
            let size = drawn.width * 6.0 * scale;
            painter.circle_filled(to_screen(*at), size * 0.75, colour);
            painter.text(
                to_screen(*at),
                egui::Align2::CENTER_CENTER,
                number.to_string(),
                egui::FontId::proportional(size),
                egui::Color32::WHITE,
            );
        }
    }
}
