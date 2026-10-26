//! Weather alerts beyond the rain: thunder about to start, strong gusts, a hot day, frost ahead,
//! bad air. Each warns once a spell, as the rain moment does. The pure part; the forecast and the
//! air come with the weather worker's answer.

use super::weather::{rain_when, Weather};
use crate::i18n::{tr, trf};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum AlertKind {
    Storm,
    Wind,
    Heat,
    Frost,
    Air,
}

/// One alert: what, its value (km/h, °C, the air index; unused for thunder) and when it starts
/// (unix seconds).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Alert {
    pub kind: AlertKind,
    pub value: i32,
    pub at_utc: i64,
}

/// WMO's thunderstorm codes.
fn thunder(code: u8) -> bool {
    (95..=99).contains(&code)
}

/// Thunder warns this far ahead, gusts this far.
const STORM_AHEAD_S: i64 = 2 * 3600;
const GUST_AHEAD_S: i64 = 3 * 3600;
/// Gusts that take an umbrella or a bin lid away (Beaufort 8, a gale).
const GUST_KMH: f32 = 60.0;
const HEAT_C: f32 = 35.0;
/// After this hour (local) a hot day is over.
const HEAT_UNTIL_H: i64 = 18;
const FROST_C: f32 = -1.0;
const FROST_AHEAD_S: i64 = 12 * 3600;
/// The European index's "poor" and "very poor"; the spell ends below "fair".
const AIR_POOR: u16 = 60;
const AIR_VERY_POOR: u16 = 80;
const AIR_CLEAN: u16 = 40;
/// Thunder and gales end their spell only after this long without them: a forecast that
/// flickers round the line doesn't warn every quarter hour.
const CALM_S: i64 = 3600;
/// Gusts under this are calm (the gale warns at 60).
const GUST_CALM_KMH: f32 = 50.0;

enum Thunder {
    Now,
    Ahead(i64),
    No,
}

/// Thunder now, starting within two hours (to the quarter when the answer has quarters, else
/// to the hour), or none.
fn thunder_of(w: &Weather, now: i64) -> Thunder {
    if thunder(w.code) {
        return Thunder::Now;
    }
    // Each quarter's code is the 15 minutes before its time; each hour's the hour from its own.
    let spans: Vec<(i64, i64, u8)> = if w.quarter_codes.is_empty() {
        w.hourly.iter().map(|(t, _, code, _)| (*t, t + 3600, *code)).collect()
    } else {
        w.quarter_codes.iter().map(|(t, code)| (t - 900, *t, *code)).collect()
    };
    match spans.iter().find(|(from, to, code)| *to > now && *from < now + STORM_AHEAD_S && thunder(*code)) {
        Some((from, ..)) if *from <= now => Thunder::Now,
        Some((from, ..)) => Thunder::Ahead(*from),
        None => Thunder::No,
    }
}

/// The strongest gust of the hours that start within three hours, and its hour.
fn strongest_gust(w: &Weather, now: i64) -> Option<(i64, f32)> {
    w.gusts.iter().filter(|(t, _)| t + 3600 > now && *t < now + GUST_AHEAD_S).max_by(|a, b| a.1.total_cmp(&b.1)).copied()
}

/// That gust (to five km/h), if it's a gale.
fn wind_of(w: &Weather, now: i64) -> Option<Alert> {
    let (at, kmh) = strongest_gust(w, now)?;
    (kmh >= GUST_KMH).then(|| Alert { kind: AlertKind::Wind, value: ((kmh / 5.0).round() * 5.0) as i32, at_utc: at })
}

/// The coldest of the next 12 hours, if it freezes and it doesn't yet.
fn frost_of(w: &Weather, now: i64) -> Option<Alert> {
    if w.temp <= 0.0 {
        return None;
    }
    let (at, c) = w
        .hourly
        .iter()
        .filter(|(t, ..)| t + 3600 > now && *t < now + FROST_AHEAD_S)
        .map(|(t, c, ..)| (*t, *c))
        .min_by(|a, b| a.1.total_cmp(&b.1))?;
    (c <= FROST_C).then(|| Alert { kind: AlertKind::Frost, value: c.round() as i32, at_utc: at })
}

/// Each warns once a spell (thunder and gales until an hour without them, the air until it is
/// clean again); a day's heat once a day, frost once a night.
#[derive(Clone, Copy, Debug, Default)]
pub struct AlertWatch {
    thunder: bool,
    wind: bool,
    air: bool,
    /// When thunder, and gusts of 50 km/h or more, were last in an answer.
    thunder_seen: Option<i64>,
    wind_seen: Option<i64>,
    /// The local day the heat warned on, and the night (noon to noon) the frost did.
    heat_day: Option<i64>,
    frost_night: Option<i64>,
}

impl AlertWatch {
    /// A new answer; the alerts to show now, the most pressing first.
    pub fn update(&mut self, w: &Weather, now_utc: i64) -> Vec<Alert> {
        let mut out = Vec::new();
        let calm = |seen: Option<i64>| seen.is_none_or(|t| now_utc - t >= CALM_S);
        match thunder_of(w, now_utc) {
            // Already on at the first look: this spell has no warning.
            Thunder::Now => {
                self.thunder = true;
                self.thunder_seen = Some(now_utc);
            }
            Thunder::Ahead(at) => {
                self.thunder_seen = Some(now_utc);
                if !self.thunder {
                    self.thunder = true;
                    out.push(Alert { kind: AlertKind::Storm, value: 0, at_utc: at });
                }
            }
            Thunder::No if calm(self.thunder_seen) => self.thunder = false,
            Thunder::No => {}
        }
        if strongest_gust(w, now_utc).is_some_and(|(_, kmh)| kmh >= GUST_CALM_KMH) {
            self.wind_seen = Some(now_utc);
        }
        match wind_of(w, now_utc) {
            Some(a) if !self.wind => {
                self.wind = true;
                out.push(a);
            }
            Some(_) => {}
            None if calm(self.wind_seen) => self.wind = false,
            None => {}
        }
        match (air_of(w, now_utc), w.aqi) {
            (Some(a), _) if !self.air => {
                self.air = true;
                out.push(a);
            }
            (None, Some(aqi)) if aqi < AIR_CLEAN => self.air = false,
            _ => {}
        }
        let day = (now_utc + w.utc_offset_s).div_euclid(86_400);
        if let Some(a) = heat_of(w, now_utc).filter(|_| self.heat_day != Some(day)) {
            self.heat_day = Some(day);
            out.push(a);
        }
        // Keyed by the frost's own night (noon to noon), so midnight doesn't warn of it again.
        if let Some(a) = frost_of(w, now_utc) {
            let night = (a.at_utc + w.utc_offset_s - 12 * 3600).div_euclid(86_400);
            if self.frost_night != Some(night) {
                self.frost_night = Some(night);
                out.push(a);
            }
        }
        out
    }
}

/// A hot day, until six in the evening.
fn heat_of(w: &Weather, now: i64) -> Option<Alert> {
    let hour = (now + w.utc_offset_s).rem_euclid(86_400) / 3600;
    (w.max >= HEAT_C && hour < HEAT_UNTIL_H).then(|| Alert { kind: AlertKind::Heat, value: w.max.round() as i32, at_utc: now })
}

fn air_of(w: &Weather, now: i64) -> Option<Alert> {
    w.aqi.filter(|a| *a >= AIR_POOR).map(|a| Alert { kind: AlertKind::Air, value: a as i32, at_utc: now })
}

/// What applies now, warned or not (the weather card's line), the most pressing first.
pub fn alerts_now(w: &Weather, now_utc: i64) -> Vec<Alert> {
    let storm = match thunder_of(w, now_utc) {
        Thunder::Now => Some(now_utc),
        Thunder::Ahead(at) => Some(at),
        Thunder::No => None,
    };
    let storm = storm.map(|at| Alert { kind: AlertKind::Storm, value: 0, at_utc: at });
    [storm, wind_of(w, now_utc), air_of(w, now_utc), heat_of(w, now_utc), frost_of(w, now_utc)].into_iter().flatten().collect()
}

/// The pill's words: ("Fırtına", "~30 dk içinde"), ("Kuvvetli rüzgâr", "65 km/sa · 21:00").
pub fn alert_text(a: &Alert, now_utc: i64, utc_offset_s: i64) -> (&'static str, String) {
    let hour = |t: i64| (t + utc_offset_s).rem_euclid(86_400) / 3600;
    match a.kind {
        AlertKind::Storm if a.at_utc <= now_utc => (tr("Fırtına"), tr("şu an").to_string()),
        AlertKind::Storm => (tr("Fırtına"), rain_when(((a.at_utc - now_utc) as u32).div_ceil(60))),
        AlertKind::Wind if a.at_utc <= now_utc => (tr("Kuvvetli rüzgâr"), trf("{} km/sa · şimdi", &[&a.value])),
        AlertKind::Wind => (tr("Kuvvetli rüzgâr"), trf("{} km/sa · {}", &[&a.value, &format!("{:02}:00", hour(a.at_utc))])),
        AlertKind::Heat => (tr("Aşırı sıcak"), trf("bugün {}°", &[&a.value])),
        AlertKind::Frost => {
            let when = match hour(a.at_utc) {
                6..=10 => trf("sabah {}°", &[&a.value]),
                11..=17 => trf("bugün {}°", &[&a.value]),
                _ => trf("gece {}°", &[&a.value]),
            };
            (tr("Don"), when)
        }
        AlertKind::Air if a.value >= AIR_VERY_POOR as i32 => (tr("Hava kalitesi çok kötü"), format!("AQI {}", a.value)),
        AlertKind::Air => (tr("Hava kalitesi kötü"), format!("AQI {}", a.value)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 21 Oct 2026 20:10 in İstanbul (UTC+3).
    const NOW: i64 = 1_792_602_600;
    const HOUR: i64 = NOW - NOW % 3600;
    const OFFSET: i64 = 10_800;

    /// A quiet evening: 12°, overcast, light wind, clean air, the next 12 hours.
    fn sky() -> Weather {
        sky_at(NOW)
    }

    /// The same quiet sky asked for at `now`: its hours and quarters from `now`'s hour.
    fn sky_at(now: i64) -> Weather {
        let hour = now - now % 3600;
        Weather {
            temp: 12.0,
            code: 3,
            is_day: false,
            hourly: (0..12).map(|i| (hour + i * 3600, 12.0, 3, false)).collect(),
            min: 8.0,
            max: 18.0,
            utc_offset_s: OFFSET,
            minutely: Vec::new(),
            gusts: (0..12).map(|i| (hour + i * 3600, 20.0)).collect(),
            quarter_codes: (0..8).map(|i| (hour + 900 * (i + 1), 3)).collect(),
            aqi: Some(25),
            fetched_utc: now,
        }
    }

    /// Thunder from 35 minutes past `now`'s hour (the quarter ending on the next hour).
    fn storm_at(now: i64) -> Weather {
        let mut w = sky_at(now);
        w.quarter_codes[3].1 = 95;
        w
    }

    fn kinds(alerts: &[Alert]) -> Vec<AlertKind> {
        alerts.iter().map(|a| a.kind).collect()
    }

    #[test]
    fn a_quiet_sky_warns_of_nothing() {
        assert!(AlertWatch::default().update(&sky(), NOW).is_empty());
    }

    #[test]
    fn thunder_warns_before_it_starts_once_a_spell() {
        // The quarter ending at HOUR + 3600 is the one from HOUR + 2700: 35 minutes away.
        let w = storm_at(NOW);
        let mut watch = AlertWatch::default();
        let alerts = watch.update(&w, NOW);
        assert_eq!(alerts, vec![Alert { kind: AlertKind::Storm, value: 0, at_utc: HOUR + 2700 }]);
        assert_eq!(alert_text(&alerts[0], NOW, OFFSET), ("Fırtına", "~35 dk içinde".to_string()));
        assert!(watch.update(&w, NOW + 600).is_empty(), "already warned");
        let mut now = sky_at(NOW + 3600);
        now.code = 96;
        assert!(watch.update(&now, NOW + 3600).is_empty(), "storming");
        // A forecast that flickers: one quiet answer isn't the end of the spell.
        assert!(watch.update(&sky_at(NOW + 4500), NOW + 4500).is_empty());
        assert!(watch.update(&storm_at(NOW + 7200), NOW + 7200).is_empty(), "the same spell");
        // An hour without thunder ends it; the next warns again.
        assert!(watch.update(&sky_at(NOW + 10_800), NOW + 10_800).is_empty());
        assert_eq!(kinds(&watch.update(&storm_at(NOW + 14_400), NOW + 14_400)), vec![AlertKind::Storm]);
    }

    #[test]
    fn thunder_already_on_at_the_first_look_doesnt_warn() {
        let mut w = sky();
        w.code = 95;
        w.quarter_codes[2].1 = 95;
        let mut watch = AlertWatch::default();
        assert!(watch.update(&w, NOW).is_empty());
        w.code = 3;
        assert!(watch.update(&w, NOW + 300).is_empty(), "the same spell");
    }

    #[test]
    fn thunder_from_the_hours_without_quarters_and_not_too_far_ahead() {
        let mut w = sky();
        w.quarter_codes.clear();
        w.hourly[1].2 = 95;
        assert_eq!(AlertWatch::default().update(&w, NOW), vec![Alert { kind: AlertKind::Storm, value: 0, at_utc: HOUR + 3600 }]);
        let mut far = sky();
        far.quarter_codes.clear();
        far.hourly[3].2 = 99;
        assert!(AlertWatch::default().update(&far, NOW).is_empty(), "three hours on: not yet");
    }

    #[test]
    fn strong_gusts_within_three_hours() {
        let mut w = sky();
        w.gusts[1].1 = 64.8;
        w.gusts[2].1 = 58.0;
        let mut watch = AlertWatch::default();
        let alerts = watch.update(&w, NOW);
        assert_eq!(alerts, vec![Alert { kind: AlertKind::Wind, value: 65, at_utc: HOUR + 3600 }]);
        assert_eq!(alert_text(&alerts[0], NOW, OFFSET), ("Kuvvetli rüzgâr", "65 km/sa · 21:00".to_string()));
        assert!(watch.update(&w, NOW + 900).is_empty(), "once a spell");
        let mut now = sky();
        now.gusts[0].1 = 72.0;
        let first = AlertWatch::default().update(&now, NOW);
        assert_eq!(alert_text(&first[0], NOW, OFFSET).1, "70 km/sa · şimdi", "to five, and the hour under way");
        let mut later = sky();
        later.gusts[4].1 = 90.0;
        assert!(AlertWatch::default().update(&later, NOW).is_empty(), "midnight: past three hours");
    }

    #[test]
    fn a_gale_warns_again_only_after_an_hour_of_calm() {
        let gust = |now: i64, kmh: f32| {
            let mut w = sky_at(now);
            w.gusts[1].1 = kmh;
            w
        };
        let mut watch = AlertWatch::default();
        assert_eq!(kinds(&watch.update(&gust(NOW, 64.0), NOW)), vec![AlertKind::Wind]);
        assert!(watch.update(&gust(NOW + 900, 55.0), NOW + 900).is_empty(), "still windy");
        assert!(watch.update(&gust(NOW + 1800, 20.0), NOW + 1800).is_empty());
        assert!(watch.update(&gust(NOW + 2700, 61.0), NOW + 2700).is_empty(), "the same spell, flickering round 60");
        assert!(watch.update(&gust(NOW + 7200, 20.0), NOW + 7200).is_empty());
        assert_eq!(kinds(&watch.update(&gust(NOW + 8100, 66.0), NOW + 8100)), vec![AlertKind::Wind], "an hour of calm since");
    }

    #[test]
    fn frost_warns_once_for_its_night_across_midnight() {
        let frost = |now: i64, at: i64| {
            let mut w = sky_at(now);
            w.temp = 3.0;
            for h in w.hourly.iter_mut().filter(|h| h.0 == at) {
                h.1 = -2.0;
            }
            w
        };
        // 20:10: frost at 07:00.
        let seven = HOUR + 11 * 3600;
        let mut watch = AlertWatch::default();
        assert_eq!(kinds(&watch.update(&frost(NOW, seven), NOW)), vec![AlertKind::Frost]);
        assert!(watch.update(&frost(NOW + 4 * 3600, seven), NOW + 4 * 3600).is_empty(), "00:10: the same night");
        assert_eq!(kinds(&watch.update(&frost(NOW + 86_400, seven + 86_400), NOW + 86_400)), vec![AlertKind::Frost], "the next night");
    }

    #[test]
    fn a_hot_day_once_a_day_before_six_in_the_evening() {
        let mut w = sky();
        w.max = 36.4;
        // 11:00 local.
        let morning = NOW - 9 * 3600 - 600;
        let mut watch = AlertWatch::default();
        let alerts = watch.update(&w, morning);
        assert_eq!(kinds(&alerts), vec![AlertKind::Heat]);
        assert_eq!(alert_text(&alerts[0], morning, OFFSET), ("Aşırı sıcak", "bugün 36°".to_string()));
        assert!(watch.update(&w, morning + 3600).is_empty(), "once a day");
        assert_eq!(kinds(&watch.update(&w, morning + 86_400)), vec![AlertKind::Heat], "and again the next");
        assert!(AlertWatch::default().update(&w, NOW).is_empty(), "20:10: the day is done");
    }

    #[test]
    fn frost_ahead_once_a_day_unless_it_already_freezes() {
        let mut w = sky();
        w.temp = 3.0;
        for (i, h) in w.hourly.iter_mut().enumerate() {
            h.1 = 3.0 - i as f32;
        }
        let mut watch = AlertWatch::default();
        let alerts = watch.update(&w, NOW);
        // Down to -8 eleven hours on (07:00 local).
        assert_eq!(alerts, vec![Alert { kind: AlertKind::Frost, value: -8, at_utc: HOUR + 11 * 3600 }]);
        assert_eq!(alert_text(&alerts[0], NOW, OFFSET), ("Don", "sabah -8°".to_string()));
        assert!(watch.update(&w, NOW + 1800).is_empty(), "once a day");
        let mut night = sky();
        night.temp = 2.0;
        night.hourly[4].1 = -1.4;
        assert_eq!(alert_text(&AlertWatch::default().update(&night, NOW)[0], NOW, OFFSET).1, "gece -1°");
        let mut frozen = w.clone();
        frozen.temp = -1.0;
        assert!(AlertWatch::default().update(&frozen, NOW).is_empty(), "already freezing: no news");
    }

    #[test]
    fn bad_air_once_until_it_clears() {
        let air = |aqi| Weather { aqi: Some(aqi), ..sky() };
        let mut watch = AlertWatch::default();
        let alerts = watch.update(&air(72), NOW);
        assert_eq!(alerts, vec![Alert { kind: AlertKind::Air, value: 72, at_utc: NOW }]);
        assert_eq!(alert_text(&alerts[0], NOW, OFFSET), ("Hava kalitesi kötü", "AQI 72".to_string()));
        assert!(watch.update(&air(85), NOW + 900).is_empty());
        assert!(watch.update(&air(45), NOW + 1800).is_empty(), "better, not clean");
        assert!(watch.update(&air(65), NOW + 2700).is_empty(), "the same spell");
        assert!(watch.update(&air(30), NOW + 3600).is_empty());
        let again = watch.update(&air(88), NOW + 4500);
        assert_eq!(alert_text(&again[0], NOW, OFFSET), ("Hava kalitesi çok kötü", "AQI 88".to_string()));
        assert!(AlertWatch::default().update(&Weather { aqi: None, ..sky() }, NOW).is_empty(), "no answer for the air");
    }

    #[test]
    fn the_card_shows_what_applies_now_whatever_was_warned() {
        let mut w = sky();
        w.code = 95;
        w.gusts[0].1 = 66.0;
        let now = alerts_now(&w, NOW);
        assert_eq!(kinds(&now), vec![AlertKind::Storm, AlertKind::Wind]);
        assert_eq!(alert_text(&now[0], NOW, OFFSET), ("Fırtına", "şu an".to_string()));
        assert!(alerts_now(&sky(), NOW).is_empty());
        let mut later = sky();
        later.quarter_codes[5].1 = 95;
        assert_eq!(alert_text(&alerts_now(&later, NOW)[0], NOW, OFFSET).1, "~1 saat içinde");
    }

    #[test]
    fn several_at_once_the_most_pressing_first() {
        let mut w = sky();
        w.temp = 3.0;
        w.hourly[5].1 = -2.0;
        w.aqi = Some(70);
        w.gusts[0].1 = 61.0;
        w.quarter_codes[1].1 = 95;
        let alerts = AlertWatch::default().update(&w, NOW);
        assert_eq!(kinds(&alerts), vec![AlertKind::Storm, AlertKind::Wind, AlertKind::Air, AlertKind::Frost]);
    }
}
