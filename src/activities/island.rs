//! The in-game island: while a fullscreen app hides the pill, what can't wait (a goal or a red
//! card, Claude asking, a timer done, the battery low, a weather alert, a screen time limit)
//! comes down from the top of that screen for a few seconds. The pure part.

use super::ActivityKind;

/// How long the island shows one thing.
pub const ISLAND_MS: u64 = 5_000;

/// The kinds the island carries; the battery only when it runs low (plugging in can wait).
pub fn island_wants(kind: ActivityKind, power_low: bool) -> bool {
    match kind {
        ActivityKind::Timer | ActivityKind::Match(_) | ActivityKind::Live(_) | ActivityKind::WeatherAlert | ActivityKind::Usage => true,
        ActivityKind::Power => power_low,
        _ => false,
    }
}

/// The front goes onto the island: it leads the stack as a moment, the island carries its kind,
/// and it started (`since_ms`) less than [`ISLAND_MS`] ago.
pub fn on_island(urgent: bool, wants: bool, since_ms: u64, now_ms: u64) -> bool {
    urgent && wants && now_ms.saturating_sub(since_ms) < ISLAND_MS
}

#[cfg(test)]
mod tests {
    use super::*;
    use ActivityKind::*;

    #[test]
    fn what_cant_wait_goes_on_the_island() {
        for kind in [Timer, Match(3), Live(7), WeatherAlert, Usage] {
            assert!(island_wants(kind, false), "{kind:?}");
        }
        assert!(island_wants(Power, true), "the battery low");
        assert!(!island_wants(Power, false), "plugged in");
        for kind in [Volume, Capture, Rain, Buds, Media(1), Game(2), Shelf, Clock, Privacy] {
            assert!(!island_wants(kind, false), "{kind:?} can wait");
        }
    }

    #[test]
    fn the_island_shows_a_moment_for_five_seconds() {
        assert!(on_island(true, true, 1_000, 1_000));
        assert!(on_island(true, true, 1_000, 1_000 + ISLAND_MS - 1));
        assert!(!on_island(true, true, 1_000, 1_000 + ISLAND_MS), "done");
        assert!(!on_island(false, true, 1_000, 1_200), "not a moment");
        assert!(!on_island(true, false, 1_000, 1_200), "not the island's kind");
    }
}
