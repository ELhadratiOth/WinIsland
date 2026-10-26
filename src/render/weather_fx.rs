//! The weather's moving background for the pill and the card: falling snow, slanting rain, a
//! storm's flashes, the sun's slow glow, a clear night's stars, drifting clouds and fog. All of
//! it is composition animation, run by the compositor: no repaints, no CPU while it plays.

use std::f32::consts::TAU;

use windows::core::{h, Interface, Result};
use windows::Foundation::TimeSpan;
use windows::UI::Color;
use windows::UI::Composition::{
    AnimationDirection, AnimationIterationBehavior, CompositionBrush, CompositionEasingFunction, CompositionRadialGradientBrush,
    CompositionSpriteShape, Compositor, ContainerVisual, ShapeVisual,
};
use windows_numerics::Vector2;

use crate::activities::weather::WeatherFx;

fn ticks(ms: f32) -> TimeSpan {
    TimeSpan { Duration: (ms * 10_000.0) as i64 }
}

fn rgba(r: u8, g: u8, b: u8, a: u8) -> Color {
    Color { A: a, R: r, G: g, B: b }
}

/// The same scene every time for the same size (no jumping when the pill repaints).
struct Rng(u32);

impl Rng {
    fn next(&mut self) -> f32 {
        self.0 = self.0.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
        (self.0 >> 8) as f32 / 16_777_216.0
    }

    fn range(&mut self, a: f32, b: f32) -> f32 {
        a + (b - a) * self.next()
    }
}

/// The scene for `fx` in a `w`×`h` px area (`k`: px per DIP), to put under the content; the
/// sun's and the moon's light centres on `glow_at` (fractions of the area), where the icon is.
pub fn build(c: &Compositor, fx: WeatherFx, w: f32, h: f32, k: f32, glow_at: (f32, f32)) -> Result<ContainerVisual> {
    let root = c.CreateContainerVisual()?;
    root.SetSize(Vector2 { X: w, Y: h })?;
    let shapes = c.CreateShapeVisual()?;
    shapes.SetSize(Vector2 { X: w, Y: h })?;
    let linear: CompositionEasingFunction = c.CreateLinearEasingFunction()?.cast()?;
    let mut rng = Rng(0x9E37_79B9 ^ (w as u32).wrapping_mul(31) ^ fx as u32);
    let area = Area { w, h, k };
    match fx {
        WeatherFx::Snow => snow(c, &shapes, &linear, &mut rng, area)?,
        WeatherFx::Rain => rain(c, &shapes, &linear, &mut rng, area, 1.0)?,
        WeatherFx::Storm => {
            rain(c, &shapes, &linear, &mut rng, area, 1.3)?;
            flash(c, &root, w, h)?;
        }
        // Warm white at the sun, then the sky brightening around it: gold over the blue mixes
        // to grey.
        WeatherFx::Sunny => {
            let light = gradient(c, &[(0.0, rgba(255, 244, 214, 150)), (0.3, rgba(205, 230, 255, 80)), (1.0, rgba(205, 230, 255, 0))])?;
            glow(c, &root, area, &light, glow_at, 3600.0)?
        }
        WeatherFx::ClearNight => {
            glow(c, &root, area, &soft(c, rgba(150, 170, 255, 70), 0.45)?, glow_at, 5200.0)?;
            stars(c, &shapes, &mut rng, area)?;
        }
        WeatherFx::Clouds => clouds(c, &shapes, &linear, &mut rng, area)?,
        WeatherFx::Fog => fog(c, &shapes, &linear, &mut rng, area)?,
    }
    root.Children()?.InsertAtBottom(&shapes)?;
    Ok(root)
}

/// The scene's size in px and px per DIP.
#[derive(Clone, Copy)]
struct Area {
    w: f32,
    h: f32,
    k: f32,
}

impl Area {
    /// In DIP², so the pill and the card are equally busy.
    fn dips(&self) -> f32 {
        self.w * self.h / (self.k * self.k)
    }
}

/// The keyframes of the loop `path` (progress 0 to 1, position) begun at `phase` instead of 0:
/// the same loop already under way, so a scene is full from its first frame.
fn started_at(path: &[(f32, Vector2)], phase: f32) -> Vec<(f32, Vector2)> {
    // Too close to the start for the jump to fit before 1.
    if phase < 1e-3 {
        return path.to_vec();
    }
    let at = |t: f32| {
        let i = path.iter().rposition(|p| p.0 <= t).unwrap_or(0);
        let (a, b) = (path[i], path[(i + 1).min(path.len() - 1)]);
        if b.0 <= a.0 {
            return a.1;
        }
        let f = (t - a.0) / (b.0 - a.0);
        Vector2 { X: a.1.X + (b.1.X - a.1.X) * f, Y: a.1.Y + (b.1.Y - a.1.Y) * f }
    };
    let now = at(phase);
    let mut keys = vec![(0.0, now)];
    keys.extend(path.iter().filter(|p| p.0 > phase).map(|&(t, v)| (t - phase, v)));
    // Straight back to the start (out of sight: paths begin and end outside the area), then
    // the part before `phase`.
    keys.push((1.0 - phase + 1e-4, path[0].1));
    keys.extend(path.iter().filter(|p| p.0 > 0.0 && p.0 < phase).map(|&(t, v)| (1.0 - phase + t, v)));
    keys.push((1.0, now));
    keys
}

/// Starts `shape` looping along `path`, `phase` of the way in.
fn move_forever(c: &Compositor, shape: &CompositionSpriteShape, path: &[(f32, Vector2)], ms: f32, phase: f32, ease: &CompositionEasingFunction) -> Result<()> {
    let anim = c.CreateVector2KeyFrameAnimation()?;
    for (t, p) in started_at(path, phase) {
        anim.InsertKeyFrameWithEasingFunction(t, p, ease)?;
    }
    anim.SetDuration(ticks(ms))?;
    anim.SetIterationBehavior(AnimationIterationBehavior::Forever)?;
    shape.StartAnimation(h!("Offset"), &anim)
}

/// A radial gradient from the middle (offset 0) to the edge (1).
fn gradient(c: &Compositor, stops: &[(f32, Color)]) -> Result<CompositionRadialGradientBrush> {
    let brush = c.CreateRadialGradientBrush()?;
    let list = brush.ColorStops()?;
    for (at, colour) in stops {
        list.Append(&c.CreateColorGradientStopWithOffsetAndColor(*at, *colour)?)?;
    }
    Ok(brush)
}

/// A soft round light: `colour` in the middle fading out to nothing at the edge (`mid`: how
/// much is left halfway).
fn soft(c: &Compositor, colour: Color, mid: f32) -> Result<CompositionRadialGradientBrush> {
    gradient(c, &[(0.0, colour), (0.5, Color { A: (colour.A as f32 * mid) as u8, ..colour }), (1.0, Color { A: 0, ..colour })])
}

/// An ellipse `rx`×`ry` around `centre` (in its shape's own space), filled with `brush`.
fn blob(c: &Compositor, centre: Vector2, rx: f32, ry: f32, brush: &CompositionBrush) -> Result<CompositionSpriteShape> {
    let geo = c.CreateEllipseGeometry()?;
    geo.SetCenter(centre)?;
    geo.SetRadius(Vector2 { X: rx, Y: ry })?;
    let shape = c.CreateSpriteShapeWithGeometry(&geo)?;
    shape.SetFillBrush(brush)?;
    Ok(shape)
}

/// Soft flakes drifting down, swaying a little, of a few sizes.
fn snow(c: &Compositor, shapes: &ShapeVisual, ease: &CompositionEasingFunction, rng: &mut Rng, a: Area) -> Result<()> {
    let n = ((a.dips() / 1700.0) as usize).clamp(10, 44);
    for _ in 0..n {
        let r = rng.range(1.2, 2.6) * a.k;
        let brush = soft(c, rgba(255, 255, 255, rng.range(170.0, 245.0) as u8), 0.75)?;
        let shape = blob(c, Vector2 { X: 0.0, Y: 0.0 }, r, r, &brush.cast()?)?;
        let (x0, sway, phase) = (rng.range(0.0, a.w), rng.range(2.0, 4.0) * a.k, rng.range(0.0, TAU));
        let fall = a.h + 4.0 * r;
        let path: Vec<(f32, Vector2)> = (0..=6)
            .map(|j| {
                let t = j as f32 / 6.0;
                (t, Vector2 { X: x0 + (t * TAU + phase).sin() * sway, Y: -2.0 * r + fall * t })
            })
            .collect();
        // The same speed in the pill and the card, but never a dash through the pill.
        let ms = (fall / a.k / rng.range(0.035, 0.06)).max(rng.range(1600.0, 2400.0));
        move_forever(c, &shape, &path, ms, rng.next(), ease)?;
        shapes.Shapes()?.Append(&shape)?;
    }
    Ok(())
}

/// Thin streaks, faint at the top, falling fast along their slant (`density`: more for a storm).
fn rain(c: &Compositor, shapes: &ShapeVisual, ease: &CompositionEasingFunction, rng: &mut Rng, a: Area, density: f32) -> Result<()> {
    const SLANT_DEG: f32 = 14.0;
    let n = ((a.dips() / 1500.0 * density) as usize).clamp((12.0 * density) as usize, 80);
    let (len, thick) = (9.0 * a.k, (1.0 * a.k).max(1.0));
    let dx_per_dy = -SLANT_DEG.to_radians().tan();
    for _ in 0..n {
        let geo = c.CreateRoundedRectangleGeometry()?;
        geo.SetSize(Vector2 { X: thick, Y: len })?;
        geo.SetCornerRadius(Vector2 { X: thick / 2.0, Y: thick / 2.0 })?;
        let shape = c.CreateSpriteShapeWithGeometry(&geo)?;
        let brush = c.CreateLinearGradientBrush()?;
        brush.SetStartPoint(Vector2 { X: 0.5, Y: 0.0 })?;
        brush.SetEndPoint(Vector2 { X: 0.5, Y: 1.0 })?;
        let drop = rgba(175, 210, 255, rng.range(110.0, 190.0) as u8);
        brush.ColorStops()?.Append(&c.CreateColorGradientStopWithOffsetAndColor(0.0, Color { A: 0, ..drop })?)?;
        brush.ColorStops()?.Append(&c.CreateColorGradientStopWithOffsetAndColor(1.0, drop)?)?;
        shape.SetFillBrush(&brush)?;
        shape.SetCenterPoint(Vector2 { X: thick / 2.0, Y: len / 2.0 })?;
        shape.SetRotationAngleInDegrees(SLANT_DEG)?;
        let fall = a.h + 2.0 * len;
        let x0 = rng.range(0.0, a.w + fall * -dx_per_dy);
        let path = [(0.0, Vector2 { X: x0, Y: -len }), (1.0, Vector2 { X: x0 + fall * dx_per_dy, Y: a.h + len })];
        let ms = (fall / a.k / rng.range(0.28, 0.38)).max(rng.range(240.0, 320.0));
        move_forever(c, &shape, &path, ms, rng.next(), ease)?;
        shapes.Shapes()?.Append(&shape)?;
    }
    Ok(())
}

/// A storm's lightning: now and then the sky flashes twice.
fn flash(c: &Compositor, root: &ContainerVisual, w: f32, h: f32) -> Result<()> {
    let sprite = c.CreateSpriteVisual()?;
    sprite.SetSize(Vector2 { X: w, Y: h })?;
    sprite.SetBrush(&c.CreateColorBrushWithColor(rgba(225, 225, 255, 255))?)?;
    sprite.SetOpacity(0.0)?;
    let anim = c.CreateScalarKeyFrameAnimation()?;
    for (t, v) in [(0.0, 0.0), (0.70, 0.0), (0.705, 0.32), (0.73, 0.0), (0.755, 0.2), (0.80, 0.0), (1.0, 0.0)] {
        anim.InsertKeyFrame(t, v)?;
    }
    anim.SetDuration(ticks(7000.0))?;
    anim.SetIterationBehavior(AnimationIterationBehavior::Forever)?;
    sprite.StartAnimation(h!("Opacity"), &anim)?;
    root.Children()?.InsertAtTop(&sprite)
}

/// The light `brush` around the icon, breathing slowly.
fn glow(c: &Compositor, root: &ContainerVisual, a: Area, brush: &CompositionRadialGradientBrush, at: (f32, f32), period_ms: f32) -> Result<()> {
    // Round whatever the shape: a pill's light reaches past its height, a card's stays near the icon.
    let r = 0.5 * a.w.min(3.0 * a.h);
    brush.SetEllipseCenter(Vector2 { X: at.0, Y: at.1 })?;
    brush.SetEllipseRadius(Vector2 { X: r / a.w, Y: r / a.h })?;
    let sprite = c.CreateSpriteVisual()?;
    sprite.SetSize(Vector2 { X: a.w, Y: a.h })?;
    sprite.SetBrush(brush)?;
    let anim = c.CreateScalarKeyFrameAnimation()?;
    anim.InsertKeyFrame(0.0, 0.55)?;
    anim.InsertKeyFrame(1.0, 1.0)?;
    anim.SetDuration(ticks(period_ms))?;
    anim.SetIterationBehavior(AnimationIterationBehavior::Forever)?;
    anim.SetDirection(AnimationDirection::Alternate)?;
    sprite.StartAnimation(h!("Opacity"), &anim)?;
    root.Children()?.InsertAtTop(&sprite)
}

/// Small stars, each twinkling at its own pace.
fn stars(c: &Compositor, shapes: &ShapeVisual, rng: &mut Rng, a: Area) -> Result<()> {
    let n = ((a.dips() / 2600.0) as usize).clamp(6, 28);
    for _ in 0..n {
        let r = rng.range(0.5, 1.0) * a.k;
        let geo = c.CreateEllipseGeometry()?;
        geo.SetRadius(Vector2 { X: r, Y: r })?;
        geo.SetCenter(Vector2 { X: rng.range(0.0, a.w), Y: rng.range(0.1, 0.85) * a.h })?;
        let brush = c.CreateColorBrushWithColor(rgba(255, 255, 255, 60))?;
        let anim = c.CreateColorKeyFrameAnimation()?;
        let (lo, hi) = (rng.range(30.0, 70.0) as u8, rng.range(170.0, 240.0) as u8);
        anim.InsertKeyFrame(0.0, rgba(255, 255, 255, lo))?;
        anim.InsertKeyFrame(0.5, rgba(255, 255, 255, hi))?;
        anim.InsertKeyFrame(1.0, rgba(255, 255, 255, lo))?;
        let ms = rng.range(1800.0, 4200.0);
        anim.SetDuration(ticks(ms))?;
        anim.SetIterationBehavior(AnimationIterationBehavior::Forever)?;
        anim.SetDelayTime(ticks(rng.range(0.0, ms)))?;
        brush.StartAnimation(h!("Color"), &anim)?;
        let shape = c.CreateSpriteShapeWithGeometry(&geo)?;
        shape.SetFillBrush(&brush)?;
        shapes.Shapes()?.Append(&shape)?;
    }
    Ok(())
}

/// Soft clouds sliding slowly across, each three puffs moving together.
fn clouds(c: &Compositor, shapes: &ShapeVisual, ease: &CompositionEasingFunction, rng: &mut Rng, a: Area) -> Result<()> {
    // A pill's clouds fill its height; a card's are as tall as a third of it.
    let size = (a.h / a.k).min(110.0) * a.k;
    let n = ((a.w / a.k / 110.0).ceil() as usize).clamp(2, 4);
    for i in 0..n {
        let (cw, ch) = (size * rng.range(0.8, 1.2), size * rng.range(0.3, 0.42));
        let brush: CompositionBrush = soft(c, rgba(255, 255, 255, rng.range(26.0, 40.0) as u8), 0.6)?.cast()?;
        let y = rng.range(0.15, 0.75) * a.h;
        let path = [(0.0, Vector2 { X: -1.6 * cw, Y: y }), (1.0, Vector2 { X: a.w + 1.6 * cw, Y: y })];
        let ms = (a.w + 3.2 * cw) / a.k / rng.range(0.012, 0.02);
        // Spread out, not bunched: each its own share of the way across.
        let phase = (i as f32 + rng.range(0.1, 0.6)) / n as f32;
        for (dx, dy, sx, sy) in [(0.0, 0.0, 1.0, 1.0), (-0.55, 0.22, 0.6, 0.75), (0.5, 0.18, 0.7, 0.8)] {
            let puff = blob(c, Vector2 { X: dx * cw, Y: dy * ch }, cw * sx, ch * sy, &brush)?;
            move_forever(c, &puff, &path, ms, phase, ease)?;
            shapes.Shapes()?.Append(&puff)?;
        }
    }
    Ok(())
}

/// Long faint bands drifting both ways, slowly.
fn fog(c: &Compositor, shapes: &ShapeVisual, ease: &CompositionEasingFunction, rng: &mut Rng, a: Area) -> Result<()> {
    let n = if a.h / a.k < 60.0 { 3 } else { 5 };
    let thick = (a.h / a.k).min(110.0) * a.k;
    for i in 0..n {
        let (bw, bh) = (a.w * rng.range(0.55, 0.85), thick * rng.range(0.12, 0.2));
        let brush: CompositionBrush = soft(c, rgba(235, 238, 245, rng.range(40.0, 58.0) as u8), 0.7)?.cast()?;
        let band = blob(c, Vector2 { X: 0.0, Y: 0.0 }, bw, bh, &brush)?;
        let y = a.h * (i as f32 + 0.5) / n as f32 + rng.range(-0.06, 0.06) * a.h;
        let (from, to) = (-bw, a.w + bw);
        let (from, to) = if i % 2 == 0 { (from, to) } else { (to, from) };
        let path = [(0.0, Vector2 { X: from, Y: y }), (1.0, Vector2 { X: to, Y: y })];
        let ms = (a.w + 2.0 * bw) / a.k / rng.range(0.008, 0.014);
        move_forever(c, &band, &path, ms, rng.next(), ease)?;
        shapes.Shapes()?.Append(&band)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn v(x: f32, y: f32) -> Vector2 {
        Vector2 { X: x, Y: y }
    }

    #[test]
    fn a_loop_starts_under_way() {
        // A fall from 0 to 100 begun a quarter in: at 25 now, at the bottom three quarters
        // later, straight back at the top, and at 25 again when the loop comes round.
        let k = started_at(&[(0.0, v(0.0, 0.0)), (1.0, v(0.0, 100.0))], 0.25);
        assert_eq!(k.first(), Some(&(0.0, v(0.0, 25.0))), "{k:?}");
        let bottom = k.iter().position(|p| *p == (0.75, v(0.0, 100.0))).expect("reaches the bottom at 0.75");
        let (t, top) = k[bottom + 1];
        assert_eq!(top, v(0.0, 0.0), "jumps back to the top");
        assert!(t > 0.75 && t < 0.7502, "at once: {t}");
        assert_eq!(k.last(), Some(&(1.0, v(0.0, 25.0))));
        assert!(k.windows(2).all(|w| w[0].0 < w[1].0), "progress only grows: {k:?}");
    }

    #[test]
    fn a_sway_keeps_its_middle_points() {
        let path = [(0.0, v(0.0, 0.0)), (0.5, v(10.0, 50.0)), (1.0, v(0.0, 100.0))];
        let k = started_at(&path, 0.75);
        assert_eq!(k.first(), Some(&(0.0, v(5.0, 75.0))), "between the points: {k:?}");
        assert!(k.contains(&(0.75, v(10.0, 50.0))), "the middle comes after the jump: {k:?}");
        assert_eq!(k.last(), Some(&(1.0, v(5.0, 75.0))));
        assert_eq!(started_at(&path, 0.0), path.to_vec(), "from the start: the path itself");
    }
}
