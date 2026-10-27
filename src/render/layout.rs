//! Physical-pixel layout: DIP scaling and where the pill and cards sit.

/// DIP to physical-pixel factor (`dpi / 96`). One One UI dp is one DIP.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Scale(pub f32);

impl Scale {
    pub fn from_dpi(dpi: u32) -> Self {
        Scale(dpi as f32 / 96.0)
    }

    /// Nearest whole pixel.
    pub fn px(&self, dip: f32) -> f32 {
        (dip * self.0).round()
    }

    /// Nearest even pixel count, so centring never lands on a half pixel.
    pub fn even_px(&self, dip: f32) -> f32 {
        (dip * self.0 / 2.0).round() * 2.0
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct RectPx {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
}

impl RectPx {
    pub fn right(&self) -> i32 {
        self.x + self.w
    }

    pub fn bottom(&self) -> i32 {
        self.y + self.h
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TaskbarAlign {
    Left,
    Center,
}

#[derive(Clone, Copy, Debug)]
pub struct TaskbarInfo {
    pub rect: RectPx,
    pub align: TaskbarAlign,
    pub widgets: bool,
    /// At the bottom of the screen: cards open above it. False for [`top_strip`]: cards open
    /// below it.
    pub bottom: bool,
}

/// The band at the top of the screen the pill lives in: as tall as Windows 11's taskbar, so the
/// pill sits 8 DIP below the screen's edge at the standard size.
pub const STRIP_H_DIP: f32 = 48.0;

/// How far below the screen's edge the pill sits, and the card that grows out of it.
pub const TOP_INSET_DIP: f32 = 8.0;

/// The island's place: a band across the top of `monitor` (screen pixels), the pill centred in
/// it and cards opening below it.
pub fn top_strip(monitor: RectPx, s: &Scale) -> TaskbarInfo {
    let rect = RectPx { x: monitor.x, y: monitor.y, w: monitor.w, h: s.px(STRIP_H_DIP) as i32 };
    TaskbarInfo { rect, align: TaskbarAlign::Left, widgets: false, bottom: false }
}

/// Where the pill sits in `bar` (the top strip, or a taskbar), in screen pixels: centred when
/// `align` is Left (nothing in the middle), at the left (after Widgets) when it is Center.
/// `offset_dip` (a setting) moves it right (+) or left (−).
pub fn pill_rect(bar: &TaskbarInfo, w_dip: f32, h_dip: f32, s: &Scale, offset_dip: f32) -> RectPx {
    let w = s.even_px(w_dip) as i32;
    let h = s.px(h_dip) as i32;
    let x = match bar.align {
        TaskbarAlign::Left => bar.rect.x + (bar.rect.w - w) / 2,
        TaskbarAlign::Center => bar.rect.x + s.px(if bar.widgets { 170.0 } else { 12.0 }) as i32,
    } + s.px(offset_dip) as i32;
    let y = bar.rect.y + ((bar.rect.h - h) as f32 / 2.0).round() as i32;
    RectPx { x, y, w, h }
}

/// The pill size setting's `factor`, made smaller where the taskbar (`bar_h` px tall) is too low
/// for a pill `h_dip` tall at `s` to keep 3 DIP above and below it.
pub fn pill_fit(bar_h: i32, h_dip: f32, s: &Scale, factor: f32) -> f32 {
    let room = bar_h as f32 - 2.0 * s.px(3.0);
    factor.min(room / (h_dip * s.0))
}

/// A card centred on `center_x`: `gap_dip` above a bottom taskbar, or in the top strip as far
/// below the screen's edge as the pill (it grows out of the pill, down).
pub fn card_rect(bar: &TaskbarInfo, center_x: i32, w_dip: f32, h_dip: f32, gap_dip: f32, s: &Scale) -> RectPx {
    let w = s.even_px(w_dip) as i32;
    let h = s.px(h_dip) as i32;
    let gap = s.px(gap_dip) as i32;
    let y = if bar.bottom { bar.rect.y - gap - h } else { bar.rect.y + s.px(TOP_INSET_DIP) as i32 };
    // Centred on the pill, but never off the taskbar's monitor (a pill at the far left: icons
    // centred with Widgets off, or a large offset).
    let margin = s.px(8.0) as i32;
    let x = (center_x - w / 2).min(bar.rect.x + bar.rect.w - margin - w).max(bar.rect.x + margin);
    RectPx { x, y, w, h }
}

/// The card window's left edge for a `w` px wide window centred on `center_x`, kept on the
/// taskbar's monitor (the card inside it is kept 8 DIP in, so it always fits).
pub fn frame_x(bar: &TaskbarInfo, center_x: i32, w: i32) -> i32 {
    (center_x - w / 2).min(bar.rect.x + bar.rect.w - w).max(bar.rect.x)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bar(align: TaskbarAlign, widgets: bool) -> TaskbarInfo {
        TaskbarInfo { rect: RectPx { x: 0, y: 1030, w: 1920, h: 50 }, align, widgets, bottom: true }
    }

    #[test]
    fn a_card_stays_on_the_taskbars_monitor() {
        let s = Scale::from_dpi(120);
        let b = bar(TaskbarAlign::Center, false);
        let left = card_rect(&b, 15, 340.0, 77.0, 12.0, &s);
        assert_eq!(left.x, s.px(8.0) as i32, "a pill at the far left: the card stops 8 DIP in");
        let right = card_rect(&b, 1915, 340.0, 77.0, 12.0, &s);
        assert_eq!(right.x + right.w, 1920 - s.px(8.0) as i32);
        assert_eq!(card_rect(&b, 960, 340.0, 77.0, 12.0, &s).x, 960 - 426 / 2, "in the middle: centred on the pill");
    }

    #[test]
    fn the_card_window_stays_on_the_monitor_too() {
        let b = bar(TaskbarAlign::Center, false);
        assert_eq!(frame_x(&b, 15, 600), 0);
        assert_eq!(frame_x(&b, 1915, 600), 1920 - 600);
        assert_eq!(frame_x(&b, 960, 600), 660);
    }

    #[test]
    fn scale_rounding() {
        let s = Scale::from_dpi(120);
        assert_eq!(s.px(12.0), 15.0);
        assert_eq!(s.even_px(242.0), 302.0);
        assert_eq!(s.even_px(340.0), 426.0);
        assert_eq!(s.px(350.0), 438.0);
    }

    #[test]
    fn pill_centred_on_left_aligned_taskbar() {
        assert_eq!(
            pill_rect(&bar(TaskbarAlign::Left, false), 176.0, 32.0, &Scale::from_dpi(120), 0.0),
            RectPx { x: 850, y: 1035, w: 220, h: 40 }
        );
    }

    #[test]
    fn pill_left_when_icons_centred() {
        let s = Scale::from_dpi(120);
        assert_eq!(pill_rect(&bar(TaskbarAlign::Center, false), 176.0, 32.0, &s, 0.0).x, 15);
        assert_eq!(pill_rect(&bar(TaskbarAlign::Center, true), 176.0, 32.0, &s, 0.0).x, 213);
    }

    /// The pill offset setting moves the pill (and with it the cards) either way.
    #[test]
    fn pill_offset_moves_it() {
        let (b, s) = (bar(TaskbarAlign::Left, false), Scale::from_dpi(120));
        let centred = pill_rect(&b, 300.0, 32.0, &s, 0.0);
        assert_eq!(pill_rect(&b, 300.0, 32.0, &s, -20.0).x, centred.x - 25);
        assert_eq!(pill_rect(&b, 300.0, 32.0, &s, 40.0).x, centred.x + 50);
    }

    /// Windows 11's taskbar (48 DIP) has room for every size; Windows 10's (40, or 30 with small
    /// buttons) makes the pill smaller so it keeps 3 DIP above and below.
    #[test]
    fn pill_fits_the_taskbars_height() {
        let one = Scale::from_dpi(96);
        assert_eq!(pill_fit(48, 32.0, &one, 1.125), 1.125, "Windows 11, Large");
        assert_eq!(pill_fit(40, 32.0, &one, 1.0), 1.0, "Windows 10, Standard");
        assert_eq!(pill_fit(40, 32.0, &one, 1.125), 34.0 / 32.0, "Windows 10, Large: 34 px");
        assert_eq!(pill_fit(30, 32.0, &one, 0.875), 0.75, "small buttons: 24 px");
        let s = Scale::from_dpi(144);
        assert_eq!(pill_fit(72, 32.0, &s, 1.125), 1.125, "Windows 11 at 150 %");
        assert_eq!(pill_fit(45, 32.0, &s, 1.0), 35.0 / 48.0, "small buttons at 150 %: 35 px");
    }

    #[test]
    fn the_strip_spans_the_top_of_the_monitor() {
        let s = Scale::from_dpi(120);
        let strip = top_strip(RectPx { x: 1920, y: -200, w: 2560, h: 1440 }, &s);
        assert_eq!(strip.rect, RectPx { x: 1920, y: -200, w: 2560, h: 60 });
        assert!(!strip.bottom);
    }

    /// The pill at the top centre of the screen, 8 DIP below its edge.
    #[test]
    fn pill_at_the_top_centre() {
        let s = Scale::from_dpi(120);
        let strip = top_strip(RectPx { x: 0, y: 0, w: 1920, h: 1080 }, &s);
        assert_eq!(pill_rect(&strip, 176.0, 32.0, &s, 0.0), RectPx { x: 850, y: 10, w: 220, h: 40 });
    }

    /// The card's top where the pill's is: as far from the top of the screen as the small island.
    #[test]
    fn card_opens_level_with_the_pill() {
        let s = Scale::from_dpi(120);
        let strip = top_strip(RectPx { x: 0, y: 0, w: 1920, h: 1080 }, &s);
        let card = card_rect(&strip, 960, 340.0, 350.0, 12.0, &s);
        assert_eq!(card, RectPx { x: 747, y: 10, w: 426, h: 438 });
        assert_eq!(card.y, pill_rect(&strip, 176.0, 32.0, &s, 0.0).y);
    }

    #[test]
    fn card_sits_12dip_above_taskbar() {
        assert_eq!(
            card_rect(&bar(TaskbarAlign::Left, false), 960, 340.0, 350.0, 12.0, &Scale::from_dpi(120)),
            RectPx { x: 747, y: 577, w: 426, h: 438 }
        );
    }
}
