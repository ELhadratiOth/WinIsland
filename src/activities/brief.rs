//! Now Brief: the morning card's choices and words. The pure part; the app opens it and the
//! card draws it. "Local" times are unix seconds plus the PC's offset from UTC.

use super::basketball::period_text;
use super::football::{civil_from_days, minute_text, MatchSnapshot, MatchStatus};
use super::weather::{fx_of, WeatherFx};
use crate::i18n::{n_, tr, trc, trf};

/// The day starts here: an unlock at 2 am is still last night.
const DAY_STARTS_H: i64 = 5;
/// A result is news this long after kick-off (the workers keep matches a little longer).
const RESULTS_FOR_S: i64 = 20 * 3600;

/// The greeting for a local hour.
pub fn greeting(hour: u32) -> &'static str {
    match hour {
        5..=11 => tr("Günaydın"),
        12..=17 => tr("İyi günler"),
        18..=22 => tr("İyi akşamlar"),
        _ => tr("İyi geceler"),
    }
}

/// "Perşembe, 22 Ekim".
pub fn date_line(local: i64) -> String {
    const DAYS: [&str; 7] = [n_("Pazartesi"), n_("Salı"), n_("Çarşamba"), n_("Perşembe"), n_("Cuma"), n_("Cumartesi"), n_("Pazar")];
    const MONTHS: [&str; 12] = [
        n_("Ocak"), n_("Şubat"), n_("Mart"), n_("Nisan"), n_("Mayıs"), n_("Haziran"), n_("Temmuz"), n_("Ağustos"), n_("Eylül"), n_("Ekim"), n_("Kasım"), n_("Aralık"),
    ];
    let days = local.div_euclid(86_400);
    let (_, month, day) = civil_from_days(days);
    // 1 January 1970 was a Thursday (index 3).
    format!("{}, {day} {}", tr(DAYS[(days + 3).rem_euclid(7) as usize]), tr(MONTHS[(month - 1) as usize]))
}

/// "2026-10-22": the day a brief was shown.
pub fn day_key(local: i64) -> String {
    let (y, m, d) = civil_from_days(local.div_euclid(86_400));
    format!("{y:04}-{m:02}-{d:02}")
}

/// Whether the day's brief is still to come: the day starts at 05:00, and `last` is the day the
/// last one was shown.
pub fn due(last: Option<&str>, local: i64) -> bool {
    local.rem_euclid(86_400) / 3600 >= DAY_STARTS_H && last != Some(day_key(local).as_str())
}

/// The first word of a Windows full name ("Ekin Yılmaz" → "Ekin").
pub fn first_name(full: &str) -> Option<String> {
    full.split_whitespace().next().map(str::to_string)
}

/// An automatic open needs the setting, a due day, no fullscreen app and no card already open.
pub fn should_auto_open(enabled: bool, due: bool, fullscreen: bool, card_open: bool) -> bool {
    enabled && due && !fullscreen && !card_open
}

/// The first hour later today (local) with rain or snow: "15:00 civarı yağmur". `hourly` is
/// the forecast's (unix time, temperature, code, day) from the current hour on.
pub fn rain_later(hourly: &[(i64, f32, u8, bool)], now_utc: i64, offset_s: i64) -> Option<String> {
    let today = (now_utc + offset_s).div_euclid(86_400);
    let (t, _, code, day) = hourly.iter().find(|(t, _, code, day)| {
        *t > now_utc && (t + offset_s).div_euclid(86_400) == today && matches!(fx_of(*code, *day), WeatherFx::Rain | WeatherFx::Storm | WeatherFx::Snow)
    })?;
    let what = if fx_of(*code, *day) == WeatherFx::Snow { tr("kar") } else { tr("yağmur") };
    Some(trf("{} civarı {}", &[&format!("{:02}:00", (t + offset_s).rem_euclid(86_400) / 3600), &what]))
}

/// One match row of the brief: which snapshot, and what its right side says.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BriefMatch {
    pub id: String,
    pub status: String,
    pub live: bool,
}

/// The followed teams' matches worth a morning look, at most three: live ones, today's still to
/// come by kick-off, then results of the last 20 hours, newest first. `now_ms` is the tick the
/// live minute counts from.
pub fn pick_matches(matches: &[MatchSnapshot], now_utc: i64, now_ms: u64, offset_s: i64, hidden: &[String]) -> Vec<BriefMatch> {
    let day = |utc: i64| (utc + offset_s).div_euclid(86_400);
    let today = day(now_utc);
    // (group, kick-off order, row): live first, then today's to come, then results newest first.
    let mut rows: Vec<(u8, i64, BriefMatch)> = matches
        .iter()
        .filter(|m| !hidden.contains(&m.id) && !m.status.is_off())
        .filter_map(|m| {
            let row = |status: String, live| BriefMatch { id: m.id.clone(), status, live };
            if m.status.is_live() {
                let at = match (&m.period, &m.clock, m.status) {
                    (Some((q, left)), _, _) => period_text(*q, left),
                    (_, _, MatchStatus::HalfTime) => tr("Devre arası").into(),
                    (_, Some(c), _) => minute_text(c, now_ms),
                    _ => return Some((0, m.kickoff_utc, row(tr("Canlı").into(), true))),
                };
                Some((0, m.kickoff_utc, row(trf("{} · Canlı", &[&at]), true)))
            } else if m.status.is_finished() {
                let label = if day(m.kickoff_utc) == today { trc("maç", "Bitti") } else { tr("Dün") };
                (now_utc - m.kickoff_utc <= RESULTS_FOR_S).then(|| (2, -m.kickoff_utc, row(format!("{}–{} · {label}", m.score.0, m.score.1), false)))
            } else {
                let local = m.kickoff_utc + offset_s;
                let at = if m.time_known { format!("{:02}:{:02}", local.rem_euclid(86_400) / 3600, local.rem_euclid(3600) / 60) } else { tr("Bugün").into() };
                (day(m.kickoff_utc) == today).then(|| (1, m.kickoff_utc, row(format!("{at} · {}", m.competition), false)))
            }
        })
        .collect();
    rows.sort_by_key(|(group, order, _)| (*group, *order));
    rows.into_iter().take(3).map(|(_, _, row)| row).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    // Local times (unix seconds as if the PC's clock were UTC).
    const OCT22_0200: i64 = 1_792_634_400;
    const OCT22_0500: i64 = 1_792_645_200;
    const OCT22_0800: i64 = 1_792_656_000;
    const OCT22_2000: i64 = 1_792_699_200;
    const JAN4_0900: i64 = 1_767_517_200;

    #[test]
    fn greeting_by_hour() {
        let at = |h| greeting(h);
        assert_eq!((at(4), at(5), at(11)), ("Good night", "Good morning", "Good morning"));
        assert_eq!((at(12), at(17)), ("Good afternoon", "Good afternoon"));
        assert_eq!((at(18), at(22), at(23)), ("Good evening", "Good evening", "Good night"));
    }

    #[test]
    fn date_in_full_english() {
        assert_eq!(date_line(OCT22_0800), "Thursday, 22 October");
        assert_eq!(date_line(JAN4_0900), "Sunday, 4 January");
    }

    #[test]
    fn the_day_starts_at_five() {
        assert_eq!(day_key(OCT22_0800), "2026-10-22");
        assert!(due(None, OCT22_0800), "never shown");
        assert!(!due(Some("2026-10-22"), OCT22_2000), "once a day");
        assert!(!due(Some("2026-10-21"), OCT22_0200), "2 am is still last night");
        assert!(due(Some("2026-10-21"), OCT22_0500));
    }

    #[test]
    fn first_word_of_the_full_name() {
        assert_eq!(first_name("Ekin Yılmaz").as_deref(), Some("Ekin"));
        assert_eq!(first_name("  Ekin ").as_deref(), Some("Ekin"));
        assert_eq!(first_name(""), None);
        assert_eq!(first_name("   "), None);
    }

    #[test]
    fn auto_open_only_when_free() {
        assert!(should_auto_open(true, true, false, false));
        assert!(!should_auto_open(false, true, false, false), "setting off");
        assert!(!should_auto_open(true, false, false, false), "already shown today");
        assert!(!should_auto_open(true, true, true, false), "a fullscreen app");
        assert!(!should_auto_open(true, true, false, true), "a card is open");
    }

    #[test]
    fn rain_later_is_today_only() {
        const TR: i64 = 3 * 3600;
        // 13:00 local on 22 October, in UTC.
        let h13 = OCT22_0800 + 5 * 3600 - TR;
        let now = h13 + 20 * 60;
        let hour = |t: i64, code: u8| (t, 12.0, code, true);
        let mut hours = vec![hour(h13, 61), hour(h13 + 3600, 0), hour(h13 + 7200, 61), hour(h13 + 12 * 3600, 73)];
        assert_eq!(rain_later(&hours, now, TR).as_deref(), Some("15:00 civarı yağmur"), "not the hour it already is");
        hours[2].2 = 0;
        assert_eq!(rain_later(&hours, now, TR), None, "tomorrow's snow is tomorrow's");
        hours[2].2 = 73;
        assert_eq!(rain_later(&hours, now, TR).as_deref(), Some("15:00 civarı kar"));
        hours[2].2 = 95;
        assert_eq!(rain_later(&hours, now, TR).as_deref(), Some("15:00 civarı yağmur"), "a storm rains");
    }

    mod matches {
        use super::super::*;
        use crate::activities::football::{Clock, MatchStatus, Team};

        const TR: i64 = 3 * 3600;
        // 09:00 local on 22 October.
        const NOW_LOCAL: i64 = super::OCT22_0800 + 3600;
        const NOW: i64 = NOW_LOCAL - TR;
        const TICK: u64 = 5_000_000;

        fn team(code: &str) -> Team {
            Team { id: code.into(), name: code.into(), short: code.into(), code: code.into(), colors: None, logo: None }
        }

        fn m(id: &str, kickoff_local: i64, status: MatchStatus, score: (u8, u8)) -> MatchSnapshot {
            MatchSnapshot {
                id: id.into(),
                league: "tur.1".into(),
                competition: "Süper Lig".into(),
                kickoff_utc: kickoff_local - TR,
                time_known: true,
                status,
                clock: None,
                home: team("FB"),
                away: team("GS"),
                score,
                pens: None,
                events: vec![],
                url: None,
                followed: None,
                venue: None,
                form: None,
                ended_utc: None,
                period: None,
                quarters: None,
            }
        }

        fn ids(v: &[BriefMatch]) -> Vec<(&str, &str)> {
            v.iter().map(|b| (b.id.as_str(), b.status.as_str())).collect()
        }

        #[test]
        fn brief_picks_live_today_then_results() {
            let mut live = m("live", NOW_LOCAL - 3600, MatchStatus::Live2, (1, 0));
            live.clock = Some(Clock { minute: 65, added: None, as_of_ms: TICK, cap: 90 });
            let all = vec![
                m("up20", NOW_LOCAL + 11 * 3600, MatchStatus::Scheduled, (0, 0)),
                m("yest", NOW_LOCAL - 9 * 3600 - 1800, MatchStatus::FullTime, (2, 1)),
                live,
                m("old", NOW_LOCAL - 21 * 3600, MatchStatus::FullTime, (0, 0)),
                m("up18", NOW_LOCAL + 9 * 3600, MatchStatus::Scheduled, (0, 0)),
                m("hid", NOW_LOCAL + 10 * 3600, MatchStatus::Scheduled, (0, 0)),
            ];
            let hidden = vec!["hid".to_string()];
            assert_eq!(
                ids(&pick_matches(&all, NOW, TICK, TR, &hidden)),
                vec![("live", "65′ · Canlı"), ("up18", "18:00 · Süper Lig"), ("up20", "20:00 · Süper Lig")]
            );
            assert!(pick_matches(&all, NOW, TICK, TR, &hidden)[0].live);
            let results: Vec<MatchSnapshot> = all.iter().filter(|x| x.status == MatchStatus::FullTime).cloned().collect();
            assert_eq!(ids(&pick_matches(&results, NOW, TICK, TR, &hidden)), vec![("yest", "2–1 · Dün")], "21 h ago is too old");
            let today = vec![m("early", NOW_LOCAL - 3 * 3600, MatchStatus::FullTime, (1, 0))];
            assert_eq!(ids(&pick_matches(&today, NOW, TICK, TR, &[])), vec![("early", "1–0 · Bitti")]);
            let off = vec![m("pp", NOW_LOCAL + 3600, MatchStatus::Postponed, (0, 0))];
            assert!(pick_matches(&off, NOW, TICK, TR, &[]).is_empty(), "called off");
        }
    }
}
