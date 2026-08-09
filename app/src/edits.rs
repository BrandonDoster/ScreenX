// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Brandon Doster

//! The edits that rewrite pixels rather than draw on top of them.
//!
//! Blur and pixelate are redaction tools: they must destroy what was there, not
//! soften it. The webview build had to hand-roll blur because WKWebView does not
//! implement `ctx.filter` and silently pretended to. Here it is just arithmetic.

use image::RgbaImage;
use screenx_core::capture::Rect;

/// Clamp a rectangle to the image and return it in pixel coordinates.
fn clamped(image: &RgbaImage, rect: &Rect) -> Option<(u32, u32, u32, u32)> {
    let x = rect.x.max(0) as u32;
    let y = rect.y.max(0) as u32;
    let right = (rect.x + rect.width as i32).clamp(0, image.width() as i32) as u32;
    let bottom = (rect.y + rect.height as i32).clamp(0, image.height() as i32) as u32;
    if right <= x || bottom <= y {
        return None;
    }
    Some((x, y, right - x, bottom - y))
}

/// Average each block down to one colour. Nothing survives it.
pub fn pixelate(image: &mut RgbaImage, rect: &Rect, block: u32) {
    let Some((x0, y0, width, height)) = clamped(image, rect) else {
        return;
    };
    let block = block.max(2);
    for by in (0..height).step_by(block as usize) {
        for bx in (0..width).step_by(block as usize) {
            let w = block.min(width - bx);
            let h = block.min(height - by);
            let (mut r, mut g, mut b, mut count) = (0u32, 0u32, 0u32, 0u32);
            for y in 0..h {
                for x in 0..w {
                    let p = image.get_pixel(x0 + bx + x, y0 + by + y).0;
                    r += p[0] as u32;
                    g += p[1] as u32;
                    b += p[2] as u32;
                    count += 1;
                }
            }
            let average = image::Rgba([
                (r / count) as u8,
                (g / count) as u8,
                (b / count) as u8,
                255,
            ]);
            for y in 0..h {
                for x in 0..w {
                    image.put_pixel(x0 + bx + x, y0 + by + y, average);
                }
            }
        }
    }
}

/// Shrink the region hard, then stretch it back.
///
/// Throwing the pixels away and interpolating what is left is what makes this a
/// redaction rather than a blur that can be sharpened back out.
pub fn blur(image: &mut RgbaImage, rect: &Rect, strength: u32) {
    let Some((x0, y0, width, height)) = clamped(image, rect) else {
        return;
    };
    let factor = strength.clamp(2, 64);
    let small_w = (width / factor).max(1);
    let small_h = (height / factor).max(1);

    let region = image::imageops::crop_imm(image, x0, y0, width, height).to_image();
    let small = image::imageops::thumbnail(&region, small_w, small_h);
    let back = image::imageops::resize(
        &small,
        width,
        height,
        image::imageops::FilterType::Triangle,
    );
    image::imageops::replace(image, &back, x0 as i64, y0 as i64);
}

/// Keep only the selected rectangle.
pub fn crop(image: &RgbaImage, rect: &Rect) -> Option<RgbaImage> {
    let (x, y, width, height) = clamped(image, rect)?;
    if width < 2 || height < 2 {
        return None;
    }
    Some(image::imageops::crop_imm(image, x, y, width, height).to_image())
}

/// Remove a band and close the gap.
///
/// The drag decides the direction: wider than tall takes out columns, taller
/// than wide takes out rows. Getting that backwards was a real bug once.
pub fn cutout(image: &RgbaImage, rect: &Rect) -> Option<RgbaImage> {
    let (x, y, width, height) = clamped(image, rect)?;
    let vertical = width >= height;
    let (limit, from, size) = if vertical {
        (image.width(), x, width)
    } else {
        (image.height(), y, height)
    };
    // Refuse to consume the whole image.
    if size < 2 || limit.saturating_sub(size) < 2 {
        return None;
    }

    let mut out = if vertical {
        RgbaImage::new(image.width() - size, image.height())
    } else {
        RgbaImage::new(image.width(), image.height() - size)
    };

    if vertical {
        let left = image::imageops::crop_imm(image, 0, 0, from, image.height()).to_image();
        image::imageops::replace(&mut out, &left, 0, 0);
        let rest = image.width() - (from + size);
        if rest > 0 {
            let right =
                image::imageops::crop_imm(image, from + size, 0, rest, image.height()).to_image();
            image::imageops::replace(&mut out, &right, from as i64, 0);
        }
    } else {
        let top = image::imageops::crop_imm(image, 0, 0, image.width(), from).to_image();
        image::imageops::replace(&mut out, &top, 0, 0);
        let rest = image.height() - (from + size);
        if rest > 0 {
            let bottom =
                image::imageops::crop_imm(image, 0, from + size, image.width(), rest).to_image();
            image::imageops::replace(&mut out, &bottom, 0, from as i64);
        }
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn image(width: u32, height: u32) -> RgbaImage {
        RgbaImage::from_fn(width, height, |x, y| {
            image::Rgba([(x % 256) as u8, (y % 256) as u8, 128, 255])
        })
    }

    fn rect(x: i32, y: i32, width: u32, height: u32) -> Rect {
        Rect { x, y, width, height }
    }

    #[test]
    fn cropping_keeps_the_rectangle() {
        let cropped = crop(&image(400, 300), &rect(50, 40, 200, 160)).unwrap();
        assert_eq!(cropped.dimensions(), (200, 160));
    }

    #[test]
    fn a_wide_cutout_removes_columns() {
        let out = cutout(&image(400, 300), &rect(100, 140, 100, 20)).unwrap();
        assert_eq!(out.dimensions(), (300, 300), "it cut the wrong way");
    }

    #[test]
    fn a_tall_cutout_removes_rows() {
        let out = cutout(&image(400, 300), &rect(100, 50, 20, 200)).unwrap();
        assert_eq!(out.dimensions(), (400, 100), "it cut the wrong way");
    }

    #[test]
    fn a_cutout_that_would_consume_the_image_is_refused() {
        assert!(cutout(&image(400, 300), &rect(0, 140, 400, 20)).is_none());
    }

    #[test]
    fn a_drag_past_the_edge_stops_at_the_edge() {
        // 20px inside the left edge, dragged 20px past it. Clamping only where
        // the band starts would add the overshoot back on the inside and take
        // 40 columns instead of 20.
        let out = cutout(&image(400, 300), &rect(-20, 140, 40, 20)).unwrap();
        assert_eq!(out.dimensions(), (380, 300));
    }

    /// The widest gap between neighbouring pixels along a row.
    fn contrast(image: &RgbaImage, y: u32, xs: std::ops::Range<u32>) -> i32 {
        xs.clone()
            .zip(xs.skip(1))
            .map(|(a, b)| {
                (image.get_pixel(a, y).0[0] as i32 - image.get_pixel(b, y).0[0] as i32).abs()
            })
            .max()
            .unwrap_or(0)
    }

    #[test]
    fn blur_destroys_the_detail_under_it() {
        // A checkerboard, because a gradient stays a gradient under any blur
        // and would prove nothing. This is the high-frequency detail that a
        // redaction has to remove — text is the real case.
        let mut img = RgbaImage::from_fn(400, 300, |x, y| {
            let on = (x + y) % 2 == 0;
            image::Rgba([if on { 255 } else { 0 }, 0, 0, 255])
        });
        assert_eq!(contrast(&img, 60, 50..70), 255, "the fixture is not sharp");

        blur(&mut img, &rect(40, 40, 80, 80), 16);
        assert!(
            contrast(&img, 60, 50..70) < 16,
            "detail survived the blur, so it would not redact"
        );
    }

    #[test]
    fn pixelate_flattens_each_block() {
        let mut img = image(400, 300);
        pixelate(&mut img, &rect(40, 40, 80, 80), 8);
        assert_eq!(*img.get_pixel(41, 41), *img.get_pixel(46, 46));
    }

    #[test]
    fn an_edit_outside_the_image_is_ignored() {
        let mut img = image(400, 300);
        let before = img.clone();
        blur(&mut img, &rect(500, 500, 40, 40), 8);
        pixelate(&mut img, &rect(-80, -80, 40, 40), 8);
        assert_eq!(img, before);
    }
}
