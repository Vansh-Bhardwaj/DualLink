//! What the window shows, and how it stays in step with the service.

use crate::apps::{self, AppInfo};
use crate::client::Client;
use duallink_core::{Config, EngineState, LinkChoice, LinkStatus, Status};
use rune::Waker;
use std::collections::{HashMap, VecDeque};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::time::Duration;

/// How many one-second samples the speed graph keeps.
pub const HISTORY: usize = 60;
pub const CAPS_MBPS: [u32; 9] = [0, 5, 10, 25, 50, 100, 250, 500, 1000];

/// Bytes down and up per link id, as last reported.
type Counters = HashMap<String, (u64, u64)>;

/// What the service said, tagged with the last change of ours it had seen.
pub struct Report {
    pub seen: u64,
    pub result: Result<Status, String>,
}

#[derive(Clone, Copy, Default, PartialEq)]
pub struct Speed {
    pub down: f32,
    pub up: f32,
}

/// The pages of the window.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Page {
    #[default]
    Overview,
    Programs,
    Settings,
    Activity,
}

pub struct Ui {
    pub page: Page,
    /// The window's width in points, for fitting the layout to it.
    pub width: f32,
    pub prefs: crate::prefs::Prefs,
    /// Whether Windows opens DualLink at sign-in.
    pub autostart: bool,
    /// The end of the service's log, newest last.
    pub log: Vec<String>,
    /// Connections this PC has, found by the window itself, shown while the service can't be reached.
    pub local: Vec<LinkStatus>,
    /// The latest report from the service; `None` until the first one arrives.
    pub status: Option<Status>,
    /// Why the service can't be reached, when it can't.
    pub offline: Option<String>,
    /// A change the service turned down.
    pub refused: Option<String>,
    /// What the user wants. Edited here first so controls answer at once, then sent.
    pub config: Config,
    /// Megabits per second through DualLink, per link id.
    pub speeds: HashMap<String, Speed>,
    /// Download speed per link for each of the last [`HISTORY`] seconds, oldest first.
    pub history: VecDeque<Vec<(String, f32)>>,
    pub peak: f32,
    pub expanded: Option<String>,
    /// Names and icons of programs, by lower-cased full path.
    pub apps: HashMap<String, AppInfo>,
    /// Programs with a window on screen, as lower-cased full paths, in the order found.
    pub open_now: Vec<String>,
    /// The travelling pulses in the picture.
    pub flow: crate::flow::Flow,
    previous: Option<(u64, Counters)>,
    sent: u64,
    changes: Option<Sender<(u64, Config)>>,
    reports: Option<Receiver<Report>>,
    wanted: Option<Sender<Vec<String>>>,
    app_news: Option<Receiver<AppNews>>,
}

/// What the program-watching thread found.
pub enum AppNews {
    Info(AppInfo),
    OpenNow(Vec<String>),
    Log(Vec<String>),
    Links(Vec<duallink_core::LinkInfo>),
}

impl Default for Ui {
    fn default() -> Self {
        Ui {
            page: Page::Overview,
            width: 1000.0,
            prefs: crate::prefs::Prefs::default(),
            autostart: false,
            log: Vec::new(),
            local: Vec::new(),
            status: None,
            offline: None,
            refused: None,
            config: Config::default(),
            speeds: HashMap::new(),
            history: VecDeque::new(),
            peak: 0.0,
            expanded: None,
            apps: HashMap::new(),
            open_now: Vec::new(),
            flow: crate::flow::Flow::default(),
            previous: None,
            sent: 0,
            changes: None,
            reports: None,
            wanted: None,
            app_news: None,
        }
    }
}

impl Ui {
    /// Starts the thread that asks the service how things are every second and passes on
    /// the user's changes. `wake` redraws the window when there is news.
    pub fn connect(&mut self, wake: Waker) {
        let wake2 = wake.clone();
        let (change_tx, change_rx) = mpsc::channel::<(u64, Config)>();
        let (report_tx, report_rx) = mpsc::channel();
        self.changes = Some(change_tx);
        self.reports = Some(report_rx);
        std::thread::spawn(move || {
            let mut client = Client::default();
            let mut seen = 0;
            loop {
                let result = match change_rx.recv_timeout(Duration::from_secs(1)) {
                    Ok(mut change) => {
                        // Only the newest of a burst of changes matters.
                        while let Ok(newer) = change_rx.try_recv() {
                            change = newer;
                        }
                        seen = change.0;
                        client.apply(change.1)
                    }
                    Err(RecvTimeoutError::Timeout) => client.status(),
                    Err(RecvTimeoutError::Disconnected) => return,
                };
                if report_tx.send(Report { seen, result }).is_err() {
                    return;
                }
                wake();
            }
        });
        self.watch_programs(wake2);
    }

    /// Starts the thread that reads program names and icons, and every few seconds looks for
    /// programs open on screen, so they can be offered with a click.
    fn watch_programs(&mut self, wake: Waker) {
        let (wanted_tx, wanted_rx) = mpsc::channel::<Vec<String>>();
        let (news_tx, news_rx) = mpsc::channel();
        self.wanted = Some(wanted_tx);
        self.app_news = Some(news_rx);
        let _ = self.wanted.as_ref().map(|w| w.send(self.listed_paths()));
        std::thread::spawn(move || {
            let mut known = std::collections::HashSet::new();
            let mut wanted: Vec<String> = Vec::new();
            loop {
                let open = apps::open_now();
                let lower: Vec<String> = open.iter().map(|p| p.to_lowercase()).collect();
                let mut fresh = false;
                for path in open.iter().chain(wanted.iter()) {
                    if known.insert(path.to_lowercase()) {
                        fresh |= news_tx.send(AppNews::Info(AppInfo::read(path))).is_ok();
                    }
                }
                if news_tx.send(AppNews::OpenNow(lower)).is_err() || news_tx.send(AppNews::Log(log_tail(40))).is_err()
                    || news_tx.send(AppNews::Links(duallink_core::links::discover().unwrap_or_default())).is_err()
                {
                    return;
                }
                if fresh {
                    wake();
                }
                match wanted_rx.recv_timeout(Duration::from_secs(4)) {
                    Ok(list) => wanted = list,
                    Err(RecvTimeoutError::Timeout) => {}
                    Err(RecvTimeoutError::Disconnected) => return,
                }
                while let Ok(list) = wanted_rx.try_recv() {
                    wanted = list;
                }
            }
        });
    }

    /// The listed programs given as full paths: the ones whose icons can be read without
    /// the program running.
    fn listed_paths(&self) -> Vec<String> {
        self.config.apps.iter().chain(&self.config.excluded).filter(|p| p.contains(['\\', '/'])).cloned().collect()
    }

    /// What is known about a listed program: by full path, or for a bare file name, any
    /// known program with that name.
    pub fn info(&self, entry: &str) -> Option<&AppInfo> {
        let entry = entry.trim().to_lowercase();
        if entry.contains(['\\', '/']) {
            return self.apps.get(&entry);
        }
        self.apps.values().find(|app| app.path.rsplit(['\\', '/']).next() == Some(entry.as_str()))
    }

    /// Programs open now that aren't listed yet, to offer with a click.
    pub fn suggestions(&self) -> Vec<&AppInfo> {
        let listed = self.programs();
        self.open_now
            .iter()
            .filter_map(|path| self.apps.get(path))
            .filter(|app| !listed.iter().any(|entry| self.info(entry).is_some_and(|known| known.path == app.path)))
            .take(10)
            .collect()
    }

    /// Takes in everything the service has reported since the last frame.
    pub fn absorb(&mut self) -> bool {
        let Some(reports) = &self.reports else { return false };
        let pending: Vec<Report> = reports.try_iter().collect();
        let mut changed = !pending.is_empty();
        for report in pending {
            self.take(report);
        }
        let news: Vec<AppNews> = self.app_news.as_ref().map(|n| n.try_iter().collect()).unwrap_or_default();
        for item in news {
            changed |= match item {
                AppNews::Info(app) => self.apps.insert(app.path.clone(), app).is_none(),
                AppNews::OpenNow(open) => std::mem::replace(&mut self.open_now, open.clone()) != open,
                AppNews::Log(lines) => std::mem::replace(&mut self.log, lines.clone()) != lines,
                AppNews::Links(found) => {
                    let local: Vec<LinkStatus> = found.into_iter().map(|info| idle_link(&self.config, info)).collect();
                    std::mem::replace(&mut self.local, local.clone()) != local
                }
            };
        }
        changed
    }

    pub fn take(&mut self, report: Report) {
        match report.result {
            Ok(status) => {
                self.offline = None;
                self.measure(&status);
                // While a change of ours is still on its way, the service's copy is older than ours.
                if report.seen == self.sent {
                    self.config = status.config.clone();
                    self.refused = None;
                }
                self.status = Some(status);
            }
            Err(problem) if problem == crate::client::NOT_RUNNING => {
                self.offline = Some(problem);
                self.speeds.clear();
                self.previous = None;
            }
            Err(problem) => {
                // The service is there but said no: go back to what it has.
                self.refused = Some(problem);
                self.sent = report.seen;
            }
        }
    }

    /// Turns the byte counters of two reports into speeds and adds a point to the graph.
    fn measure(&mut self, status: &Status) {
        let now: HashMap<String, (u64, u64)> =
            status.links.iter().map(|l| (l.info.id.clone(), (l.downloaded, l.uploaded))).collect();
        if let Some((then_ms, then)) = &self.previous {
            let elapsed = status.uptime_ms.saturating_sub(*then_ms) as f32 / 1000.0;
            if elapsed < 0.2 {
                return; // two reports in quick succession: keep the earlier baseline
            }
            self.speeds.clear();
            for (id, (down, up)) in &now {
                let (old_down, old_up) = then.get(id).copied().unwrap_or((*down, *up));
                let mbps = |new: u64, old: u64| new.saturating_sub(old) as f32 * 8.0 / 1_000_000.0 / elapsed;
                self.speeds.insert(id.clone(), Speed { down: mbps(*down, old_down), up: mbps(*up, old_up) });
            }
            let sample: Vec<(String, f32)> =
                status.links.iter().map(|l| (l.info.id.clone(), self.speeds.get(&l.info.id).map_or(0.0, |s| s.down))).collect();
            self.peak = self.peak.max(sample.iter().map(|(_, v)| v).sum());
            self.history.push_back(sample);
            while self.history.len() > HISTORY {
                self.history.pop_front();
            }
        }
        self.previous = Some((status.uptime_ms, now));
    }

    /// Sends the user's configuration to the service.
    pub fn send(&mut self) {
        self.sent += 1;
        self.refused = None;
        if let Some(changes) = &self.changes {
            let _ = changes.send((self.sent, self.config.clone()));
        }
        if let Some(wanted) = &self.wanted {
            let _ = wanted.send(self.listed_paths());
        }
    }

    /// The connections to show: the service's, or while it can't be reached, the ones this PC has.
    pub fn links(&self) -> &[LinkStatus] {
        self.status.as_ref().filter(|_| self.offline.is_none()).map_or(&self.local, |s| &s.links)
    }

    pub fn state(&self) -> EngineState {
        self.status.as_ref().map_or(EngineState::Off, |s| s.state)
    }

    pub fn total(&self) -> Speed {
        self.speeds.values().fold(Speed::default(), |a, s| Speed { down: a.down + s.down, up: a.up + s.up })
    }

    /// Makes sure every link found has an entry, in the order shown, then edits one of them.
    pub fn edit_link(&mut self, id: &str, edit: impl FnOnce(&mut LinkChoice)) {
        self.arrange();
        if let Some(choice) = self.config.links.iter_mut().find(|c| c.id.eq_ignore_ascii_case(id)) {
            edit(choice);
            self.send();
        }
    }

    /// Moves a link one place up the priority order.
    pub fn raise_link(&mut self, id: &str) {
        self.arrange();
        if let Some(at) = self.config.links.iter().position(|c| c.id.eq_ignore_ascii_case(id)).filter(|&at| at > 0) {
            self.config.links.swap(at, at - 1);
            self.send();
        }
    }

    fn arrange(&mut self) {
        let found: Vec<String> = self.ordered().iter().map(|l| l.info.id.clone()).collect();
        let mut arranged: Vec<LinkChoice> = found.iter().map(|id| self.config.choice(id)).collect();
        // Links that are unplugged right now keep their settings for when they return.
        arranged.extend(self.config.links.iter().filter(|c| !found.iter().any(|id| id.eq_ignore_ascii_case(&c.id))).cloned());
        self.config.links = arranged;
    }

    /// The links in the user's priority order: arranged ones first, then the rest as found.
    pub fn ordered(&self) -> Vec<&LinkStatus> {
        let links = self.links();
        let place = |l: &LinkStatus| self.config.links.iter().position(|c| c.id.eq_ignore_ascii_case(&l.info.id));
        let mut ordered: Vec<&LinkStatus> = links.iter().collect();
        ordered.sort_by_key(|l| place(l).unwrap_or(usize::MAX));
        ordered
    }

    /// Adds what is typed in the program box to the list in use, if it names a program.
    pub fn add_program(&mut self, name: &str) {
        let name = name.trim().trim_matches('"').to_string();
        if name.is_empty() {
            return;
        }
        let name = if name.to_ascii_lowercase().ends_with(".exe") { name } else { format!("{name}.exe") };
        let list = self.programs_mut();
        if !list.iter().any(|p| p.eq_ignore_ascii_case(&name)) {
            list.push(name);
            self.send();
        }
    }

    pub fn remove_program(&mut self, index: usize) {
        let list = self.programs_mut();
        if index < list.len() {
            list.remove(index);
            self.send();
        }
    }

    /// The list that goes with the current choice: programs to combine, or programs to leave alone.
    pub fn programs(&self) -> &[String] {
        match self.config.scope {
            duallink_core::Scope::Everything => &self.config.excluded,
            duallink_core::Scope::Chosen => &self.config.apps,
        }
    }

    fn programs_mut(&mut self) -> &mut Vec<String> {
        match self.config.scope {
            duallink_core::Scope::Everything => &mut self.config.excluded,
            duallink_core::Scope::Chosen => &mut self.config.apps,
        }
    }
}

/// A connection the window found itself, with nothing measured yet.
pub fn idle_link(config: &Config, info: duallink_core::LinkInfo) -> LinkStatus {
    let choice = config.choice(&info.id);
    LinkStatus {
        info,
        enabled: choice.enabled,
        weight: choice.weight,
        limit_mbps: choice.limit_mbps,
        healthy: true,
        active_connections: 0,
        total_connections: 0,
        failures: 0,
        latency_ms: None,
        downloaded: 0,
        uploaded: 0,
    }
}

/// The last `count` lines of the service's log, oldest first.
pub fn log_tail(count: usize) -> Vec<String> {
    let base = std::env::var_os("ProgramData").map_or_else(|| std::path::PathBuf::from(r"C:\ProgramData"), std::path::PathBuf::from);
    let Ok(text) = std::fs::read_to_string(base.join("DualLink").join("service.log")) else { return Vec::new() };
    let lines: Vec<&str> = text.lines().filter(|l| !l.trim().is_empty()).collect();
    lines[lines.len().saturating_sub(count)..].iter().map(|l| l.to_string()).collect()
}

/// The next speed cap up or down from `current` in [`CAPS_MBPS`].
pub fn step_cap(current: u32, up: bool) -> u32 {
    let at = CAPS_MBPS.iter().position(|&c| c >= current).unwrap_or(CAPS_MBPS.len() - 1);
    // "No cap" (0) sits above the fastest cap, so stepping up from 1000 lifts the cap.
    match (current, up) {
        (0, true) => 0,
        (0, false) => *CAPS_MBPS.last().unwrap(),
        (_, true) if at + 1 >= CAPS_MBPS.len() => 0,
        (_, true) => CAPS_MBPS[at + 1],
        (_, false) => CAPS_MBPS[at.saturating_sub(1).max(1)],
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use duallink_core::{LinkInfo, LinkKind};

    fn link(id: &str, downloaded: u64) -> LinkStatus {
        LinkStatus {
            info: LinkInfo {
                id: id.into(),
                name: id.into(),
                description: String::new(),
                kind: LinkKind::Ethernet,
                if_index: 1,
                address: [10, 0, 0, 2].into(),
                gateway: [10, 0, 0, 1].into(),
            },
            enabled: true,
            weight: 1,
            limit_mbps: 0,
            healthy: true,
            active_connections: 0,
            total_connections: 0,
            failures: 0,
            latency_ms: None,
            downloaded,
            uploaded: 0,
        }
    }

    fn report(seen: u64, uptime_ms: u64, links: Vec<LinkStatus>, config: Config) -> Report {
        Report { seen, result: Ok(Status { uptime_ms, links, config, ..Status::default() }) }
    }

    #[test]
    fn speeds_come_from_two_reports() {
        let mut ui = Ui::default();
        ui.take(report(0, 1000, vec![link("a", 0), link("b", 0)], Config::default()));
        assert!(ui.speeds.is_empty(), "one report says nothing about speed");
        ui.take(report(0, 2000, vec![link("a", 12_500_000), link("b", 2_500_000)], Config::default()));
        assert_eq!(ui.speeds["a"].down, 100.0);
        assert_eq!(ui.speeds["b"].down, 20.0);
        assert_eq!(ui.total().down, 120.0);
        assert_eq!((ui.history.len(), ui.peak), (1, 120.0));
    }

    #[test]
    fn a_change_in_flight_is_not_overwritten_by_an_older_report() {
        let mut ui = Ui::default();
        ui.config.enabled = true;
        ui.send();
        ui.take(report(0, 1000, vec![], Config::default()));
        assert!(ui.config.enabled, "the service had not seen the change yet");
        ui.take(report(1, 2000, vec![], Config { enabled: true, ..Config::default() }));
        assert!(ui.config.enabled);
    }

    #[test]
    fn a_refused_change_falls_back_to_what_the_service_has() {
        let mut ui = Ui::default();
        ui.config.enabled = true;
        ui.send();
        ui.take(Report { seen: 1, result: Err("A program entry is invalid.".into()) });
        assert_eq!(ui.refused.as_deref(), Some("A program entry is invalid."));
        ui.take(report(1, 1000, vec![], Config::default()));
        assert!(!ui.config.enabled && ui.refused.is_none());
    }

    #[test]
    fn links_can_be_reordered_and_missing_ones_keep_their_settings() {
        let mut ui = Ui::default();
        ui.config.links = vec![LinkChoice { weight: 7, ..LinkChoice::new("unplugged") }];
        ui.take(report(0, 1000, vec![link("a", 0), link("b", 0)], ui.config.clone()));
        ui.raise_link("b");
        let ids: Vec<&str> = ui.config.links.iter().map(|c| c.id.as_str()).collect();
        assert_eq!(ids, ["b", "a", "unplugged"]);
        assert_eq!(ui.config.links[2].weight, 7);
        assert_eq!(ui.ordered()[0].info.id, "b");
    }

    #[test]
    fn programs_get_their_extension_and_are_not_repeated() {
        let mut ui = Ui::default();
        ui.add_program(" steam ");
        ui.add_program("Steam.exe");
        ui.add_program(r#""C:\Tools\fetch.exe""#);
        assert_eq!(ui.config.excluded, ["steam.exe", r"C:\Tools\fetch.exe"]);
    }

    #[test]
    fn caps_step_through_the_presets() {
        assert_eq!(step_cap(0, false), 1000);
        assert_eq!(step_cap(1000, true), 0);
        assert_eq!(step_cap(50, true), 100);
        assert_eq!(step_cap(50, false), 25);
        assert_eq!(step_cap(5, false), 5);
        assert_eq!(step_cap(0, true), 0);
    }
}
