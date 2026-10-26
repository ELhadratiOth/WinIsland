//! The charging and low-battery moments: plugging in, and the level dropping below the
//! threshold on battery, each show in the pill for a few seconds.

use crate::platform::power::Power;

/// A moment stays in front this long.
pub const POWER_MOMENT_MS: u64 = 4000;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PowerMoment {
    Plugged,
    Low,
}

/// The moment a change from `before` to `now` makes, if any (`low_pct`: the settings' threshold).
pub fn power_moment(before: Option<Power>, now: Option<Power>, low_pct: u8) -> Option<PowerMoment> {
    let (b, n) = (before?, now?);
    if n.charging && !b.charging {
        return Some(PowerMoment::Plugged);
    }
    let low = |p: Power| !p.charging && p.pct < low_pct;
    (low(n) && !low(b)).then_some(PowerMoment::Low)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn p(pct: u8, charging: bool) -> Option<Power> {
        Some(Power { pct, charging })
    }

    #[test]
    fn power_moment_on_plug_in_and_on_crossing_low() {
        assert_eq!(power_moment(p(40, false), p(40, true), 15), Some(PowerMoment::Plugged));
        assert_eq!(power_moment(p(40, true), p(41, true), 15), None, "charging on");
        assert_eq!(power_moment(p(40, true), p(40, false), 15), None, "unplugged");
        assert_eq!(power_moment(p(15, false), p(14, false), 15), Some(PowerMoment::Low));
        assert_eq!(power_moment(p(14, false), p(13, false), 15), None, "once, when it crosses");
        assert_eq!(power_moment(p(15, true), p(14, true), 15), None, "not while charging");
        assert_eq!(power_moment(p(14, true), p(14, false), 15), Some(PowerMoment::Low), "unplugged while low");
        assert_eq!(power_moment(None, p(50, true), 15), None, "the first reading is no change");
        assert_eq!(power_moment(None, None, 15), None);
    }
}
