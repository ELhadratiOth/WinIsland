//! EuroLeague and EuroCup basketball, from the leagues' own public feeds: a followed club's
//! games from feeds.incrowdsports.com (names, crests, kick-off, results, quarter scores) and the
//! live score from live.euroleague.net's game header (quarter, time left). The pure part;
//! basketball_worker.rs polls. A game is a MatchSnapshot like a football match, with a period
//! (quarter, time left) and quarter scores instead of a minute and goals.

use serde_json::Value;

use crate::i18n::{tr, trf};

use super::football::{civil_from_days, parse_kickoff, MatchSnapshot, MatchStatus, Side, Team, TeamHit};

/// The competitions: feed code, our league slug, the name shown.
pub const LEAGUES: [(char, &str, &str); 2] = [('E', "euroleague", "EuroLeague"), ('U', "eurocup", "EuroCup")];

pub fn is_basketball(league: &str) -> bool {
    LEAGUES.iter().any(|(_, slug, _)| *slug == league)
}

/// Our id for a club in a competition: "bb-E-ULK" (a file name too: its crest's).
pub fn team_id(comp: char, code: &str) -> String {
    format!("bb-{comp}-{code}")
}

/// (competition, club code) of one of our ids.
pub fn parse_team_id(id: &str) -> Option<(char, String)> {
    let (comp, code) = id.strip_prefix("bb-")?.split_once('-')?;
    let mut c = comp.chars();
    match (c.next(), c.next()) {
        (Some(comp), None) if !code.is_empty() => Some((comp, code.to_string())),
        _ => None,
    }
}

fn league_of(comp: char) -> (&'static str, &'static str) {
    LEAGUES.iter().find(|(c, _, _)| *c == comp).map_or(("euroleague", "EuroLeague"), |(_, slug, name)| (*slug, *name))
}

/// The season code at `now_utc`: seasons start in July ("E2026" for 2026-27).
pub fn season_code(comp: char, now_utc: i64) -> String {
    let (year, month, _) = civil_from_days(now_utc.div_euclid(86_400));
    format!("{comp}{}", if month >= 7 { year } else { year - 1 })
}

pub fn games_url(comp: char, season: &str, code: &str) -> String {
    format!("https://feeds.incrowdsports.com/provider/euroleague-feeds/v2/competitions/{comp}/seasons/{season}/games?teamCode={code}")
}

pub fn clubs_url(comp: char, season: &str) -> String {
    format!("https://feeds.incrowdsports.com/provider/euroleague-feeds/v2/competitions/{comp}/seasons/{season}/clubs")
}

pub fn header_url(season: &str, game: u32) -> String {
    format!("https://live.euroleague.net/api/Header?gamecode={game}&seasoncode={season}")
}

/// The games of a club's season list (`/games?teamCode=`), as snapshots.
pub fn parse_games(v: &Value, comp: char, followed: &[String]) -> Vec<MatchSnapshot> {
    v["data"].as_array().map(|games| games.iter().filter_map(|g| parse_game(g, comp, followed)).collect()).unwrap_or_default()
}

fn parse_game(g: &Value, comp: char, followed: &[String]) -> Option<MatchSnapshot> {
    let (league, competition) = league_of(comp);
    let team = |t: &Value| -> Option<Team> {
        let code = t["code"].as_str()?;
        let name = turkish(t["editorialName"].as_str().or(t["abbreviatedName"].as_str()).unwrap_or(code));
        Some(Team {
            id: team_id(comp, code),
            short: name.clone(),
            name,
            code: t["tla"].as_str().unwrap_or(code).to_string(),
            colors: None,
            logo: t["imageUrls"]["crest"].as_str().map(str::to_string),
        })
    };
    let (home, away) = (team(&g["home"])?, team(&g["away"])?);
    let status = match g["status"].as_str() {
        Some("result") => MatchStatus::FullTime,
        Some("live" | "playing" | "inProgress" | "in_progress") => MatchStatus::Live1,
        _ => MatchStatus::Scheduled,
    };
    let points = |t: &Value| t["score"].as_u64().unwrap_or(0).min(255) as u8;
    let per_quarter = |t: &Value| -> Vec<u8> {
        ["q1", "q2", "q3", "q4", "ot1", "ot2", "ot3", "ot4", "ot5"].iter().filter_map(|q| t["quarters"][q].as_u64()).map(|n| n.min(255) as u8).collect()
    };
    let quarters = (status != MatchStatus::Scheduled).then(|| (per_quarter(&g["home"]), per_quarter(&g["away"])));
    let followed = if followed.contains(&home.id) {
        Some(Side::Home)
    } else if followed.contains(&away.id) {
        Some(Side::Away)
    } else {
        None
    };
    Some(MatchSnapshot {
        id: format!("bb-{}-{}", g["season"]["code"].as_str()?, g["code"].as_u64()?),
        league: league.into(),
        competition: competition.into(),
        kickoff_utc: g["date"].as_str().and_then(parse_kickoff)?,
        time_known: g["confirmedTime"].as_bool().unwrap_or(true),
        status,
        clock: None,
        score: (points(&g["home"]), points(&g["away"])),
        home,
        away,
        pens: None,
        events: Vec::new(),
        url: None,
        followed,
        venue: g["venue"]["name"].as_str().filter(|v| !v.trim().is_empty()).map(title_case),
        form: None,
        ended_utc: None,
        period: None,
        quarters,
    })
}

/// The season code and game number behind one of our game ids ("bb-E2026-70").
pub fn game_of(id: &str) -> Option<(String, u32)> {
    let (season, game) = id.strip_prefix("bb-")?.split_once('-')?;
    Some((season.to_string(), game.parse().ok()?))
}

/// Brings a game up to date from its live header: score, quarter and time left, quarter scores,
/// and whether it's on or over.
pub fn apply_header(snap: &mut MatchSnapshot, v: &Value) {
    let num = |k: &str| v[k].as_u64().map(|n| n as u32).or_else(|| v[k].as_str().and_then(|s| s.trim().parse().ok()));
    let (a, b) = (num("ScoreA").unwrap_or(0), num("ScoreB").unwrap_or(0));
    let game_minutes: u32 = v["GameTime"].as_str().and_then(|t| t.split(':').next()).and_then(|m| m.trim().parse().ok()).unwrap_or(0);
    // "Live" may drop between quarters: under 40 minutes played, a started game is still on.
    let live = v["Live"].as_bool().unwrap_or(false) || (a + b > 0 && game_minutes < 40);
    if !live && a + b == 0 {
        return;
    }
    let quarter = quarter_of(v["Quarter"].as_str().unwrap_or("")).unwrap_or(if live { (game_minutes / 10 + 1).min(4) as u8 } else { 4 });
    let left = v["RemainingPartialTime"].as_str().unwrap_or("").trim().to_string();
    snap.score = (a.min(255) as u8, b.min(255) as u8);
    snap.status = if live { MatchStatus::Live1 } else { MatchStatus::FullTime };
    snap.period = live.then_some((quarter, left));
    // The header's quarter scores are running totals.
    let played = if live { quarter.min(4) as usize } else { 4 };
    let side = |s: char| -> Vec<u8> {
        let totals: Vec<u32> = (1..=4).map(|i| num(&format!("ScoreQuarter{i}{s}")).unwrap_or(0)).collect();
        let mut per: Vec<u8> = (0..played).map(|i| (totals[i].saturating_sub(if i == 0 { 0 } else { totals[i - 1] })).min(255) as u8).collect();
        let extra = num(&format!("ScoreExtraTime{s}")).unwrap_or(0);
        if extra > 0 && (!live || quarter > 4) {
            per.push((if extra >= totals[3] && totals[3] > 0 { extra - totals[3] } else { extra }).min(255) as u8);
        }
        per
    };
    snap.quarters = Some((side('A'), side('B')));
}

/// "Q3" → 3, "OT" / "OT2" → 5 / 6 (the header's quarter; empty before and after a game).
fn quarter_of(s: &str) -> Option<u8> {
    let s = s.trim().to_ascii_uppercase();
    let n: Option<u8> = s.chars().filter(char::is_ascii_digit).collect::<String>().parse().ok();
    if s.contains("OT") || s.starts_with('E') {
        Some(4 + n.unwrap_or(1))
    } else {
        n.filter(|q| (1..=4).contains(q))
    }
}

/// The pill's centre while a game is on: "Ç3 04:12", "DA" at half time, "UZ 02:10" in overtime.
pub fn period_text(quarter: u8, left: &str) -> String {
    let ended = left == "00:00";
    match quarter {
        2 if ended => tr("DA").into(),
        1..=4 if ended => trf("Ç{} sonu", &[&quarter]),
        1..=4 => trf("Ç{} {}", &[&quarter, &left]),
        _ => {
            let n = quarter - 4;
            let ot = if n > 1 { trf("UZ{}", &[&n]) } else { tr("UZ").into() };
            if ended { trf("{} sonu", &[&ot]) } else { format!("{ot} {left}") }
        }
    }
}

/// The card's capsule while a game is on: "3. çeyrek · 04:12", "Devre arası", "Uzatma · 02:10".
pub fn period_capsule(quarter: u8, left: &str) -> String {
    let ended = left == "00:00";
    match quarter {
        2 if ended => tr("Devre arası").into(),
        1..=4 if ended => trf("{}. çeyrek sonu", &[&quarter]),
        1..=4 => trf("{}. çeyrek · {}", &[&quarter, &left]),
        _ => {
            let n = quarter - 4;
            let ot = if n > 1 { trf("{}. uzatma", &[&n]) } else { tr("Uzatma").into() };
            if ended { trf("{} sonu", &[&ot]) } else { format!("{ot} · {left}") }
        }
    }
}

/// The clubs of a competition (`/clubs`), as search results.
pub fn parse_clubs(v: &Value, comp: char) -> Vec<TeamHit> {
    let (league, _) = league_of(comp);
    let Some(clubs) = v["data"].as_array() else { return Vec::new() };
    clubs
        .iter()
        .filter_map(|c| {
            let code = c["code"].as_str()?;
            Some(TeamHit {
                id: team_id(comp, code),
                name: turkish(c["name"].as_str().unwrap_or(code)),
                league: league.into(),
                abbr: c["tvCode"].as_str().unwrap_or(code).to_string(),
                colors: None,
                logo: c["images"]["crest"].as_str().map(str::to_string),
            })
        })
        .collect()
}

/// The clubs whose name holds `query`, Turkish letters or not ("besiktas" finds "Beşiktaş").
pub fn search_clubs(clubs: &[TeamHit], query: &str) -> Vec<TeamHit> {
    let q = fold(query.trim());
    if q.chars().count() < 2 {
        return Vec::new();
    }
    clubs.iter().filter(|c| fold(&c.name).contains(&q)).cloned().collect()
}

/// The feeds spell Turkish clubs in ASCII.
fn turkish(name: &str) -> String {
    const FIXES: [(&str, &str); 10] = [
        ("Fenerbahce", "Fenerbahçe"),
        ("Besiktas", "Beşiktaş"),
        ("Karsiyaka", "Karşıyaka"),
        ("Bahcesehir", "Bahçeşehir"),
        ("Turk Telekom", "Türk Telekom"),
        ("Tofas", "Tofaş"),
        ("Darussafaka", "Darüşşafaka"),
        ("Buyukcekmece", "Büyükçekmece"),
        ("Pinar", "Pınar"),
        ("Istanbul", "İstanbul"),
    ];
    FIXES.iter().fold(name.trim().to_string(), |n, (from, to)| n.replace(from, to))
}

/// Lower case without Turkish letters, for matching.
fn fold(s: &str) -> String {
    s.chars()
        .flat_map(char::to_lowercase)
        .filter(|c| *c != '\u{307}')
        .map(|c| match c {
            'ç' => 'c',
            'ş' => 's',
            'ğ' => 'g',
            'ı' => 'i',
            'ö' => 'o',
            'ü' => 'u',
            other => other,
        })
        .collect()
}

/// "ULKER SPORTS AND EVENT HALL" → "Ulker Sports And Event Hall".
fn title_case(s: &str) -> String {
    s.split_whitespace()
        .map(|w| {
            let mut c = w.chars();
            c.next().map(|f| f.to_uppercase().chain(c.flat_map(char::to_lowercase)).collect::<String>()).unwrap_or_default()
        })
        .collect::<Vec<_>>()
        .join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Two of Fenerbahçe Beko's games from the real feed (trimmed): a result and the next one.
    const GAMES: &str = r#"{"status":"success","data":[
      {"code":54,"season":{"code":"E2026"},"date":"2026-10-15T17:45:00.000Z","status":"result","confirmedTime":true,
       "home":{"code":"ULK","name":"Fenerbahce Beko Istanbul","abbreviatedName":"Fenerbahce","tla":"FBT","score":74,
               "quarters":{"q1":20,"q2":18,"q3":19,"q4":17,"ot1":null},"imageUrls":{"crest":"https://media-cdn.cortextech.io/fb.png"},"editorialName":"Fenerbahce"},
       "away":{"code":"PAR","name":"Partizan Mozzart Bet Belgrade","abbreviatedName":"Partizan","tla":"PAR","score":65,
               "quarters":{"q1":15,"q2":17,"q3":16,"q4":17,"ot1":null},"imageUrls":{"crest":"https://media-cdn.cortextech.io/par.png"},"editorialName":"Partizan"},
       "venue":{"name":"ULKER SPORTS AND EVENT HALL"}},
      {"code":70,"season":{"code":"E2026"},"date":"2026-10-23T18:15:00.000Z","status":"confirmed","confirmedTime":true,
       "home":{"code":"OLY","name":"Olympiacos Piraeus","abbreviatedName":"Olympiacos","tla":"OLY","score":0,
               "quarters":{"q1":null,"q2":null,"q3":null,"q4":null},"imageUrls":{"crest":"https://media-cdn.incrowdsports.com/oly.png"},"editorialName":"Olympiacos"},
       "away":{"code":"ULK","name":"Fenerbahce Beko Istanbul","abbreviatedName":"Fenerbahce","tla":"FBT","score":0,
               "quarters":{"q1":null,"q2":null,"q3":null,"q4":null},"imageUrls":{"crest":"https://media-cdn.cortextech.io/fb.png"},"editorialName":"Fenerbahce"},
       "venue":{"name":"PEACE AND FRIENDSHIP STADIUM"}}
    ]}"#;

    /// live.euroleague.net's header during a game (the fields it has after a game, filled in as
    /// they read while one is on; the quarter scores add up).
    const LIVE: &str = r#"{"Live":true,"ScoreA":"58","ScoreB":"61","Quarter":"Q3","RemainingPartialTime":"04:12","GameTime":"25:48",
        "ScoreQuarter1A":20,"ScoreQuarter2A":38,"ScoreQuarter3A":58,"ScoreQuarter4A":0,"ScoreExtraTimeA":0,
        "ScoreQuarter1B":15,"ScoreQuarter2B":40,"ScoreQuarter3B":61,"ScoreQuarter4B":0,"ScoreExtraTimeB":0}"#;
    /// The real header after the 2026 Final Four semi-final.
    const OVER: &str = r#"{"Live":false,"ScoreA":"79","ScoreB":"61","Quarter":"","RemainingPartialTime":"00:00","GameTime":"40:00",
        "ScoreQuarter1A":18,"ScoreQuarter2A":33,"ScoreQuarter3A":56,"ScoreQuarter4A":79,"ScoreExtraTimeA":0,
        "ScoreQuarter1B":12,"ScoreQuarter2B":24,"ScoreQuarter3B":41,"ScoreQuarter4B":61,"ScoreExtraTimeB":0}"#;

    fn games() -> Vec<MatchSnapshot> {
        parse_games(&serde_json::from_str(GAMES).unwrap(), 'E', &["bb-E-ULK".to_string()])
    }

    #[test]
    fn games_become_snapshots_with_turkish_names() {
        let g = games();
        assert_eq!(g.len(), 2);
        let (done, next) = (&g[0], &g[1]);
        assert_eq!(done.id, "bb-E2026-54");
        assert_eq!((done.league.as_str(), done.competition.as_str()), ("euroleague", "EuroLeague"));
        assert_eq!(done.status, MatchStatus::FullTime);
        assert_eq!(done.score, (74, 65));
        assert_eq!((done.home.name.as_str(), done.home.code.as_str(), done.home.id.as_str()), ("Fenerbahçe", "FBT", "bb-E-ULK"));
        assert_eq!(done.home.logo.as_deref(), Some("https://media-cdn.cortextech.io/fb.png"));
        assert_eq!(done.quarters, Some((vec![20, 18, 19, 17], vec![15, 17, 16, 17])));
        assert_eq!(done.followed, Some(Side::Home));
        assert_eq!(done.venue.as_deref(), Some("Ulker Sports And Event Hall"));
        assert_eq!(next.status, MatchStatus::Scheduled);
        assert_eq!(next.kickoff_utc, parse_kickoff("2026-10-23T18:15:00Z").unwrap());
        assert_eq!(next.followed, Some(Side::Away));
        assert_eq!(next.quarters, None, "not started");
        assert_eq!(game_of(&next.id), Some(("E2026".to_string(), 70)));
    }

    #[test]
    fn the_header_brings_the_score_and_the_quarter() {
        let mut g = games().remove(1);
        apply_header(&mut g, &serde_json::from_str(LIVE).unwrap());
        assert_eq!(g.status, MatchStatus::Live1);
        assert_eq!(g.score, (58, 61));
        assert_eq!(g.period, Some((3, "04:12".to_string())));
        assert_eq!(g.quarters, Some((vec![20, 18, 20], vec![15, 25, 21])), "the header adds them up; only the quarters played");
        apply_header(&mut g, &serde_json::from_str(OVER).unwrap());
        assert_eq!(g.status, MatchStatus::FullTime);
        assert_eq!(g.score, (79, 61));
        assert_eq!(g.period, None);
        assert_eq!(g.quarters, Some((vec![18, 15, 23, 23], vec![12, 12, 17, 20])));
    }

    #[test]
    fn a_header_before_tip_off_changes_nothing() {
        let mut g = games().remove(1);
        let before = g.clone();
        let pre = r#"{"Live":false,"ScoreA":"0","ScoreB":"0","Quarter":"","RemainingPartialTime":"10:00","GameTime":"00:00"}"#;
        apply_header(&mut g, &serde_json::from_str(pre).unwrap());
        assert_eq!(g, before);
    }

    #[test]
    fn the_pill_centre_shows_the_period() {
        use crate::activities::football::centre_text;
        let mut g = games().remove(1);
        apply_header(&mut g, &serde_json::from_str(LIVE).unwrap());
        assert_eq!(centre_text(&g, 0, 3 * 3600), "Ç3 04:12");
        apply_header(&mut g, &serde_json::from_str(OVER).unwrap());
        assert_eq!(centre_text(&g, 0, 3 * 3600), "MS");
    }

    #[test]
    fn period_texts() {
        assert_eq!(period_text(3, "04:12"), "Ç3 04:12");
        assert_eq!(period_text(2, "00:00"), "DA");
        assert_eq!(period_text(1, "00:00"), "Ç1 sonu");
        assert_eq!(period_text(5, "02:10"), "UZ 02:10");
        assert_eq!(period_text(6, "04:00"), "UZ2 04:00");
        assert_eq!(period_capsule(3, "04:12"), "3. çeyrek · 04:12");
        assert_eq!(period_capsule(2, "00:00"), "Devre arası");
        assert_eq!(period_capsule(5, "02:10"), "Uzatma · 02:10");
    }

    #[test]
    fn seasons_start_in_july() {
        let at = |s: &str| parse_kickoff(s).unwrap();
        assert_eq!(season_code('E', at("2026-10-21T12:00Z")), "E2026");
        assert_eq!(season_code('U', at("2027-03-01T12:00Z")), "U2026");
        assert_eq!(season_code('E', at("2026-07-01T00:00Z")), "E2026");
        assert_eq!(season_code('E', at("2026-06-30T23:59Z")), "E2025");
        assert_eq!(parse_team_id("bb-U-BES"), Some(('U', "BES".to_string())));
        assert_eq!(parse_team_id("436"), None, "an ESPN football id");
        assert!(is_basketball("eurocup") && !is_basketball("tur.1"));
    }

    #[test]
    fn club_search_ignores_turkish_letters() {
        let clubs = parse_clubs(
            &serde_json::from_str(
                r#"{"data":[{"code":"ULK","name":"Fenerbahce Beko Istanbul","editorialName":"Fenerbahce","tvCode":"FBB","images":{"crest":"https://x/fb.png"}},
                {"code":"IST","name":"Anadolu Efes Istanbul","editorialName":"Efes","tvCode":"EFS","images":{"crest":"https://x/efs.png"}},
                {"code":"BES","name":"Besiktas Fibabanka Istanbul","editorialName":"Besiktas","tvCode":"BJK","images":{"crest":null}}]}"#,
            )
            .unwrap(),
            'E',
        );
        assert_eq!(clubs.len(), 3);
        assert_eq!((clubs[0].id.as_str(), clubs[0].name.as_str(), clubs[0].league.as_str()), ("bb-E-ULK", "Fenerbahçe Beko İstanbul", "euroleague"));
        let names = |q: &str| search_clubs(&clubs, q).into_iter().map(|h| h.id).collect::<Vec<_>>();
        assert_eq!(names("fener"), vec!["bb-E-ULK"]);
        assert_eq!(names("Beşiktaş"), vec!["bb-E-BES"]);
        assert_eq!(names("besiktas"), vec!["bb-E-BES"]);
        assert_eq!(names("efes"), vec!["bb-E-IST"]);
        assert_eq!(names("istanbul").len(), 3);
        assert!(names("x").is_empty(), "one letter finds nothing");
    }
}
