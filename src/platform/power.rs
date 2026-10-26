//! The battery: its level and whether it charges (GetSystemPowerStatus).

use windows::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Power {
    pub pct: u8,
    pub charging: bool,
}

/// The battery now; `None` on a machine without one (or when Windows doesn't know).
pub fn status() -> Option<Power> {
    let mut s = SYSTEM_POWER_STATUS::default();
    unsafe { GetSystemPowerStatus(&mut s) }.ok()?;
    from_status(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent)
}

/// SYSTEM_POWER_STATUS's fields: AC line 1 = plugged in, battery flag 128 = no battery and
/// 255 = unknown, level 255 = unknown.
pub fn from_status(ac_line: u8, flag: u8, pct: u8) -> Option<Power> {
    if flag == 128 || flag == 255 || pct == 255 {
        return None;
    }
    Some(Power { pct: pct.min(100), charging: ac_line == 1 })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn no_battery_no_power_activity() {
        assert_eq!(from_status(1, 128, 255), None, "a desktop");
        assert_eq!(from_status(255, 255, 255), None, "unknown");
        assert_eq!(from_status(0, 1, 255), None, "level unknown");
        assert_eq!(from_status(1, 8, 64), Some(Power { pct: 64, charging: true }));
        assert_eq!(from_status(0, 2, 12), Some(Power { pct: 12, charging: false }));
        assert_eq!(from_status(1, 0, 100), Some(Power { pct: 100, charging: true }), "full, still plugged in");
    }
}
