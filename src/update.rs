//! Updates from GitHub Releases: whether the latest release is newer, and its installer with the
//! checksum GitHub keeps for it. The pure part; platform/updater.rs asks GitHub, downloads the
//! installer and runs it.

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// "owner/name" of the repository whose releases are the updates (asked without a token: it
/// answers once the repository is public); empty: none asked.
pub const REPO: &str = "ELhadratiOth/WinIsland";

/// The one file a release carries: the installer, which updates an installed WinIsland in place.
pub const SETUP: &str = "winisland-setup.exe";

pub type Version = (u32, u32, u32);

pub fn latest_url(repo: &str) -> String {
    format!("https://api.github.com/repos/{repo}/releases/latest")
}

/// "v0.3.0" or "0.3.0"; `None` for anything else, a pre-release suffix included.
pub fn parse_version(tag: &str) -> Option<Version> {
    let mut parts = tag.strip_prefix('v').unwrap_or(tag).split('.').map(|p| p.parse::<u32>().ok());
    let version = (parts.next()??, parts.next()??, parts.next()??);
    parts.next().is_none().then_some(version)
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Release {
    pub version: Version,
    pub tag: String,
    /// The release's page (opened when the exes can't be swapped).
    pub page: String,
    pub assets: Vec<Asset>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Asset {
    pub name: String,
    pub url: String,
    /// GitHub's SHA-256 of the file (lowercase hex), when it gave one.
    pub sha256: Option<String>,
}

impl Release {
    pub fn asset(&self, name: &str) -> Option<&Asset> {
        self.assets.iter().find(|a| a.name == name)
    }
}

/// GitHub's latest-release answer; `None` for a draft, a pre-release or an error answer.
pub fn parse_release(v: &Value) -> Option<Release> {
    if v["draft"].as_bool() != Some(false) || v["prerelease"].as_bool() != Some(false) {
        return None;
    }
    let tag = v["tag_name"].as_str()?;
    let assets = v["assets"]
        .as_array()?
        .iter()
        .filter_map(|a| {
            let sha256 = a["digest"].as_str().and_then(|d| d.strip_prefix("sha256:")).map(str::to_ascii_lowercase);
            Some(Asset { name: a["name"].as_str()?.to_string(), url: a["browser_download_url"].as_str()?.to_string(), sha256 })
        })
        .collect();
    Some(Release { version: parse_version(tag)?, tag: tag.to_string(), page: v["html_url"].as_str()?.to_string(), assets })
}

/// Whether `r` is newer than the running `current` ("0.2.0").
pub fn is_newer(r: &Release, current: &str) -> bool {
    parse_version(current).is_some_and(|now| r.version > now)
}

/// Whether the version `latest` ("0.3.0", as update.json keeps it) is newer than `current`.
pub fn newer(latest: &str, current: &str) -> bool {
    matches!((parse_version(latest), parse_version(current)), (Some(l), Some(c)) if l > c)
}

/// The Hakkında page's update line, and whether its row installs.
pub fn update_line(state: Option<&UpdateState>, current: &str) -> (String, bool) {
    match state {
        None => (crate::i18n::trf("{} · denetlenmedi", &[&current]), false),
        Some(s) if newer(&s.latest, current) => (crate::i18n::trf("{} hazır · şimdiki {}", &[&s.latest, &current]), true),
        Some(_) => (crate::i18n::trf("{} · güncel", &[&current]), false),
    }
}

/// `%LOCALAPPDATA%\WinIsland\update.json`: what the last check found, for the settings window.
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct UpdateState {
    /// The latest release's version ("0.3.0").
    pub latest: String,
    pub page: String,
    /// When it was asked (unix seconds).
    pub checked: i64,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versions_compare_as_numbers() {
        assert_eq!(parse_version("v0.10.0"), Some((0, 10, 0)));
        assert_eq!(parse_version("0.2.0"), Some((0, 2, 0)));
        assert_eq!(parse_version("v0.3.0-beta"), None);
        assert_eq!(parse_version("latest"), None);
        assert_eq!(parse_version("v1.2"), None);
        let r = Release { version: (0, 10, 0), tag: "v0.10.0".into(), page: String::new(), assets: Vec::new() };
        assert!(is_newer(&r, "0.9.3"));
        assert!(!is_newer(&r, "0.10.0"));
        assert!(!is_newer(&r, "1.0.0"));
        assert!(!is_newer(&r, "garbage"), "an unreadable current version never updates");
        assert!(newer("0.3.0", "0.2.0"));
        assert!(!newer("0.2.0", "0.2.0"));
        assert!(!newer("", "0.2.0"), "nothing found yet");
    }

    fn answer() -> Value {
        serde_json::json!({
            "tag_name": "v0.3.0", "html_url": "https://github.com/k/winisland/releases/tag/v0.3.0",
            "draft": false, "prerelease": false,
            "assets": [
                {"name": "winisland-setup.exe", "browser_download_url": "https://github.com/k/winisland/releases/download/v0.3.0/winisland-setup.exe", "size": 9000000, "digest": "sha256:AB12CD"},
                {"name": "notes.txt", "browser_download_url": "https://github.com/k/winisland/releases/download/v0.3.0/notes.txt"}
            ]
        })
    }

    #[test]
    fn release_reads_tag_page_and_assets() {
        let r = parse_release(&answer()).unwrap();
        assert_eq!((r.version, r.tag.as_str()), ((0, 3, 0), "v0.3.0"));
        assert_eq!(r.page, "https://github.com/k/winisland/releases/tag/v0.3.0");
        let setup = r.asset(SETUP).unwrap();
        assert_eq!(setup.url, "https://github.com/k/winisland/releases/download/v0.3.0/winisland-setup.exe");
        assert_eq!(setup.sha256.as_deref(), Some("ab12cd"), "GitHub's digest, lowercase");
        assert_eq!(r.asset("notes.txt").unwrap().sha256, None, "no digest given");
        assert_eq!(r.asset("winisland.exe"), None);
        let mut draft = answer();
        draft["draft"] = true.into();
        assert_eq!(parse_release(&draft), None);
        let mut pre = answer();
        pre["prerelease"] = true.into();
        assert_eq!(parse_release(&pre), None);
        assert_eq!(parse_release(&serde_json::json!({"message": "Not Found", "status": "404"})), None);
        assert_eq!(latest_url("k/winisland"), "https://api.github.com/repos/k/winisland/releases/latest");
    }

    #[test]
    fn the_about_line_says_where_it_stands() {
        let state = |latest: &str| UpdateState { latest: latest.into(), page: String::new(), checked: 1 };
        assert_eq!(update_line(None, "0.2.0"), ("0.2.0 · not checked".to_string(), false));
        assert_eq!(update_line(Some(&state("0.2.0")), "0.2.0"), ("0.2.0 · up to date".to_string(), false));
        assert_eq!(update_line(Some(&state("0.3.0")), "0.2.0"), ("0.3.0 ready · now 0.2.0".to_string(), true));
    }
}
