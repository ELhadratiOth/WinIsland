//! Pill ⇄ card morph, drawn entirely in the card window. One scalar `t` (0 = pill, 1 = card),
//! driven by a sampled spring (see `motion`), decides every rect, radius and opacity through expression animations,
//! so the whole morph runs in the compositor at the display's refresh rate.
//!
//! At t = 0 the card window shows an exact copy of the pill (same surface, same colours) over
//! the real one, which the app hides in the same frame; that hand-off is invisible.

use windows::core::{h, Interface, Result, HSTRING};
use windows::Foundation::{TimeSpan, TypedEventHandler};
use windows::UI::Color;
use windows::UI::Composition::*;
use windows::Win32::Graphics::Direct2D::Common::D2D1_COMPOSITE_MODE_SOURCE_OVER;
use windows::Win32::Graphics::Direct2D::{
    CLSID_D2D1Shadow, ID2D1Image, D2D1_INTERPOLATION_MODE_LINEAR, D2D1_PROPERTY_TYPE_FLOAT, D2D1_SHADOW_PROP_BLUR_STANDARD_DEVIATION,
};
use windows_numerics::{Matrix3x2, Vector2};

use super::gfx::{draw_surface, rect, rgba, Gfx};
use super::material::{blurred_card_brush, MASK_R};
use super::motion::{scaled_ticks, spring_samples, SPRING_DURATION_S};
use crate::platform::windows::{push_event, UiEvent};
use crate::render::layout::RectPx;

/// Page turn inside an open card: out, then in (One UI Basic easing on the way in).
pub const PAGE_OUT_MS: i64 = 90;
const PAGE_IN_MS: i64 = 160;

/// Key frames per morph; linear between them is indistinguishable from the curve at 144 Hz.
const SPRING_FRAMES: usize = 32;
/// Shadow under the card: 24 DIP blur, 6 DIP down, black 35 %.
pub const SHADOW_BLUR_DIP: f32 = 24.0;
pub const SHADOW_DY_DIP: f32 = 6.0;
const SHADOW_OPACITY: f32 = 0.35;
/// Source radius of the shadow nine-grid, in px; inset scales map it to the live radius.
const SHADOW_R: f32 = 48.0;

pub use super::pill::PillLook;

pub struct Morph {
    c: Compositor,
    props: CompositionPropertySet,
    /// The pill's material (flat or album-tinted, top→bottom); fades out as the card's comes in.
    pill_base: CompositionLinearGradientBrush,
    /// Album tint of the media card over the blur (transparent for other cards).
    tint: CompositionLinearGradientBrush,
    /// Copies of the pill's content layers.
    pill_copy: ContainerVisual,
    accent: CompositionLinearGradientBrush,
    card_brush: CompositionSurfaceBrush,
    blur: SpriteVisual,
    blurred: CompositionMaskBrush,
    /// Opaque fallback when Windows transparency effects are off (or Energy Saver is on).
    flat: CompositionMaskBrush,
    /// The weather card's moving background, under the card's content.
    fx: ContainerVisual,
}

/// Card body when transparency effects are off: #1C1C1E at 96 %.
const FLAT_MATERIAL: Color = Color { A: 245, R: 0x1C, G: 0x1C, B: 0x1E };

fn color(a: u8, (r, g, b): (u8, u8, u8)) -> Color {
    Color { A: a, R: r, G: g, B: b }
}

impl Morph {
    pub fn new(g: &Gfx, root: &ContainerVisual, shadow_margin_px: f32, shadow_dy_px: f32) -> Result<Self> {
        let c = g.compositor.clone();
        let props = c.CreatePropertySet()?;
        // t: where the shape is between the from and to rects; o: how open the card is (the
        // materials and contents). They spring together, except when an open card changes size.
        for k in ["t", "o", "fx", "fy", "fw", "fh", "fr", "tx", "ty", "tw", "th", "tr", "ph", "px"] {
            props.InsertScalar(&HSTRING::from(k), 0.0)?;
        }
        // Page turns inside an open card (media ⇄ output): content opacity and slide.
        props.InsertScalar(h!("pg"), 1.0)?;
        props.InsertScalar(h!("m"), shadow_margin_px)?;
        props.InsertScalar(h!("dy"), shadow_dy_px)?;
        props.InsertScalar(h!("mr"), MASK_R)?;
        props.InsertScalar(h!("sr"), SHADOW_R)?;

        let l = "(p.fx+(p.tx-p.fx)*p.t)";
        let t = "(p.fy+(p.ty-p.fy)*p.t)";
        let w = "(p.fw+(p.tw-p.fw)*p.t)";
        let hh = "(p.fh+(p.th-p.fh)*p.t)";
        let r = format!("Min(p.fr+(p.tr-p.fr)*p.t,{hh}/2)");
        let bind = |obj: &CompositionObject, prop: &str, expr: &str| -> Result<()> {
            let e = c.CreateExpressionAnimationWithExpression(&HSTRING::from(expr))?;
            e.SetReferenceParameter(h!("p"), &props)?;
            obj.StartAnimation(&HSTRING::from(prop), &e)
        };
        let geometry = || -> Result<CompositionRoundedRectangleGeometry> {
            let geo = c.CreateRoundedRectangleGeometry()?;
            let o: CompositionObject = geo.cast()?;
            bind(&o, "Offset", &format!("Vector2({l},{t})"))?;
            bind(&o, "Size", &format!("Vector2({w},{hh})"))?;
            bind(&o, "CornerRadius", &format!("Vector2({r},{r})"))?;
            Ok(geo)
        };
        let shape_layer = |fill: &CompositionBrush, opacity: &str| -> Result<ShapeVisual> {
            let v = c.CreateShapeVisual()?;
            v.SetRelativeSizeAdjustment(Vector2 { X: 1.0, Y: 1.0 })?;
            let shape = c.CreateSpriteShapeWithGeometry(&geometry()?)?;
            shape.SetFillBrush(fill)?;
            v.Shapes()?.Append(&shape)?;
            bind(&v.cast()?, "Opacity", opacity)?;
            Ok(v)
        };
        let kids = root.Children()?;

        // Shadow (card only).
        let shadow_surface = shadow_surface(g, shadow_margin_px)?;
        let shadow_source = c.CreateSurfaceBrushWithSurface(&shadow_surface)?;
        shadow_source.SetStretch(CompositionStretch::Fill)?;
        let shadow_nine = c.CreateNineGridBrush()?;
        shadow_nine.SetSource(&shadow_source)?;
        shadow_nine.SetInsets(shadow_margin_px + SHADOW_R)?;
        let k = format!("({r}/p.sr)");
        for side in ["LeftInsetScale", "TopInsetScale", "RightInsetScale", "BottomInsetScale"] {
            bind(&shadow_nine.cast()?, side, &k)?;
        }
        let shadow = c.CreateSpriteVisual()?;
        shadow.SetBrush(&shadow_nine)?;
        let so: CompositionObject = shadow.cast()?;
        bind(&so, "Offset", &format!("Vector3({l}-p.m*{k},{t}-p.m*{k}+p.dy,0)"))?;
        bind(&so, "Size", &format!("Vector2({w}+2*p.m*{k},{hh}+2*p.m*{k})"))?;
        bind(&so, "Opacity", &format!("Clamp(p.o,0,1)*{SHADOW_OPACITY}"))?;
        kids.InsertAtTop(&shadow)?;

        // Blurred One UI material (card), cross-fading in over the pill's flat material.
        let (blur_brush, nine) = blurred_card_brush(g)?;
        for side in ["LeftInsetScale", "TopInsetScale", "RightInsetScale", "BottomInsetScale"] {
            bind(&nine.cast()?, side, &format!("{r}/p.mr"))?;
        }
        let flat = c.CreateMaskBrush()?;
        flat.SetSource(&c.CreateColorBrushWithColor(FLAT_MATERIAL)?)?;
        flat.SetMask(&nine)?;
        let blur = c.CreateSpriteVisual()?;
        blur.SetBrush(&blur_brush)?;
        let bo: CompositionObject = blur.cast()?;
        bind(&bo, "Offset", &format!("Vector3({l},{t},0)"))?;
        bind(&bo, "Size", &format!("Vector2({w},{hh})"))?;
        bind(&bo, "Opacity", "Clamp(p.o*2,0,1)")?;
        kids.InsertAtTop(&blur)?;

        let vertical = || -> Result<CompositionLinearGradientBrush> {
            let brush = c.CreateLinearGradientBrush()?;
            brush.SetMappingMode(CompositionMappingMode::Absolute)?;
            bind(&brush.cast()?, "StartPoint", &format!("Vector2(0,{t})"))?;
            bind(&brush.cast()?, "EndPoint", &format!("Vector2(0,{t}+{hh})"))?;
            Ok(brush)
        };
        let tint = vertical()?;
        kids.InsertAtTop(&shape_layer(&tint.cast()?, "Clamp(p.o*2,0,1)")?)?;
        let pill_base = vertical()?;
        kids.InsertAtTop(&shape_layer(&pill_base.cast()?, "1-Clamp(p.o*2,0,1)")?)?;

        let accent = c.CreateLinearGradientBrush()?;
        accent.SetMappingMode(CompositionMappingMode::Absolute)?;
        bind(&accent.cast()?, "StartPoint", &format!("Vector2({l},0)"))?;
        bind(&accent.cast()?, "EndPoint", &format!("Vector2({l}+{w},0)"))?;
        kids.InsertAtTop(&shape_layer(&accent.cast()?, "1")?)?;

        // Pill content: fades out early, anchored to the left edge, vertically centred.
        let pill_copy = c.CreateContainerVisual()?;
        let po: CompositionObject = pill_copy.cast()?;
        bind(&po, "Offset", &format!("Vector3({l},{t}+({hh}-p.ph)/2,0)"))?;
        bind(&po, "Opacity", "Clamp(1-p.o*2.5,0,1)")?;
        kids.InsertAtTop(&pill_copy)?;

        // Card content: fades in late, anchored top-left, clipped to the morphing shape so it
        // never spills out mid-way (the clip's edge is aliased, but at rest nothing touches it).
        let card_holder = c.CreateContainerVisual()?;
        card_holder.SetRelativeSizeAdjustment(Vector2 { X: 1.0, Y: 1.0 })?;
        card_holder.SetClip(&c.CreateGeometricClipWithGeometry(&geometry()?)?)?;
        let card_brush = c.CreateSurfaceBrush()?;
        // At its own size from the top-left: while an open card changes size, the old picture
        // isn't stretched to the new one.
        card_brush.SetStretch(CompositionStretch::None)?;
        card_brush.SetHorizontalAlignmentRatio(0.0)?;
        card_brush.SetVerticalAlignmentRatio(0.0)?;
        let card_content = c.CreateSpriteVisual()?;
        card_content.SetBrush(&card_brush)?;
        let co: CompositionObject = card_content.cast()?;
        bind(&co, "Offset", &format!("Vector3({l}+p.px,{t},0)"))?;
        bind(&co, "Size", "Vector2(p.tw,p.th)")?;
        bind(&co, "Opacity", "Clamp((p.o-0.35)/0.45,0,1)*p.pg")?;
        let fx = c.CreateContainerVisual()?;
        let fo: CompositionObject = fx.cast()?;
        bind(&fo, "Offset", &format!("Vector3({l},{t},0)"))?;
        bind(&fo, "Opacity", "Clamp((p.o-0.35)/0.45,0,1)*p.pg")?;
        card_holder.Children()?.InsertAtTop(&fx)?;
        card_holder.Children()?.InsertAtTop(&card_content)?;
        kids.InsertAtTop(&card_holder)?;

        Ok(Morph { c, props, pill_base, tint, pill_copy, accent, card_brush, blur, blurred: blur_brush, flat, fx })
    }

    fn set_stops(&self, brush: &CompositionLinearGradientBrush, stops: &[(f32, Color)]) -> Result<()> {
        let list = brush.ColorStops()?;
        list.Clear()?;
        for (offset, color) in stops {
            list.Append(&self.c.CreateColorGradientStopWithOffsetAndColor(*offset, *color)?)?;
        }
        Ok(())
    }

    /// Starts the morph from an exact copy of the pill as it looks now.
    pub fn set_pill(&self, look: &PillLook) -> Result<()> {
        let children = self.pill_copy.Children()?;
        children.RemoveAll()?;
        if let Some(scene) = &look.scene {
            let (w, h) = (look.size_px.0 as f32, look.size_px.1 as f32);
            let live = self.c.CreateVisualSurface()?;
            live.SetSourceVisual(scene)?;
            live.SetSourceSize(Vector2 { X: w, Y: h })?;
            let sprite = self.c.CreateSpriteVisual()?;
            sprite.SetBrush(&self.c.CreateSurfaceBrushWithSurface(&live)?)?;
            sprite.SetSize(Vector2 { X: w, Y: h })?;
            // The pill's own clip is on its parent, which the surface doesn't see.
            let shape = self.c.CreateRoundedRectangleGeometry()?;
            shape.SetSize(Vector2 { X: w, Y: h })?;
            shape.SetCornerRadius(Vector2 { X: h / 2.0, Y: h / 2.0 })?;
            sprite.SetClip(&self.c.CreateGeometricClipWithGeometry(&shape)?)?;
            children.InsertAtTop(&sprite)?;
        }
        for layer in &look.layers {
            let brush = self.c.CreateSurfaceBrushWithSurface(&layer.surface)?;
            brush.SetStretch(CompositionStretch::None)?;
            brush.SetHorizontalAlignmentRatio(0.0)?;
            brush.SetVerticalAlignmentRatio(0.0)?;
            let sprite = self.c.CreateSpriteVisual()?;
            sprite.SetBrush(&brush)?;
            sprite.SetOffset(windows_numerics::Vector3 { X: layer.offset.0, Y: layer.offset.1, Z: 0.0 })?;
            sprite.SetSize(Vector2 { X: layer.size.0, Y: layer.size.1 })?;
            children.InsertAtTop(&sprite)?;
        }
        self.props.InsertScalar(h!("ph"), look.size_px.1 as f32)?;
        self.set_stops(&self.pill_base, &[(0.0, look.base.0), (1.0, look.base.1)])?;
        self.set_accent(look.accent)
    }

    /// Album tint over the card's blur (media), or none.
    pub fn set_card_tint(&self, tint: Option<(Color, Color)>) -> Result<()> {
        let clear = Color { A: 0, R: 0, G: 0, B: 0 };
        let (top, bottom) = tint.unwrap_or((clear, clear));
        self.set_stops(&self.tint, &[(0.0, top), (1.0, bottom)])
    }

    /// Moves the album tint to new colours over `ms` while the card stays open (a new track).
    pub fn fade_card_tint(&self, tint: Option<(Color, Color)>, ms: i64) -> Result<()> {
        let clear = Color { A: 0, R: 0, G: 0, B: 0 };
        let (top, bottom) = tint.unwrap_or((clear, clear));
        let stops = self.tint.ColorStops()?;
        if stops.Size()? != 2 {
            return self.set_card_tint(tint);
        }
        for (i, target) in [top, bottom].into_iter().enumerate() {
            let anim = self.c.CreateColorKeyFrameAnimation()?;
            anim.InsertKeyFrame(1.0, target)?;
            anim.SetDuration(TimeSpan { Duration: scaled_ticks(ms as f32) })?;
            stops.GetAt(i as u32)?.StartAnimation(h!("Color"), &anim)?;
        }
        Ok(())
    }

    /// Blurred One UI material, or the flat fallback when transparency effects are off.
    pub fn set_blur(&self, enabled: bool) -> Result<()> {
        if enabled {
            self.blur.SetBrush(&self.blurred)
        } else {
            self.blur.SetBrush(&self.flat)
        }
    }

    /// For cards that don't grow out of the pill (the picker): nothing to morph from.
    pub fn set_no_pill(&self, accent: (u8, u8, u8)) -> Result<()> {
        self.pill_copy.Children()?.RemoveAll()?;
        let clear = Color { A: 0, R: 0, G: 0, B: 0 };
        self.set_stops(&self.pill_base, &[(0.0, clear), (1.0, clear)])?;
        self.set_accent(Some(accent))
    }

    /// Puts `scene` (or nothing) under the card's content, in place of the last one.
    pub fn set_card_fx(&self, scene: Option<&ContainerVisual>) -> Result<()> {
        self.fx.Children()?.RemoveAll()?;
        if let Some(scene) = scene {
            self.fx.Children()?.InsertAtTop(scene)?;
        }
        Ok(())
    }

    pub fn set_card(&self, surface: &CompositionDrawingSurface) -> Result<()> {
        self.card_brush.SetSurface(surface)
    }

    /// Left accent gradient (timers, Buds, picker); none for media.
    fn set_accent(&self, accent: Option<(u8, u8, u8)>) -> Result<()> {
        let accent_color = accent.unwrap_or((0, 0, 0));
        let start = if accent.is_some() { 97 } else { 0 };
        self.set_stops(&self.accent, &[(0.0, color(start, accent_color)), (0.5, color(0, accent_color))])
    }

    /// Turns the open card's page: the content fades and slides `slide_px` out (negative = left)
    /// in the first `PAGE_OUT_MS`, the caller repaints it then, and it comes back from the other side.
    pub fn page_turn(&self, slide_px: f32) -> Result<()> {
        let total = PAGE_OUT_MS + PAGE_IN_MS;
        let mid = PAGE_OUT_MS as f32 / total as f32;
        let ease = self.c.CreateCubicBezierEasingFunction(Vector2 { X: 0.22, Y: 0.25 }, Vector2 { X: 0.0, Y: 1.0 })?;
        let fade = self.c.CreateScalarKeyFrameAnimation()?;
        fade.InsertKeyFrame(0.0, 1.0)?;
        fade.InsertKeyFrame(mid, 0.0)?;
        fade.InsertKeyFrameWithEasingFunction(1.0, 1.0, &ease)?;
        fade.SetDuration(TimeSpan { Duration: scaled_ticks(total as f32) })?;
        let slide = self.c.CreateScalarKeyFrameAnimation()?;
        slide.InsertKeyFrame(0.0, 0.0)?;
        slide.InsertKeyFrame(mid, slide_px)?;
        slide.InsertKeyFrame((mid + 0.001).min(1.0), -slide_px)?;
        slide.InsertKeyFrameWithEasingFunction(1.0, 0.0, &ease)?;
        slide.SetDuration(TimeSpan { Duration: scaled_ticks(total as f32) })?;
        self.props.StartAnimation(h!("pg"), &fade)?;
        self.props.StartAnimation(h!("px"), &slide)
    }

    /// Rects are in card-window pixels; radii in pixels.
    pub fn open(&self, from: RectPx, from_r: f32, to: RectPx, to_r: f32) -> Result<()> {
        for (k, v) in [
            ("fx", from.x as f32),
            ("fy", from.y as f32),
            ("fw", from.w as f32),
            ("fh", from.h as f32),
            ("fr", from_r),
            ("tx", to.x as f32),
            ("ty", to.y as f32),
            ("tw", to.w as f32),
            ("th", to.h as f32),
            ("tr", to_r),
        ] {
            self.props.InsertScalar(&HSTRING::from(k), v)?;
        }
        self.animate(1.0, true)
    }

    pub fn close(&self) -> Result<()> {
        self.animate(0.0, false)
    }

    /// Where a closing card shrinks to: a resize leaves the shape's start on the card it came
    /// from, so the close would end on that card (and vanish) instead of in the pill.
    pub fn set_from(&self, from: RectPx, from_r: f32) -> Result<()> {
        for (k, v) in [("fx", from.x as f32), ("fy", from.y as f32), ("fw", from.w as f32), ("fh", from.h as f32), ("fr", from_r)] {
            self.props.InsertScalar(&HSTRING::from(k), v)?;
        }
        Ok(())
    }

    /// An open card becomes another of a different size: the shape springs from `from` to `to`
    /// (card-window pixels) while the card stays open; nothing is queued when it ends.
    pub fn resize(&self, from: RectPx, from_r: f32, to: RectPx, to_r: f32) -> Result<()> {
        self.props.StopAnimation(h!("t"))?;
        for (k, v) in [
            ("t", 0.0),
            ("fx", from.x as f32),
            ("fy", from.y as f32),
            ("fw", from.w as f32),
            ("fh", from.h as f32),
            ("fr", from_r),
            ("tx", to.x as f32),
            ("ty", to.y as f32),
            ("tw", to.w as f32),
            ("th", to.h as f32),
            ("tr", to_r),
        ] {
            self.props.InsertScalar(&HSTRING::from(k), v)?;
        }
        self.props.StartAnimation(h!("t"), &self.spring(1.0)?)
    }

    fn spring(&self, to: f32) -> Result<ScalarKeyFrameAnimation> {
        let anim = self.c.CreateScalarKeyFrameAnimation()?;
        let linear = self.c.CreateLinearEasingFunction()?;
        for (progress, k) in spring_samples(SPRING_FRAMES) {
            let expr = format!("this.StartingValue+({to}-this.StartingValue)*{k}");
            anim.InsertExpressionKeyFrameWithEasingFunction(progress, &HSTRING::from(expr), &linear)?;
        }
        anim.SetDuration(TimeSpan { Duration: scaled_ticks(SPRING_DURATION_S * 1000.0) })?;
        Ok(anim)
    }

    /// Springs `t` from wherever it is to `to`; queues `UiEvent::MorphDone` when it ends
    /// (or is interrupted by the next morph).
    fn animate(&self, to: f32, opened: bool) -> Result<()> {
        let batch = self.c.CreateScopedBatch(CompositionBatchTypes::Animation)?;
        let anim = self.spring(to)?;
        self.props.StartAnimation(h!("t"), &anim)?;
        self.props.StartAnimation(h!("o"), &anim)?;
        batch.End()?;
        batch.Completed(&TypedEventHandler::new(move |_, _| {
            push_event(UiEvent::MorphDone { opened });
            Ok(())
        }))?;
        Ok(())
    }
}

fn shadow_surface(g: &Gfx, margin: f32) -> Result<CompositionDrawingSurface> {
    let side = 2.0 * (margin + SHADOW_R) + 2.0;
    draw_surface(g, side as i32, side as i32, |ink| unsafe {
        let dc = ink.dc;
        let saved = {
            let mut m = Matrix3x2::default();
            dc.GetTransform(&mut m);
            m
        };
        dc.SetTransform(&Matrix3x2::identity());
        let list = dc.CreateCommandList()?;
        let target: ID2D1Image = dc.GetTarget()?;
        dc.SetTarget(&list);
        ink.rrect(rect(margin, margin, side - 2.0 * margin, side - 2.0 * margin), SHADOW_R, rgba(0, 0, 0, 1.0))?;
        dc.SetTarget(&target);
        list.Close()?;
        let effect = dc.CreateEffect(&CLSID_D2D1Shadow)?;
        effect.SetInput(0, &list, true);
        let sigma = (margin / 3.0).to_le_bytes();
        effect.SetValue(D2D1_SHADOW_PROP_BLUR_STANDARD_DEVIATION.0 as u32, D2D1_PROPERTY_TYPE_FLOAT, &sigma)?;
        dc.SetTransform(&saved);
        dc.DrawImage(&effect.GetOutput()?, None, None, D2D1_INTERPOLATION_MODE_LINEAR, D2D1_COMPOSITE_MODE_SOURCE_OVER);
        Ok(())
    })
}
