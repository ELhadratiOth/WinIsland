//! Album-art colours: the vivid accent of a cover, and the One UI 9 media pill gradient from it.

/// The accent is never darker than this HSL lightness, so it reads on the dark material.
const MIN_LIGHTNESS: f32 = 0.45;

/// Vivid colour of a BGRA image: a mean weighted by saturation² × value over a 32×32 sample,
/// ignoring near-black pixels; plain mean if nothing is vivid; then lifted to readable lightness.
pub fn accent_from_rgba(px: &[u8], w: usize, h: usize) -> [u8; 3] {
    if w == 0 || h == 0 || px.len() < w * h * 4 {
        return lift([0.5, 0.5, 0.5]);
    }
    let step_x = (w / 32).max(1);
    let step_y = (h / 32).max(1);
    let (mut weighted, mut total) = ([0.0f32; 3], 0.0f32);
    let (mut plain, mut count) = ([0.0f32; 3], 0.0f32);
    for y in (0..h).step_by(step_y) {
        for x in (0..w).step_by(step_x) {
            let i = (y * w + x) * 4;
            let rgb = [px[i + 2] as f32 / 255.0, px[i + 1] as f32 / 255.0, px[i] as f32 / 255.0];
            for c in 0..3 {
                plain[c] += rgb[c];
            }
            count += 1.0;
            let max = rgb.iter().cloned().fold(0.0, f32::max);
            let min = rgb.iter().cloned().fold(1.0, f32::min);
            if max < 0.15 {
                continue;
            }
            let sat = (max - min) / max;
            let weight = sat * sat * max;
            for c in 0..3 {
                weighted[c] += rgb[c] * weight;
            }
            total += weight;
        }
    }
    let mean = if total > 1e-4 {
        weighted.map(|v| v / total)
    } else {
        plain.map(|v| v / count.max(1.0))
    };
    lift(mean)
}

/// Raises HSL lightness to at least `MIN_LIGHTNESS`, keeping hue and saturation.
fn lift(rgb: [f32; 3]) -> [u8; 3] {
    let max = rgb.iter().cloned().fold(0.0, f32::max);
    let min = rgb.iter().cloned().fold(1.0, f32::min);
    let l = (max + min) / 2.0;
    let out = if l >= MIN_LIGHTNESS {
        rgb
    } else {
        let d = max - min;
        let s = if d == 0.0 { 0.0 } else { d / (1.0 - (2.0 * l - 1.0).abs()) };
        hsl_to_rgb(hue_sextant(rgb), s, MIN_LIGHTNESS)
    };
    out.map(|v| (v.clamp(0.0, 1.0) * 255.0).round() as u8)
}

/// The hue of an RGB colour (0–1 channels) in sextants [0, 6); 0 for a grey.
fn hue_sextant(rgb: [f32; 3]) -> f32 {
    let max = rgb.iter().cloned().fold(0.0, f32::max);
    let min = rgb.iter().cloned().fold(1.0, f32::min);
    let d = max - min;
    if d == 0.0 {
        0.0
    } else if max == rgb[0] {
        ((rgb[1] - rgb[2]) / d).rem_euclid(6.0)
    } else if max == rgb[1] {
        (rgb[2] - rgb[0]) / d + 2.0
    } else {
        (rgb[0] - rgb[1]) / d + 4.0
    }
}

/// The wallpaper accent's tone: light enough to read on the dark pill, dark enough for the white
/// icons on it, and never washed out (One UI's palette is a tone of the wallpaper's hue).
const WALLPAPER_L: (f32, f32) = (0.55, 0.62);
const WALLPAPER_MIN_S: f32 = 0.45;
/// Below this HSL saturation a colour is a grey: the chosen accent stays.
const GREY_S: f32 = 0.12;

/// `rgb` (0–1 channels) at the wallpaper accent's tone; `None` for a grey.
fn accent_tone(rgb: [f32; 3]) -> Option<[u8; 3]> {
    let max = rgb.iter().cloned().fold(0.0, f32::max);
    let min = rgb.iter().cloned().fold(1.0, f32::min);
    let l = (max + min) / 2.0;
    let d = max - min;
    let s = if d == 0.0 || l <= 0.0 || l >= 1.0 { 0.0 } else { d / (1.0 - (2.0 * l - 1.0).abs()) };
    if s < GREY_S || max < 0.06 {
        return None;
    }
    let out = hsl_to_rgb(hue_sextant(rgb), s.clamp(WALLPAPER_MIN_S, 0.9), l.clamp(WALLPAPER_L.0, WALLPAPER_L.1));
    Some(out.map(|v| (v.clamp(0.0, 1.0) * 255.0).round() as u8))
}

/// A solid colour background as the accent (`None` for a grey one).
pub fn solid_accent(c: [u8; 3]) -> Option<[u8; 3]> {
    accent_tone(c.map(|v| v as f32 / 255.0))
}

/// The wallpaper's own colour from a premultiplied BGRA picture: the hue that covers the most
/// with the most colour (36 buckets of 10°, each scored with its neighbours, weighted by chroma),
/// that hue's mean, at the accent's tone. A mean of every pixel would blend a blue sea and an
/// orange sunset into grey. `None` when the picture has too little colour (the chosen accent
/// stays); transparent pixels (a letterbox) don't count.
pub fn wallpaper_accent(px: &[u8], w: usize, h: usize) -> Option<[u8; 3]> {
    if w == 0 || h == 0 || px.len() < w * h * 4 {
        return None;
    }
    let (mut weight, mut sums, mut seen) = ([0.0f32; 36], [[0.0f32; 3]; 36], 0.0f32);
    for p in px[..w * h * 4].chunks_exact(4) {
        if p[3] == 0 {
            continue;
        }
        seen += 1.0;
        let a = p[3] as f32;
        let rgb = [p[2] as f32 / a, p[1] as f32 / a, p[0] as f32 / a].map(|v| v.min(1.0));
        let max = rgb.iter().cloned().fold(0.0, f32::max);
        let chroma = max - rgb.iter().cloned().fold(1.0, f32::min);
        // Near-black and near-grey pixels have no hue worth keeping.
        if chroma < 0.08 || max < 0.12 {
            continue;
        }
        let bucket = (hue_sextant(rgb) * 6.0) as usize % 36;
        weight[bucket] += chroma;
        for c in 0..3 {
            sums[bucket][c] += rgb[c] * chroma;
        }
    }
    let around = |b: usize| [(b + 35) % 36, b, (b + 1) % 36];
    let score = |b: usize| around(b).iter().map(|&i| weight[i]).sum::<f32>();
    let best = (0..36).max_by(|&a, &b| score(a).total_cmp(&score(b)))?;
    // A colour on a few percent of the picture isn't the picture's colour.
    if score(best) < 0.04 * seen {
        return None;
    }
    let mut mean = [0.0f32; 3];
    for i in around(best) {
        for c in 0..3 {
            mean[c] += sums[i][c];
        }
    }
    accent_tone(mean.map(|v| v / score(best)))
}

/// `hue` in sextants [0, 6).
fn hsl_to_rgb(hue: f32, s: f32, l: f32) -> [f32; 3] {
    let c = (1.0 - (2.0 * l - 1.0).abs()) * s;
    let x = c * (1.0 - (hue.rem_euclid(2.0) - 1.0).abs());
    let (r, g, b) = match hue as u32 {
        0 => (c, x, 0.0),
        1 => (x, c, 0.0),
        2 => (0.0, c, x),
        3 => (0.0, x, c),
        4 => (x, 0.0, c),
        _ => (c, 0.0, x),
    };
    let m = l - c / 2.0;
    [r + m, g + m, b + m]
}

/// A team's colours for the live-match activity (sports notes §5.2).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TeamPalette {
    /// Accents, glows, slivers: vivid and readable on the dark material.
    pub tint: [u8; 3],
    /// The goal moment's background, white text on it.
    pub flood: [u8; 3],
    /// "GOL!" and the fresh score digit.
    pub headline: [u8; 3],
}

fn hsl(c: [u8; 3]) -> (f32, f32, f32) {
    let rgb = c.map(|v| v as f32 / 255.0);
    let max = rgb.iter().cloned().fold(0.0, f32::max);
    let min = rgb.iter().cloned().fold(1.0, f32::min);
    let l = (max + min) / 2.0;
    let d = max - min;
    let s = if d == 0.0 { 0.0 } else { d / (1.0 - (2.0 * l - 1.0).abs()) };
    (0.0, s, l)
}

/// WCAG relative luminance.
fn luminance(c: [u8; 3]) -> f32 {
    let lin = |v: u8| {
        let v = v as f32 / 255.0;
        if v <= 0.04045 { v / 12.92 } else { ((v + 0.055) / 1.055).powf(2.4) }
    };
    0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2])
}

fn contrast(a: [u8; 3], b: [u8; 3]) -> f32 {
    let (la, lb) = (luminance(a), luminance(b));
    (la.max(lb) + 0.05) / (la.min(lb) + 0.05)
}

/// Tint: the first colour with some saturation, lifted to HSL L ≥ 0.45 (grey for black-and-white
/// clubs). Flood: the first colour dark enough for white text. Headline: the other colour if it
/// stands out on the flood, else white.
pub fn team_palette(primary: [u8; 3], secondary: [u8; 3]) -> TeamPalette {
    let vivid = [primary, secondary].into_iter().find(|c| hsl(*c).1 >= 0.25);
    let tint = vivid.map_or([0x8A, 0x8A, 0x8A], |c| lift(c.map(|v| v as f32 / 255.0)));
    let white = [0xFF, 0xFF, 0xFF];
    let flood_pick = [primary, secondary].into_iter().find(|c| hsl(*c).2 >= 0.12 && contrast(*c, white) >= 4.5);
    let flood = flood_pick.unwrap_or(tint.map(|v| v / 2));
    let other = match flood_pick {
        Some(f) if f == primary => Some(secondary),
        Some(_) => Some(primary),
        None => None,
    };
    let headline = other.filter(|o| contrast(*o, flood) >= 3.0).unwrap_or(white);
    TeamPalette { tint, flood, headline }
}

/// One UI 9 media pill: a vertical gradient from a dark version of the accent to a darker one.
pub fn media_gradient(accent: [u8; 3]) -> ([u8; 3], [u8; 3]) {
    let scale = |k: f32| accent.map(|c| (c as f32 * k).round() as u8);
    (scale(0.32), scale(0.13))
}

#[cfg(test)]
mod tests {
    fn hex(v: u32) -> [u8; 3] {
        [(v >> 16) as u8, (v >> 8) as u8, v as u8]
    }

    fn lightness(c: [u8; 3]) -> f32 {
        let max = *c.iter().max().unwrap() as f32 / 255.0;
        let min = *c.iter().min().unwrap() as f32 / 255.0;
        (max + min) / 2.0
    }

    /// The design's examples (sports notes §5.2).
    #[test]
    fn team_palettes() {
        let fb = team_palette(hex(0x0B2A6F), hex(0xFFE500));
        assert_eq!((fb.flood, fb.headline), (hex(0x0B2A6F), hex(0xFFE500)));
        assert!(lightness(fb.tint) >= 0.44 && fb.tint[2] > fb.tint[0], "navy lifted, still blue: {:?}", fb.tint);
        let gs = team_palette(hex(0xA90432), hex(0xFDB912));
        assert_eq!((gs.flood, gs.headline), (hex(0xA90432), hex(0xFDB912)));
        let bjk = team_palette(hex(0x000000), hex(0xFFFFFF));
        assert_eq!((bjk.tint, bjk.flood, bjk.headline), (hex(0x8A8A8A), hex(0x454545), hex(0xFFFFFF)));
        let ts = team_palette(hex(0x8E003B), hex(0x5699EB));
        assert_eq!((ts.flood, ts.headline), (hex(0x8E003B), hex(0x5699EB)));
    }

    use super::*;

    fn image(pixels: &[(u8, u8, u8)], w: usize, h: usize) -> Vec<u8> {
        (0..w * h).flat_map(|i| {
            let (r, g, b) = pixels[i % pixels.len()];
            [b, g, r, 255]
        }).collect()
    }

    #[test]
    fn accent_of_solid_red() {
        let [r, g, b] = accent_from_rgba(&image(&[(255, 0, 0)], 8, 8), 8, 8);
        assert!(r >= 245 && g <= 10 && b <= 10, "{r},{g},{b}");
    }

    #[test]
    fn accent_ignores_grey_background() {
        // Three grey pixels for every blue one.
        let px = image(&[(128, 128, 128), (128, 128, 128), (128, 128, 128), (20, 40, 220)], 16, 16);
        let [r, _, b] = accent_from_rgba(&px, 16, 16);
        assert!(b as i32 > r as i32 + 60, "r {r} b {b}");
    }

    #[test]
    fn accent_of_black_is_grey_lifted() {
        let [r, g, b] = accent_from_rgba(&image(&[(0, 0, 0)], 4, 4), 4, 4);
        assert!(r >= 110 && g >= 110 && b >= 110, "{r},{g},{b}");
    }

    #[test]
    fn media_gradient_warm() {
        assert_eq!(media_gradient([240, 218, 162]), ([77, 70, 52], [31, 28, 21]));
    }

    /// (hue in degrees, saturation, lightness).
    fn hsl_deg(c: [u8; 3]) -> (f32, f32, f32) {
        let rgb = c.map(|v| v as f32 / 255.0);
        (hue_sextant(rgb) * 60.0, hsl(c).1, lightness(c))
    }

    #[test]
    fn wallpaper_accent_is_the_main_hue_not_a_blend() {
        // A blue sea with an orange sunset band: a mean of the two would be grey or pink.
        let px = image(&[(20, 90, 200), (20, 90, 200), (25, 100, 210), (240, 130, 40)], 32, 32);
        let (h, s, l) = hsl_deg(wallpaper_accent(&px, 32, 32).expect("a colourful wallpaper"));
        assert!((200.0..=230.0).contains(&h), "blue: {h}");
        assert!(s >= 0.45 && (0.54..=0.63).contains(&l), "readable on the pill: s {s} l {l}");
    }

    #[test]
    fn wallpaper_accent_is_put_at_a_readable_tone() {
        // A dark navy: lifted, still blue.
        let (h, _, l) = hsl_deg(wallpaper_accent(&image(&[(10, 20, 60)], 8, 8), 8, 8).unwrap());
        assert!((215.0..=235.0).contains(&h) && l >= 0.54, "h {h} l {l}");
        // A pale pink: darker and fuller, so white icons read on it.
        let (h, s, l) = hsl_deg(wallpaper_accent(&image(&[(250, 215, 225)], 8, 8), 8, 8).unwrap());
        assert!(!(30.0..=300.0).contains(&h), "pink: {h}");
        assert!(s >= 0.45 && l <= 0.63, "s {s} l {l}");
    }

    #[test]
    fn a_grey_wallpaper_has_no_accent() {
        assert_eq!(wallpaper_accent(&image(&[(128, 128, 128), (30, 30, 30), (240, 240, 240)], 16, 16), 16, 16), None);
        // A dash of colour in a grey picture isn't its colour either.
        let mut px = image(&[(128, 128, 128)], 32, 32);
        px[0..4].copy_from_slice(&[0, 0, 255, 255]);
        assert_eq!(wallpaper_accent(&px, 32, 32), None);
        assert_eq!(wallpaper_accent(&[], 0, 0), None);
    }

    #[test]
    fn transparent_pixels_dont_count() {
        // The letterbox around a non-square picture, decoded into a square: alpha 0.
        let mut px = image(&[(0, 160, 90)], 16, 16);
        for row in 0..4 {
            for p in px[row * 64..row * 64 + 64].chunks_mut(4) {
                p.copy_from_slice(&[0, 0, 0, 0]);
            }
        }
        let (h, ..) = hsl_deg(wallpaper_accent(&px, 16, 16).unwrap());
        assert!((140.0..=160.0).contains(&h), "green: {h}");
    }

    #[test]
    fn a_solid_background_is_its_own_accent() {
        let (h, s, l) = hsl_deg(solid_accent([0xD0, 0x40, 0x40]).unwrap());
        assert!(!(5.0..=355.0).contains(&h) && s >= 0.45 && (0.54..=0.63).contains(&l), "h {h} s {s} l {l}");
        assert_eq!(solid_accent([0, 0, 0]), None);
        assert_eq!(solid_accent([0x80, 0x80, 0x84]), None);
    }
}
