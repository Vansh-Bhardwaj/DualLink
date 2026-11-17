//! How the window is drawn.
//!
//! The window is built round one picture: each connection is a stream of its own colour, and
//! the streams run together into one thick stream that ends at the combined speed. The
//! thicker a stream, the more it is carrying right now.

use crate::state::{HISTORY, Speed, Ui, step_cap};
use crate::theme::{Theme, theme};
use duallink_core::{Config, EngineState, LinkInfo, LinkKind, LinkStatus, Mode, Scope, Status};
use rune::{Align, Color, Cx, El, FileDialogOptions, LineCap, Path, ScrollSpec, Shape, Tx, canvas, col, el, row, text};
use rune_kit::{Button, ButtonVariant, ControlSize, SegmentedControl, Switch};

const MODES: [(Mode, &str, &str); 3] = [
    (Mode::Smart, "Smart", "Uses whichever connection is free and quick, and keeps each site on one connection so sign-ins stay put"),
    (Mode::Balanced, "Even", "Shares traffic in turn, following each connection's share"),
    (Mode::Backup, "Backup", "Uses the first connection; the others step in only if it drops"),
];
/// Height each connection gets in the picture.
const LANE: f32 = 64.0;
const NODES: f32 = 290.0;
const READOUT: f32 = 270.0;

fn mbps(value: f32) -> String {
    if value >= 100.0 { format!("{value:.0}") } else { format!("{value:.1}") }
}

fn bytes(value: u64) -> String {
    const UNITS: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];
    let mut amount = value as f64;
    let mut unit = 0;
    while amount >= 1000.0 && unit < UNITS.len() - 1 {
        amount /= 1000.0;
        unit += 1;
    }
    if unit == 0 { format!("{amount:.0} {}", UNITS[unit]) } else { format!("{amount:.1} {}", UNITS[unit]) }
}

pub fn view(s: &Ui, cx: &mut Cx<Ui>) -> El {
    let mut t = theme(cx.system_dark(), cx.env().scale);
    t.still = cx.system_reduced_motion();
    s.flow.start();
    let body = col()
        .key("window")
        .w_full()
        .h_full()
        .fill(t.ground)
        .child(header(s, &t))
        .children(notice(s, &t))
        .child(
            col()
                .key("body")
                .grow(1.0)
                .min_h(0.0)
                .px(20.0)
                .pb(20.0)
                .gap(14.0)
                .child(stage(s, &t))
                .children(link_settings(s, &t))
                .child(row().key("controls").gap(14.0).items_start().child(scope_panel(s, &t)).child(sharing_panel(s, &t))),
        );
    rune_kit::root(body, Vec::new())
}

fn panel(key: &str, t: &Theme) -> El {
    col().key(key).radius(16.0).clip_rounded(16.0).fill(t.panel).border(t.hair, t.line)
}

fn label(value: &str, t: &Theme) -> El {
    text(value.to_uppercase()).font(t.ty.label).color(t.ink_faint)
}

/// The engine's state as one word and a colour.
fn summary(s: &Ui, t: &Theme) -> (&'static str, Color) {
    if s.offline.is_some() {
        return ("Unavailable", t.bad);
    }
    match s.status.as_ref().map(|status| status.state) {
        None => ("Connecting", t.ink_faint),
        Some(EngineState::Off) => ("Off", t.ink_faint),
        Some(EngineState::Starting) => ("Starting", t.warn),
        Some(EngineState::On) => ("Combining", t.accent),
        Some(EngineState::NoLinks) => ("No connection", t.warn),
        Some(EngineState::Failed) => ("Couldn't start", t.bad),
    }
}

fn header(s: &Ui, t: &Theme) -> El {
    let (word, color) = summary(s, t);
    row()
        .key("header")
        .h(60.0)
        .px(24.0)
        .center_y()
        .gap(10.0)
        .child(mark(t))
        .child(text("DualLink").key("name").font(t.ty.title).color(t.ink))
        .child(el().key("gap").grow(1.0))
        .child(
            row()
                .key("state")
                .center_y()
                .gap(8.0)
                .child(el().key("dot").size(8.0, 8.0).radius(4.0).fill(color).shadow(0.0, 10.0, color.alpha(0.7)).a11y_hidden())
                .child(label(word, t).key("word").color(t.ink_soft)),
        )
        .child(
            Switch::new(s.config.enabled)
                .label("Combine connections")
                .hide_label()
                .disabled(s.offline.is_some())
                .on_toggle(|s: &mut Ui| {
                    s.config.enabled = !s.config.enabled;
                    s.send();
                })
                .build(&t.k),
        )
}

/// DualLink's mark: two paths running together into one.
fn mark(t: &Theme) -> El {
    let (top, bottom) = (t.link(1), t.link(0));
    let shape = Shape::new()
        .stroke_path(&Path::new().move_to([10.0, 14.0]).cubic_to([19.0, 14.0], [19.0, 24.0], [27.0, 24.0]), 5.5, LineCap::Round, top)
        .stroke_path(&Path::new().move_to([10.0, 34.0]).cubic_to([19.0, 34.0], [19.0, 24.0], [27.0, 24.0]), 5.5, LineCap::Round, bottom)
        .line([27.0, 24.0], [38.0, 24.0], 5.5, LineCap::Round, t.ink)
        .view_box(48.0, 48.0);
    el().key("mark").size(26.0, 26.0).radius(7.0).fill(t.raised).border(t.hair, t.line).shape(shape).a11y_hidden()
}

/// A line under the header, only when something needs the user's attention.
fn notice(s: &Ui, t: &Theme) -> Option<El> {
    let problem = s.status.as_ref().and_then(|status| status.problem.clone());
    let (message, color) = if s.offline.is_some() {
        ("The DualLink service isn't running. Restart the PC or run setup again.".to_string(), t.bad)
    } else if let Some(refused) = &s.refused {
        (format!("Not saved. {refused}"), t.warn)
    } else {
        let problem = problem?;
        (problem, if s.state() == EngineState::Failed { t.bad } else { t.warn })
    };
    Some(
        row().key("notice").px(20.0).pb(12.0).child(
            row()
                .key("box")
                .w_full()
                .px(14.0)
                .py(10.0)
                .gap(10.0)
                .center_y()
                .radius(10.0)
                .fill(color.alpha(0.12))
                .border(t.hair, color.alpha(0.45))
                .live_region(rune::LiveRegion::Polite)
                .child(el().key("mark").size(6.0, 6.0).radius(3.0).fill(color).a11y_hidden())
                .child(text(message).key("message").font(t.ty.body).color(t.ink).wrap()),
        ),
    )
}

/// The picture: connections on the left, their streams running together, the combined speed.
fn stage(s: &Ui, t: &Theme) -> El {
    let links = s.ordered();
    let lanes = links.len().max(1);
    let height = (lanes as f32 * LANE).max(236.0);
    let total = s.total();
    let on = s.state() == EngineState::On;

    let mut nodes = col().key("nodes").w(NODES).h(height);
    if links.is_empty() {
        let message = if s.status.is_some() { "Plug in a cable, join Wi-Fi\nor tether a phone" } else { "Looking for connections" };
        nodes = nodes.child(
            col().key("empty").grow(1.0).center().child(text(message).font(t.ty.body).color(t.ink_faint).align(Align::Center).wrap()),
        );
    }
    for (index, link) in links.iter().enumerate() {
        nodes = nodes.child(node(s, t, link, index));
    }

    let readout = col()
        .key("readout")
        .w(READOUT)
        .h(height)
        .justify_center()
        .items_end()
        .pr(30.0)
        .gap(2.0)
        .label(format!("{} megabits per second combined", mbps(total.down)))
        .child(text(mbps(total.down)).key("speed").font(t.ty.display).color(if on { t.ink } else { t.ink_faint }).a11y_hidden())
        .child(label("Mbps", t).key("unit").a11y_hidden())
        .child(
            row()
                .key("more")
                .gap(14.0)
                .pt(12.0)
                .child(text(format!("↑ {}", mbps(total.up))).key("up").font(t.ty.body).color(t.ink_soft).help("Upload speed"))
                .child(text(format!("▲ {}", mbps(s.peak))).key("peak").font(t.ty.body).color(t.ink_faint).help("Fastest so far")),
        );

    panel("stage", t)
        .child(
            row()
                .key("flow")
                .pt(18.0)
                .pb(6.0)
                .pl(10.0)
                .child(nodes)
                .child(streams(s, t, &links, height))
                .child(readout),
        )
        .child(canvas(minute(s, t)).key("minute").w_full().h(44.0).a11y_hidden())
}

/// One connection at the head of its stream.
fn node(s: &Ui, t: &Theme, link: &LinkStatus, index: usize) -> El {
    let id = link.info.id.clone();
    let choice = s.config.choice(&id);
    let speed = s.speeds.get(&id).copied().unwrap_or(Speed::default());
    let open = s.expanded.as_deref() == Some(id.as_str());
    let color = if !choice.enabled {
        t.ink_faint
    } else if !link.healthy {
        t.bad
    } else {
        t.link(index)
    };
    // A connection Windows already calls "Wi-Fi" doesn't need telling that it is Wi-Fi.
    let mut facts = Vec::new();
    if !link_title(&link.info).eq_ignore_ascii_case(link.info.kind.label()) && link.info.kind != LinkKind::UsbTether {
        facts.push(link.info.kind.label().to_string());
    }
    if !link.healthy && choice.enabled {
        facts.push("not responding".into());
    } else if let Some(ms) = link.latency_ms {
        facts.push(format!("{ms:.0} ms"));
    }
    let (toggle_id, open_id) = (id.clone(), id.clone());
    row()
        .key(format!("link-{id}"))
        .grow(1.0)
        .min_h(0.0)
        .center_y()
        .gap(10.0)
        .child(
            row()
                .key("tile")
                .grow(1.0)
                .min_w(0.0)
                .h(48.0)
                .pl(12.0)
                .pr(12.0)
                .gap(12.0)
                .center_y()
                .radius(12.0)
                .fill(if open { t.raised } else { Color::TRANSPARENT })
                .hover(t.k.c.hover)
                .press(t.k.c.pressed)
                .focusable(true)
                .role("button")
                .label(format!("{}, {} megabits per second. Settings", link_title(&link.info), mbps(speed.down)))
                .expanded(open)
                .on_press(move |s: &mut Ui| {
                    s.expanded = if s.expanded.as_deref() == Some(open_id.as_str()) { None } else { Some(open_id.clone()) };
                })
                .child(el().key("colour").size(10.0, 10.0).radius(5.0).fill(color).shadow(0.0, 8.0, color.alpha(0.55)).a11y_hidden())
                .child(
                    col()
                        .key("names")
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(1.0)
                        .child(
                            text(link_title(&link.info))
                                .key("name")
                                .font(t.ty.title)
                                .color(if choice.enabled { t.ink } else { t.ink_faint })
                                .truncate(),
                        )
                        .child(text(facts.join("  ·  ")).key("facts").font(t.ty.body).color(t.ink_faint).truncate()),
                )
                .child(text(mbps(speed.down)).key("speed").font(t.ty.figure).color(if choice.enabled { t.ink } else { t.ink_faint })),
        )
        .child(
            Switch::new(choice.enabled)
                .label(format!("Use {}", link_title(&link.info)))
                .hide_label()
                .on_toggle(move |s: &mut Ui| s.edit_link(&toggle_id, |c| c.enabled = !c.enabled))
                .build(&t.k),
        )
}

fn link_title(info: &LinkInfo) -> String {
    match info.kind {
        LinkKind::UsbTether => "Phone (USB)".into(),
        _ => info.name.clone(),
    }
}

/// Width of the picture's drawing space; it stretches to fit.
const W: f32 = 400.0;
/// Where the lines meet, as a share of the width.
const JOIN: f32 = 0.66;

/// A connection's line: from its row on the left, curving into the meeting point. With
/// `onward` it carries on to the right edge, which is the way its pulses travel.
fn line(index: usize, count: usize, height: f32, onward: bool) -> Path {
    let lane = height / count as f32;
    let (from, mid) = (lane * (index as f32 + 0.5), height / 2.0);
    let join = W * JOIN;
    let path = Path::new().move_to([0.0, from]).cubic_to([join * 0.55, from], [join * 0.45, mid], [join, mid]);
    if onward { path.line_to([W, mid]) } else { path }
}

/// The lines. Each connection is a thin line in its own colour running into one line that
/// ends at the combined speed, with pulses of light travelling along it while it carries
/// traffic: the busier the connection, the faster they go.
fn streams(s: &Ui, t: &Theme, links: &[&LinkStatus], height: f32) -> El {
    let flowing = s.state() == EngineState::On;
    let speeds: Vec<f32> = links
        .iter()
        .map(|l| if s.config.choice(&l.info.id).enabled { s.speeds.get(&l.info.id).map_or(0.0, |v| v.down) } else { 0.0 })
        .collect();
    let total: f32 = speeds.iter().sum();
    let moving = flowing && total > 0.05;
    s.flow.set_speeds(&speeds, speeds.iter().copied().fold(0.0, f32::max), moving && !t.still);

    let mut base = Shape::new();
    let count = links.len().max(1);
    for (index, link) in links.iter().enumerate() {
        let enabled = s.config.choice(&link.info.id).enabled;
        let share = if total > 0.05 { speeds[index] / total } else { 0.0 };
        // Mixed into the panel rather than see-through: a see-through stroke shows its joints.
        let color = if !enabled {
            t.ink.mix(t.panel, 0.9)
        } else {
            t.link(index).mix(t.panel, if flowing { 0.65 - 0.45 * share } else { 0.75 })
        };
        base = base.stroke_path(&line(index, count, height, false), 1.25 + 1.5 * share, LineCap::Round, color);
    }
    let mid = height / 2.0;
    if !links.is_empty() {
        let out = if moving { t.ink.mix(t.panel, 0.45) } else { t.ink.mix(t.panel, 0.82) };
        base = base.line([W * JOIN, mid], [W, mid], if moving { 2.0 } else { 1.25 }, LineCap::Round, out);
        // A small light where the lines meet.
        let ring: Vec<[f32; 2]> = (0..24)
            .map(|k| {
                let a = k as f32 / 24.0 * std::f32::consts::TAU;
                [W * JOIN + 3.2 * a.cos(), mid + 3.2 * a.sin()]
            })
            .collect();
        base = base.fill(ring, if moving { t.accent } else { t.ink_faint });
    }

    let mut layer = el().key("streams").grow(1.0).min_w(60.0).h(height).a11y_hidden().child(
        canvas(base.view_box(W, height)).key("lines").absolute(0.0, 0.0).w_full().h_full(),
    );
    if moving {
        for (index, link) in links.iter().enumerate().take(duallink_core::MAX_LINKS) {
            if speeds[index] <= 0.05 {
                continue;
            }
            let pulse = Shape::new().stroke_path(&line(index, count, height, true), 2.5, LineCap::Round, t.link(index));
            for n in 0..crate::flow::PULSES {
                layer = layer.child(
                    canvas(pulse.clone().view_box(W, height))
                        .key(format!("pulse-{}-{n}", link.info.id))
                        .absolute(0.0, 0.0)
                        .w_full()
                        .h_full()
                        .live_trim(&s.flow.pulses[index * crate::flow::PULSES + n]),
                );
            }
        }
    }
    layer
}
/// The last minute of combined speed, as a quiet strip along the bottom of the picture.
fn minute(s: &Ui, t: &Theme) -> Shape {
    const W: f32 = 900.0;
    const H: f32 = 54.0;
    let mut shape = Shape::new();
    let ids: Vec<&str> = s.ordered().iter().map(|l| l.info.id.as_str()).collect();
    if s.history.len() >= 2 && !ids.is_empty() {
        let highest = s.history.iter().map(|sample| sample.iter().map(|(_, v)| *v).sum::<f32>()).fold(1.0f32, f32::max) * 1.15;
        let step = W / (HISTORY - 1) as f32;
        let start = HISTORY - s.history.len();
        let x = |i: usize| (start + i) as f32 * step;
        let y = |value: f32| H - (value / highest) * (H - 4.0);
        let mut below = vec![0.0f32; s.history.len()];
        for (index, id) in ids.iter().enumerate() {
            let above: Vec<f32> = s
                .history
                .iter()
                .zip(&below)
                .map(|(sample, base)| base + sample.iter().find(|(link, _)| link == id).map_or(0.0, |(_, v)| *v))
                .collect();
            let edge: Vec<[f32; 2]> = above.iter().enumerate().map(|(i, v)| [x(i), y(*v)]).collect();
            shape = shape.stroke(edge, 1.25, t.link(index).mix(t.panel, 0.55));
            below = above;
        }
    } else {
        shape = shape.line([0.0, H - 1.0], [W, H - 1.0], 1.0, LineCap::Butt, t.line);
    }
    shape.view_box(W, H)
}

fn stepper(t: &Theme, key: &str, name: &str, value: String, change: impl Fn(&mut Ui, bool) + Clone + 'static) -> El {
    let (less, more) = (change.clone(), change);
    row()
        .key(key)
        .center_y()
        .gap(6.0)
        .child(label(name, t).key("label").mr(4.0))
        .child(
            Button::new("−")
                .control_size(ControlSize::Small)
                .variant(ButtonVariant::Ghost)
                .help(format!("Lower {}", name.to_lowercase()))
                .on_press(move |s: &mut Ui| less(s, false))
                .build(&t.k),
        )
        .child(text(value).key("value").font(t.ty.body).color(t.ink).min_w(70.0).align(Align::Center))
        .child(
            Button::new("+")
                .control_size(ControlSize::Small)
                .variant(ButtonVariant::Ghost)
                .help(format!("Raise {}", name.to_lowercase()))
                .on_press(move |s: &mut Ui| more(s, true))
                .build(&t.k),
        )
}

/// The settings of the connection the user opened, in a strip under the picture.
fn link_settings(s: &Ui, t: &Theme) -> Option<El> {
    let links = s.ordered();
    let index = links.iter().position(|l| Some(l.info.id.as_str()) == s.expanded.as_deref())?;
    let link = links[index];
    let choice = s.config.choice(&link.info.id);
    let (share_id, cap_id, raise_id) = (link.info.id.clone(), link.info.id.clone(), link.info.id.clone());
    let mut strip = row()
        .key("link-settings")
        .radius(14.0)
        .fill(t.panel)
        .border(t.hair, t.line)
        .px(16.0)
        .h(52.0)
        .center_y()
        .gap(22.0)
        .child(
            row()
                .key("which")
                .center_y()
                .gap(9.0)
                .child(el().key("colour").size(8.0, 8.0).radius(4.0).fill(t.link(index)).a11y_hidden())
                .child(text(link_title(&link.info)).key("name").font(t.ty.title).color(t.ink)),
        )
        .child(stepper(t, "share", "Share", format!("{} / 10", choice.weight), move |s, up| {
            s.edit_link(&share_id, |c| c.weight = if up { (c.weight + 1).min(10) } else { c.weight.saturating_sub(1).max(1) });
        }))
        .child(stepper(
            t,
            "cap",
            "Limit",
            if choice.limit_mbps == 0 { "None".into() } else { format!("{} Mbps", choice.limit_mbps) },
            move |s, up| s.edit_link(&cap_id, |c| c.limit_mbps = step_cap(c.limit_mbps, up)),
        ));
    if index > 0 {
        strip = strip.child(
            Button::new("Move up")
                .control_size(ControlSize::Small)
                .variant(ButtonVariant::Ghost)
                .help("Connections higher up are tried first")
                .on_press(move |s: &mut Ui| s.raise_link(&raise_id))
                .build(&t.k),
        );
    }
    Some(
        strip.child(el().key("gap").grow(1.0)).child(
            text(format!("↓ {}   ↑ {}", bytes(link.downloaded), bytes(link.uploaded)))
                .key("used")
                .font(t.ty.body)
                .color(t.ink_faint)
                .help(format!("{} · {}", link.info.description, link.info.address)),
        ),
    )
}

fn scope_panel(s: &Ui, t: &Theme) -> El {
    let everything = s.config.scope == Scope::Everything;
    let mut tiles = row().key("tiles").flex_wrap().gap(4.0).w_full();
    for (index, entry) in s.programs().iter().enumerate() {
        let info = s.info(entry);
        let name = info.map_or_else(|| crate::apps::display_name(entry), |app| app.name.clone());
        let (state, hint) = if everything { ("left alone", "Left alone · click to combine") } else { ("combined", "Combined · click to remove") };
        tiles = tiles.child(
            app_tile(t, format!("program-{index}"), info, &name, if everything { Tile::Excluded } else { Tile::Chosen })
                .label(format!("{name}, {state}"))
                .help(hint)
                .on_press(move |s: &mut Ui| s.remove_program(index)),
        );
    }
    for app in s.suggestions() {
        let path = app.path.clone();
        tiles = tiles.child(
            app_tile(t, format!("open-{}", app.path), Some(app), &app.name, Tile::Offered)
                .label(format!("{}, open now. {}", app.name, if everything { "Leave alone" } else { "Combine" }))
                .help(if everything { "Open now · click to leave alone" } else { "Open now · click to combine" })
                .on_press(move |s: &mut Ui| s.add_program(&path)),
        );
    }
    tiles = tiles.child(
        col()
            .key("browse")
            .w(84.0)
            .py(8.0)
            .gap(6.0)
            .items_center()
            .radius(12.0)
            .hover(t.k.c.hover)
            .press(t.k.c.pressed)
            .focusable(true)
            .role("button")
            .label("Choose a program")
            .help("Choose a program")
            .on_press_tx(|_: &mut Ui, tx: &mut Tx| {
                let options = FileDialogOptions { title: "Choose a program".into(), ..Default::default() }.filter("Programs", &["exe"]);
                tx.open_file(options, |s: &mut Ui, picked, _| {
                    if let Ok(Some(path)) = picked {
                        s.add_program(&path.to_string_lossy());
                    }
                });
            })
            .child(
                el().key("plus")
                    .size(36.0, 36.0)
                    .radius(10.0)
                    .border(1.5, t.line)
                    .border_dash(4.0, 3.0)
                    .center()
                    .child(text("+").font(t.ty.figure).color(t.ink_faint)),
            )
            .child(el().key("spacer").h(15.0)),
    );
    panel("scope", t)
        .grow(1.0)
        .min_w(0.0)
        .padding(16.0)
        .gap(10.0)
        .child(
            row().key("top").center_y().gap(12.0).child(label("Combine", t).key("label")).child(el().key("gap").grow(1.0)).child(
                SegmentedControl::new(&["Everything", "Chosen"], Some(usize::from(!everything)))
                    .label("Combine everything or only chosen programs")
                    .fit()
                    .on_pick(|s: &mut Ui, pick, _: &mut Tx| {
                        let scope = if pick == 0 { Scope::Everything } else { Scope::Chosen };
                        if s.config.scope != scope {
                            s.config.scope = scope;
                            s.send();
                        }
                    })
                    .build(&t.k),
            ),
        )
        .child(el().key("scroll").max_h(176.0).scroll(ScrollSpec { stick_bottom: false }).child(tiles))
}

#[derive(Clone, Copy, PartialEq)]
enum Tile {
    /// Listed, and combined.
    Chosen,
    /// Listed, and left alone.
    Excluded,
    /// Open now, not listed.
    Offered,
}

/// A program as its icon and name.
fn app_tile(t: &Theme, key: String, info: Option<&crate::apps::AppInfo>, name: &str, kind: Tile) -> El {
    let picture = match info.and_then(|app| app.icon.as_ref()) {
        Some(pixels) => el().key("icon").size(32.0, 32.0).pixels(pixels),
        None => el()
            .key("icon")
            .size(32.0, 32.0)
            .radius(9.0)
            .fill(t.raised)
            .center()
            .child(text(name.chars().next().unwrap_or('?').to_uppercase().to_string()).font(t.ty.title).color(t.ink_soft)),
    };
    let badge = match kind {
        Tile::Chosen => Some((t.accent, "✓")),
        Tile::Excluded => Some((t.bad, "–")),
        Tile::Offered => None,
    };
    let mut icon = el().key("well").size(36.0, 36.0).center().child(picture.opacity(if kind == Tile::Excluded { 0.45 } else { 1.0 }));
    if let Some((color, mark)) = badge {
        icon = icon.child(
            el().key("badge")
                .size(15.0, 15.0)
                .radius(7.5)
                .absolute(25.0, -3.0)
                .fill(color)
                .border(2.0, t.panel)
                .center()
                .a11y_hidden()
                .child(text(mark).font(t.ty.label).color(t.ground)),
        );
    }
    col()
        .key(key)
        .w(84.0)
        .py(8.0)
        .gap(6.0)
        .items_center()
        .radius(12.0)
        .opacity(if kind == Tile::Offered { 0.5 } else { 1.0 })
        .hover(t.k.c.hover)
        .press(t.k.c.pressed)
        .focusable(true)
        .role("button")
        .child(icon)
        .child(text(name).key("name").font(t.ty.label_case).color(t.ink_soft).w(78.0).align(Align::Center).wrap().max_lines(2).a11y_hidden())
}
fn sharing_panel(s: &Ui, t: &Theme) -> El {
    let current = MODES.iter().position(|(mode, _, _)| *mode == s.config.mode).unwrap_or(0);
    let names: Vec<&str> = MODES.iter().map(|(_, name, _)| *name).collect();
    let option = |key: &str, name: &str, hint: &str, on: bool, flip: fn(&mut Config)| {
        row()
            .key(key)
            .center_y()
            .gap(10.0)
            .child(text(name).key("name").font(t.ty.body).color(t.ink_soft).grow(1.0).help(hint).a11y_hidden())
            .child(
                Switch::new(on)
                    .label(name)
                    .hide_label()
                    .on_toggle(move |s: &mut Ui| {
                        flip(&mut s.config);
                        s.send();
                    })
                    .build(&t.k),
            )
    };
    panel("sharing", t)
        .w(330.0)
        .padding(16.0)
        .gap(12.0)
        .child(
            row()
                .key("top")
                .center_y()
                .gap(12.0)
                .child(label("Share", t).key("label").help(MODES[current].2))
                .child(el().key("gap").grow(1.0))
                .child(
                    SegmentedControl::new(&names, Some(current))
                        .label("How traffic is shared between connections")
                        .fit()
                        .on_pick(|s: &mut Ui, pick, _: &mut Tx| {
                            let mode = MODES[pick.min(MODES.len() - 1)].0;
                            if s.config.mode != mode {
                                s.config.mode = mode;
                                s.send();
                            }
                        })
                        .build(&t.k),
                ),
        )
        .child(option(
            "quic",
            "Boost browsers",
            "Keeps browsers on the kind of traffic DualLink can spread across connections",
            s.config.steer_quic,
            |c| c.steer_quic = !c.steer_quic,
        ))
        .child(option(
            "ipv4",
            "Prefer IPv4",
            "IPv4 is the kind of traffic DualLink can spread across connections",
            s.config.prefer_ipv4,
            |c| c.prefer_ipv4 = !c.prefer_ipv4,
        ))
}

/// A named state the window can be put in.
pub type Scene = (&'static str, fn(&mut Ui));

/// States the window can be shown in without a service, for checking how it looks.
pub const SCENES: &[Scene] = &[
    ("on", |s| {
        demo(s, EngineState::On, 3);
        s.config.excluded = vec![DEMO_PROGRAMS[3].into()];
        known(s, DEMO_PROGRAMS);
        s.open_now = DEMO_PROGRAMS[..3].iter().map(|p| p.to_lowercase()).collect();
    }),
    ("four", |s| demo(s, EngineState::On, 4)),
    ("off", |s| demo(s, EngineState::Off, 2)),
    ("open", |s| {
        demo(s, EngineState::On, 3);
        s.expanded = Some("{B}".into());
    }),
    ("chosen", |s| {
        demo(s, EngineState::On, 2);
        s.config.scope = Scope::Chosen;
        s.config.apps = DEMO_PROGRAMS.iter().map(|p| p.to_string()).collect();
        known(s, DEMO_PROGRAMS);
    }),
    ("no-links", |s| demo(s, EngineState::NoLinks, 0)),
    ("failed", |s| {
        demo(s, EngineState::Failed, 2);
        if let Some(status) = &mut s.status {
            status.problem = Some("The network driver is not installed or could not be opened.".into());
        }
    }),
    ("offline", |s| s.offline = Some(crate::client::NOT_RUNNING.into())),
];

const DEMO_PROGRAMS: &[&str] = &[
    r"C:\Program Files (x86)\Steam\steam.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\BraveSoftware\Brave-Origin\Application\brave.exe",
    r"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
];

/// Reads programs' names and icons right away, as the watching thread would.
fn known(s: &mut Ui, paths: &[&str]) {
    for path in paths {
        let app = crate::apps::AppInfo::read(path);
        s.apps.insert(app.path.clone(), app);
    }
}

fn demo(s: &mut Ui, state: EngineState, count: usize) {
    let all = [
        ("{A}", "Ethernet", "Realtek PCIe GbE Family Controller", LinkKind::Ethernet, [192, 168, 1, 20], 14.0, 6.2),
        ("{B}", "Wi-Fi", "Intel(R) Wi-Fi 6 AX200 160MHz", LinkKind::Wifi, [192, 168, 0, 104], 31.0, 3.4),
        ("{C}", "Ethernet 3", "Remote NDIS based Internet Sharing Device", LinkKind::UsbTether, [192, 168, 42, 87], 48.0, 2.1),
        ("{D}", "Wi-Fi 2", "TP-Link Wireless USB Adapter", LinkKind::Wifi, [10, 20, 0, 14], 39.0, 1.4),
    ];
    let on = state == EngineState::On;
    let config = Config { enabled: state != EngineState::Off, ..Config::default() };
    let link = |index: usize, downloaded: u64| {
        let (id, name, description, kind, address, latency, _) = all[index];
        LinkStatus {
            info: LinkInfo {
                id: id.into(),
                name: name.into(),
                description: description.into(),
                kind,
                if_index: index as u32 + 1,
                address: address.into(),
                gateway: [address[0], address[1], address[2], 1].into(),
            },
            enabled: true,
            weight: 1,
            limit_mbps: 0,
            healthy: true,
            active_connections: if on { 6 - index as u32 } else { 0 },
            total_connections: downloaded / 3_000_000,
            failures: 0,
            latency_ms: on.then_some(latency),
            downloaded,
            uploaded: downloaded / 16,
        }
    };
    // A minute of reports, so the picture and the speeds are drawn from real arithmetic.
    let seconds = if on { HISTORY as u64 + 1 } else { 1 };
    let mut downloaded = vec![0u64; count];
    for second in 0..seconds {
        let links = (0..count)
            .map(|index| {
                // Megabytes a second, rising and falling at a different pace on each link.
                let rate = all[index].6 * (1.0 + 0.45 * ((second as f32 * 0.21) + index as f32 * 1.9).sin());
                downloaded[index] += (rate * 1_000_000.0) as u64;
                link(index, downloaded[index])
            })
            .collect::<Vec<_>>();
        let status = Status {
            version: env!("CARGO_PKG_VERSION").into(),
            state,
            config: config.clone(),
            active_connections: links.iter().map(|l| l.active_connections).sum(),
            links,
            uptime_ms: second * 1000,
            ..Status::default()
        };
        s.take(crate::state::Report { seen: 0, result: Ok(status) });
    }
}
