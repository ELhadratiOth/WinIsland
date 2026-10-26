//! Other Bluetooth headsets and their battery. A headset reports its level over Hands-Free
//! (HFP) and Windows keeps it on the headset's device node ({104EA319-…} 2) with its address;
//! the Bluetooth device at that address says whether it's connected, its name and its class. A
//! thread asks every minute and posts the list when it changes.

use std::sync::{Arc, Mutex};
use std::time::Duration;

use windows::core::{IInspectable, Interface, HSTRING};
use windows::Devices::Bluetooth::{BluetoothConnectionStatus, BluetoothDevice};
use windows::Devices::Enumeration::{DeviceInformation, DeviceInformationKind};
use windows::Foundation::IPropertyValue;
use windows_collections::IIterable;

use crate::platform::log::log;
use crate::platform::windows::post_headsets;

/// A connected headset with a battery level (`id`: a hash of its address).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Headset {
    pub id: u32,
    pub name: String,
    pub pct: u8,
}

/// A connected Bluetooth device: its name, address ("02:00:00:00:00:01") and major device class.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Endpoint {
    pub name: String,
    pub address: String,
    pub major: u32,
}

pub type HeadsetMailbox = Arc<Mutex<Vec<Headset>>>;

const ASK_EVERY: Duration = Duration::from_secs(60);
const BATTERY: &str = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
const NODE_ADDRESS: &str = "{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A} 1";

/// Starts the thread that asks Windows every minute; the first list arrives at once.
pub fn watch(mailbox: HeadsetMailbox) {
    std::thread::spawn(move || {
        unsafe {
            let _ = windows::Win32::System::Com::CoInitializeEx(None, windows::Win32::System::Com::COINIT_MULTITHREADED);
        }
        let mut last = None;
        let mut first = true;
        loop {
            let levels = levels();
            let connected = connected(&levels);
            if first {
                let names: Vec<&str> = connected.iter().map(|e| e.name.as_str()).collect();
                log(&format!("headsets: connected {names:?}, {} with a level", levels.len()));
                first = false;
            }
            let now = join(&connected, &levels);
            if last.as_ref() != Some(&now) {
                if let Ok(mut slot) = mailbox.lock() {
                    *slot = now.clone();
                }
                post_headsets();
                last = Some(now);
            }
            std::thread::sleep(ASK_EVERY);
        }
    });
}

/// The connected headsets (not the Galaxy Buds: the Buds worker has those) with a level.
pub fn headsets() -> Vec<Headset> {
    let levels = levels();
    join(&connected(&levels), &levels)
}

fn find(aqs: &str, props: &[&str], kind: DeviceInformationKind) -> Vec<DeviceInformation> {
    let props: Vec<HSTRING> = props.iter().map(|p| HSTRING::from(*p)).collect();
    DeviceInformation::FindAllAsyncWithKindAqsFilterAndAdditionalProperties(&HSTRING::from(aqs), &IIterable::from(props), kind)
        .and_then(|op| op.join())
        .map(|all| all.into_iter().collect())
        .inspect_err(|e| log(&format!("headsets: {aqs}: {e:?}")))
        .unwrap_or_default()
}

fn property(d: &DeviceInformation, key: &str) -> Option<IPropertyValue> {
    let value: IInspectable = d.Properties().ok()?.Lookup(&HSTRING::from(key)).ok()?;
    value.cast().ok()
}

/// The devices among these addresses that are connected now, asked one by one (an
/// association-endpoint query would search the air for 30 s, which can stutter Bluetooth audio).
fn connected(levels: &[(String, u8)]) -> Vec<Endpoint> {
    levels.iter().filter_map(|(address, _)| endpoint(address)).collect()
}

fn endpoint(address: &str) -> Option<Endpoint> {
    let device = BluetoothDevice::FromBluetoothAddressAsync(u64::from_str_radix(address, 16).ok()?).ok()?.join().ok()?;
    if device.ConnectionStatus().ok()? != BluetoothConnectionStatus::Connected {
        return None;
    }
    Some(Endpoint {
        name: device.Name().ok()?.to_string(),
        address: address.to_string(),
        major: device.ClassOfDevice().ok()?.MajorClass().ok()?.0 as u32,
    })
}

/// (address "020000000001", level) of every classic Bluetooth device node that has a level.
fn levels() -> Vec<(String, u8)> {
    let aqs = r#"System.Devices.DeviceInstanceId:~<"BTHENUM""#;
    find(aqs, &[BATTERY, NODE_ADDRESS], DeviceInformationKind::Device)
        .iter()
        .filter_map(|d| Some((property(d, NODE_ADDRESS)?.GetString().ok()?.to_string(), property(d, BATTERY)?.GetUInt8().ok()?)))
        .collect()
}

/// The headsets among the connected devices (audio class, not the Buds) that have a level.
pub fn join(connected: &[Endpoint], levels: &[(String, u8)]) -> Vec<Headset> {
    // Major device class 4 is audio / video.
    connected
        .iter()
        .filter(|e| e.major == 4 && !is_galaxy_buds(&e.name))
        .filter_map(|e| {
            let address: String = e.address.chars().filter(|c| *c != ':').collect::<String>().to_uppercase();
            let pct = levels.iter().find(|(a, _)| a.eq_ignore_ascii_case(&address))?.1;
            let id = address.bytes().fold(0x811c_9dc5u32, |h, b| (h ^ b as u32).wrapping_mul(0x0100_0193));
            Some(Headset { id, name: headset_name(&e.name), pct: pct.min(100) })
        })
        .collect()
}

/// "Ekin adlı kişiye ait soundcore H30i" → "soundcore H30i".
pub fn headset_name(name: &str) -> String {
    name.split_once(" adlı kişiye ait ").map_or(name, |(_, device)| device).trim().to_string()
}

/// Samsung's Buds, which the Buds worker reads (renamed ones keep their model in the name):
/// other makers' "Buds" (Pixel, Redmi, OnePlus, FreeBuds) are headsets here.
pub fn is_galaxy_buds(name: &str) -> bool {
    let n = name.to_lowercase();
    n.contains("galaxy buds") || ["buds2", "buds3", "buds4", "buds fe", "buds pro", "buds live", "buds+", "buds core"].iter().any(|m| n.contains(m)) && !["pixel", "redmi", "oneplus", "freebuds", "nothing"].iter().any(|o| n.contains(o))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_galaxy_buds_are_left_to_the_buds_worker() {
        for name in ["Galaxy Buds2 Pro (A1B2)", "Ekin adlı kişiye ait Buds2", "Galaxy Buds FE", "Ayşe's Buds3 Pro"] {
            assert!(is_galaxy_buds(name), "{name}");
        }
        for name in ["Pixel Buds Pro", "Redmi Buds 4", "OnePlus Buds Z2", "HUAWEI FreeBuds 5i", "soundcore H30i"] {
            assert!(!is_galaxy_buds(name), "{name} has its battery shown as a headset");
        }
    }

    fn ep(name: &str, address: &str, major: u32) -> Endpoint {
        Endpoint { name: name.into(), address: address.into(), major }
    }

    /// Asks Windows for real: `cargo test --lib live_headsets -- --ignored --nocapture`.
    #[test]
    #[ignore]
    fn live_headsets() {
        let t = std::time::Instant::now();
        let levels = levels();
        println!("levels: {levels:?} in {:?}", t.elapsed());
        let t = std::time::Instant::now();
        println!("connected: {:?} in {:?}", connected(&levels), t.elapsed());
        println!("headsets: {:?}", headsets());
    }

    #[test]
    fn headset_battery_from_property() {
        let connected = [
            ep("soundcore H30i", "02:00:00:00:00:01", 4),
            ep("Ekin adlı kişiye ait Buds2", "02:00:00:00:00:02", 4),
            ep("Ekin adlı kişiye ait S22", "02:00:00:00:00:03", 2),
            ep("VXE R1SE+", "02:00:00:00:00:04", 0),
            ep("Ekin adlı kişiye ait WH-1000XM4", "00:11:22:33:44:55", 4),
        ];
        let levels = [("020000000001".to_string(), 60), ("020000000002".into(), 82), ("020000000003".into(), 60), ("020000000004".into(), 20)];
        let found = join(&connected, &levels);
        assert_eq!(found.len(), 1, "an audio device with a level, not the Buds, the phone or the mouse: {found:?}");
        assert_eq!((found[0].name.as_str(), found[0].pct), ("soundcore H30i", 60));
        assert_ne!(found[0].id, 0);
        assert_eq!(headset_name("Ekin adlı kişiye ait WH-1000XM4"), "WH-1000XM4");
        assert_eq!(headset_name("soundcore H30i"), "soundcore H30i");
    }
}
