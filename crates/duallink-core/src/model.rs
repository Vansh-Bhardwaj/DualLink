//! Types shared by the app and the service. Everything here crosses the local pipe as JSON.

use serde::{Deserialize, Serialize};
use std::net::Ipv4Addr;

pub const MAX_LINKS: usize = 16;
pub const MAX_APPS: usize = 256;
pub const MAX_WEIGHT: u8 = 10;
pub const MAX_LIMIT_MBPS: u32 = 10_000;

/// What sort of connection an adapter is, as a person would name it.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum LinkKind {
    Ethernet,
    Wifi,
    /// A phone or modem sharing its connection over a USB cable.
    UsbTether,
    /// A built-in or plugged-in mobile data modem.
    Cellular,
    Other,
}

impl LinkKind {
    pub fn label(self) -> &'static str {
        match self {
            LinkKind::Ethernet => "Ethernet",
            LinkKind::Wifi => "Wi-Fi",
            LinkKind::UsbTether => "USB phone",
            LinkKind::Cellular => "Mobile data",
            LinkKind::Other => "Other",
        }
    }
}

/// One internet connection found on this PC.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct LinkInfo {
    /// The adapter's Windows GUID. Stable across address changes and reboots.
    pub id: String,
    /// The name Windows shows, such as "Ethernet 2".
    pub name: String,
    pub description: String,
    pub kind: LinkKind,
    pub if_index: u32,
    pub address: Ipv4Addr,
    pub gateway: Ipv4Addr,
}

/// How new connections are shared between links.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Mode {
    /// Prefer the link that is least busy and responding fastest, and keep a destination on
    /// the link it used before so sign-ins and sessions don't see the address change.
    #[default]
    Smart,
    /// Hand connections out in turn, in proportion to each link's share.
    Balanced,
    /// Use the first link; the others take over only while it is down.
    Backup,
}

/// Which programs DualLink combines connections for.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Scope {
    /// Everything on the PC, apart from the excluded programs.
    #[default]
    Everything,
    /// Only the chosen programs.
    Chosen,
}

/// The user's choice for one link.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct LinkChoice {
    pub id: String,
    pub enabled: bool,
    /// Share of new connections in Balanced mode, 1 to [`MAX_WEIGHT`].
    pub weight: u8,
    /// Speed cap in megabits per second; 0 means no cap.
    pub limit_mbps: u32,
}

impl LinkChoice {
    pub fn new(id: impl Into<String>) -> Self {
        LinkChoice { id: id.into(), enabled: true, weight: 1, limit_mbps: 0 }
    }
}

/// Everything the service needs to know about what the user wants.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct Config {
    /// Whether combining should be on. Survives restarts.
    pub enabled: bool,
    pub mode: Mode,
    pub scope: Scope,
    /// Programs to combine for when `scope` is [`Scope::Chosen`]: full paths or bare file names.
    pub apps: Vec<String>,
    /// Programs never touched when `scope` is [`Scope::Everything`].
    pub excluded: Vec<String>,
    /// Per-link choices, in priority order. A link not listed here is used with defaults.
    pub links: Vec<LinkChoice>,
    /// Stop routed programs using QUIC so their traffic falls back to connections DualLink
    /// can spread across links.
    pub steer_quic: bool,
}

impl Default for Config {
    fn default() -> Self {
        Config {
            enabled: false,
            mode: Mode::Smart,
            scope: Scope::Everything,
            apps: Vec::new(),
            excluded: Vec::new(),
            links: Vec::new(),
            steer_quic: true,
        }
    }
}

impl Config {
    /// Rejects values that could only come from a damaged file or a hostile client.
    pub fn validate(&self) -> Result<(), String> {
        if self.links.len() > MAX_LINKS {
            return Err(format!("At most {MAX_LINKS} connections can be configured."));
        }
        if self.apps.len() > MAX_APPS || self.excluded.len() > MAX_APPS {
            return Err(format!("At most {MAX_APPS} programs can be listed."));
        }
        for link in &self.links {
            if link.id.is_empty() || link.id.len() > 64 || link.id.chars().any(char::is_control) {
                return Err("A connection identifier is invalid.".into());
            }
            if link.weight == 0 || link.weight > MAX_WEIGHT {
                return Err(format!("A connection share must be between 1 and {MAX_WEIGHT}."));
            }
            if link.limit_mbps > MAX_LIMIT_MBPS {
                return Err(format!("A speed cap must be at most {MAX_LIMIT_MBPS} Mbps."));
            }
        }
        for app in self.apps.iter().chain(&self.excluded) {
            let name = app.trim();
            if name.is_empty() || name.len() > 1024 || name.chars().any(char::is_control) {
                return Err("A program entry is invalid.".into());
            }
            if !name.to_ascii_lowercase().ends_with(".exe") {
                return Err("Programs must be Windows .exe names or paths.".into());
            }
        }
        Ok(())
    }

    pub fn choice(&self, id: &str) -> LinkChoice {
        self.links
            .iter()
            .find(|c| c.id.eq_ignore_ascii_case(id))
            .cloned()
            .unwrap_or_else(|| LinkChoice::new(id))
    }
}

/// Live numbers for one link, measured by the router.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct LinkStatus {
    pub info: LinkInfo,
    pub enabled: bool,
    pub weight: u8,
    pub limit_mbps: u32,
    /// False while the router is resting the link after failed connections.
    pub healthy: bool,
    pub active_connections: u32,
    pub total_connections: u64,
    pub failures: u32,
    /// Smoothed time to open a connection, if any has been opened.
    pub latency_ms: Option<f32>,
    pub downloaded: u64,
    pub uploaded: u64,
}

/// What the engine is doing right now.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum EngineState {
    #[default]
    Off,
    Starting,
    On,
    /// On was asked for, but there is no usable link at the moment.
    NoLinks,
    /// The engine could not start; `Status::problem` says why.
    Failed,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct Status {
    pub version: String,
    pub state: EngineState,
    pub problem: Option<String>,
    pub config: Config,
    pub links: Vec<LinkStatus>,
    pub active_connections: u32,
    /// Connections taken over since combining was switched on.
    pub routed_connections: u64,
    /// Connections left alone (local network, excluded programs, programs not chosen).
    pub bypassed_connections: u64,
    /// Milliseconds since the service started, for turning byte counters into speeds.
    pub uptime_ms: u64,
}
