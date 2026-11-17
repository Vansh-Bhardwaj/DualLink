//! How the window is drawn.
//!
//! A sidebar lists the connections on the window's ground; the main panel is raised beside it.
//! At the top of the panel each connection is a thin line in its own colour running into one
//! line that ends at the combined speed, with pulses travelling along each line while it
//! carries traffic. Settings sit below in grouped cards.

use crate::fx::metal;
use crate::state::{HISTORY, Speed, Ui, step_cap};
use crate::theme::{Theme, theme};
use duallink_core::{Config, EngineState, LinkInfo, LinkKind, LinkStatus, Mode, Scope, Status};
use rune::tok::motion;
use rune::{Align, Color, Cx, El, FileDialogOptions, LineCap, Path, ScrollSpec, Shape, Tx, canvas, col, el, row, text};
use std::sync::atomic::Ordering;

const MODES: [(Mode, &str); 3] = [(Mode::Smart, "Smart"), (Mode::Balanced, "Even"), (Mode::Backup, "Backup")];
const SIDEBAR: f32 = 264.0;
/// Height of the picture.
const PICTURE: f32 = 232.0;
/// Width of the picture's drawing space; it stretches to fit.
const W: f32 = 400.0;
/// Where the lines meet, as a share of the width.
const JOIN: f32 = 0.62;

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
    s.metal_moving.store(s.config.enabled && s.offline.is_none() && !t.still, Ordering::Relaxed);
    let body = row()
        .key("window")
        .w_full()
        .h_full()
        .fill(t.ground)
        .child(sidebar(s, &t))
        .child(main_panel(s, &t));
    rune_kit::root(body, Vec::new())
}

// ---------------------------------------------------------------------------------------------
// Controls, drawn the way the platform draws them.

/// An on/off switch.
fn toggle(t: &Theme, key: &str, label: &str, on: bool, flip: impl Fn(&mut Ui) + 'static) -> El {
    let (w, h, k) = (38.0, 22.0, 18.0);
    let inset = (h - k) / 2.0;
    el().key(key.to_string())
        .role("switch")
        .label(label.to_string())
        .checked(on)
        .focusable(true)
        .size(w, h)
        .radius(h / 2.0)
        .fill(if on { t.accent } else { t.control_off })
        .motion(motion::SNAPPY)
        .on_press(flip)
        .child(
            el().key("knob")
                .size(k, k)
                .absolute(if on { w - k - inset } else { inset }, inset)
                .radius(k / 2.0)
                .fill(t.knob)
                .shadow(1.0, 3.0, t.knob_shadow)
                .shadow(0.0, 0.5, t.edge)
                .a11y_hidden(),
        )
}

/// A row of choices with a sliding indicator under the chosen one.
fn segmented(t: &Theme, key: &str, label: &str, names: &[&str], chosen: usize, each: f32, pick: fn(&mut Ui, usize)) -> El {
    let (h, pad) = (28.0, 2.0);
    let mut segments = row().key("segments");
    for (index, name) in names.iter().enumerate() {
        let selected = index == chosen;
        segments = segments.child(
            el().key(name.to_string())
                .role("segment")
                .label(name.to_string())
                .checked(selected)
                .focusable(true)
                .size(each, h - 2.0 * pad)
                .center()
                .radius(6.0)
                .hover(if selected { Color::TRANSPARENT } else { t.hover })
                .on_press(move |s: &mut Ui| pick(s, index))
                .child(text(name.to_string()).font(t.ty.body_medium).color(if selected { t.ink } else { t.ink_soft }).a11y_hidden()),
        );
    }
    el().key(key.to_string())
        .role("radiogroup")
        .label(label.to_string())
        .size(each * names.len() as f32 + 2.0 * pad, h)
        .padding(pad)
        .radius(8.0)
        .fill(t.track)
        .motion(motion::SNAPPY)
        .child(
            el().key("indicator")
                .size(each, h - 2.0 * pad)
                .absolute(pad + chosen as f32 * each, pad)
                .radius(6.0)
                .fill(t.indicator)
                .shadow(1.0, 3.0, t.shadow)
                .shadow(0.0, 0.5, t.edge)
                .lead(motion::LEAD, motion::DEFAULT)
                .a11y_hidden(),
        )
        .child(segments)
}

/// A small square button with one sign on it.
fn sign_button(t: &Theme, key: &str, sign: &str, hint: String, act: impl Fn(&mut Ui) + 'static) -> El {
    el().key(key.to_string())
        .role("button")
        .label(hint.clone())
        .help(hint)
        .focusable(true)
        .size(26.0, 26.0)
        .radius(7.0)
        .fill(t.track)
        .hover(t.hover)
        .press(t.press)
        .center()
        .on_press(act)
        .child(text(sign.to_string()).font(t.ty.body_medium).color(t.ink).a11y_hidden())
}

/// A heading above a card, as in the platform's settings.
fn section(t: &Theme, key: &str, title: &str, rows: Vec<El>) -> El {
    let mut card = col()
        .key("card")
        .padding(4.0)
        .radius(12.0)
        .fill(t.card)
        .border(t.hair, t.card_border)
        .shadow(1.0, 2.0, t.shadow)
        .shadow(8.0, 24.0, t.shadow_soft);
    let count = rows.len();
    for (index, item) in rows.into_iter().enumerate() {
        card = card.child(item);
        if index + 1 < count {
            card = card.child(el().key(format!("sep{index}")).h(t.hair).mx(12.0).fill(t.separator).a11y_hidden());
        }
    }
    col()
        .key(key.to_string())
        .gap(8.0)
        .child(text(title.to_string()).key("heading").font(t.ty.section).color(t.ink_soft).ml(12.0))
        .child(card)
}

/// One row of a card: a label on the left, its control on the right.
fn setting(t: &Theme, key: &str, label: &str, control: El) -> El {
    row()
        .key(key.to_string())
        .h(44.0)
        .px(12.0)
        .center_y()
        .justify_between()
        .gap(16.0)
        .child(text(label.to_string()).key("label").font(t.ty.body).color(t.ink))
        .child(control)
}

// ---------------------------------------------------------------------------------------------
// Sidebar.

fn sidebar(s: &Ui, t: &Theme) -> El {
    let links = s.ordered();
    let mut list = col().key("links").w_full().gap(2.0);
    if links.is_empty() {
        let message = if s.status.is_some() { "Plug in a cable, join Wi-Fi or tether a phone" } else { "Looking for connections" };
        list = list.child(text(message).key("empty").font(t.ty.small).color(t.ink_faint).wrap().px(10.0).py(8.0));
    }
    for (index, link) in links.iter().enumerate() {
        list = list.child(link_row(s, t, link, index));
    }
    col()
        .key("sidebar")
        .w(SIDEBAR)
        .h_full()
        .px(10.0)
        .pt(14.0)
        .pb(12.0)
        .gap(4.0)
        .child(
            row()
                .key("brand")
                .h(36.0)
                .px(8.0)
                .gap(10.0)
                .center_y()
                .child(mark(t))
                .child(text("DualLink").key("name").font(t.ty.title).color(t.ink)),
        )
        .child(text("Connections").key("heading").font(t.ty.section).color(t.ink_faint).ml(10.0).mt(18.0).mb(4.0))
        .child(el().key("scroll").grow(1.0).min_h(0.0).scroll(ScrollSpec { stick_bottom: false }).child(list))
        .child(master(s, t))
}

/// DualLink's mark: two paths running together into one.
fn mark(t: &Theme) -> El {
    let (top, bottom) = (t.link(1), t.link(0));
    let shape = Shape::new()
        .stroke_path(&Path::new().move_to([10.0, 14.0]).cubic_to([19.0, 14.0], [19.0, 24.0], [27.0, 24.0]), 5.5, LineCap::Round, top)
        .stroke_path(&Path::new().move_to([10.0, 34.0]).cubic_to([19.0, 34.0], [19.0, 24.0], [27.0, 24.0]), 5.5, LineCap::Round, bottom)
        .line([27.0, 24.0], [38.0, 24.0], 5.5, LineCap::Round, t.ink)
        .view_box(48.0, 48.0);
    el().key("mark").size(24.0, 24.0).radius(6.0).fill(t.card).border(t.hair, t.card_border).shape(shape).a11y_hidden()
}

fn link_title(info: &LinkInfo) -> String {
    match info.kind {
        LinkKind::UsbTether => "Phone (USB)".into(),
        _ => info.name.clone(),
    }
}

/// A connection in the sidebar: its colour, name and speed. Pressing it opens its settings.
fn link_row(s: &Ui, t: &Theme, link: &LinkStatus, index: usize) -> El {
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
    let detail = if !choice.enabled {
        "Not used".to_string()
    } else if !link.healthy {
        "Not responding".to_string()
    } else {
        link.latency_ms.map_or_else(|| link.info.kind.label().to_string(), |ms| format!("{ms:.0} ms"))
    };
    let ink = if choice.enabled { t.ink } else { t.ink_faint };
    row()
        .key(format!("link-{id}"))
        .w_full()
        .h(46.0)
        .px(10.0)
        .gap(10.0)
        .center_y()
        .radius(8.0)
        .fill(if open { t.selected } else { Color::TRANSPARENT })
        .hover(t.hover)
        .press(t.press)
        .focusable(true)
        .role("button")
        .label(format!("{}, {} megabits per second, {detail}", link_title(&link.info), mbps(speed.down)))
        .expanded(open)
        .on_press(move |s: &mut Ui| {
            s.expanded = if s.expanded.as_deref() == Some(id.as_str()) { None } else { Some(id.clone()) };
        })
        .child(el().key("colour").size(8.0, 8.0).radius(4.0).fill(color).shadow(0.0, 6.0, color.alpha(0.5)).a11y_hidden())
        .child(
            col()
                .key("names")
                .grow(1.0)
                .min_w(0.0)
                .gap(1.0)
                .child(text(link_title(&link.info)).key("name").font(t.ty.body_medium).color(ink).truncate())
                .child(text(detail).key("detail").font(t.ty.small).color(t.ink_faint).truncate()),
        )
        .child(text(mbps(speed.down)).key("speed").font(t.ty.figure).color(ink).a11y_hidden())
}

/// The one control that matters: combining on or off. While on, it wears a band of
/// polished, slowly moving chrome.
fn master(s: &Ui, t: &Theme) -> El {
    let on = s.config.enabled;
    let available = s.offline.is_none();
    let (word, dot) = summary(s, t);
    let mut pill = row()
        .key("master")
        .h(48.0)
        .w_full()
        .pl(16.0)
        .pr(8.0)
        .gap(10.0)
        .center_y()
        .radius(24.0)
        .fill(t.card)
        .role("switch")
        .label("Combine connections")
        .checked(on)
        .focusable(available)
        .hover(t.hover)
        .press(t.press);
    if available {
        pill = pill.on_press(|s: &mut Ui| {
            s.config.enabled = !s.config.enabled;
            s.send();
        });
    }
    if on && available {
        pill = pill.child(metal(3.5).absolute(0.0, 0.0).w_full().h_full().radius(24.0).a11y_hidden());
    } else {
        pill = pill.border(t.hair, t.card_border);
    }
    pill.child(el().key("dot").size(8.0, 8.0).radius(4.0).fill(dot).shadow(0.0, 8.0, dot.alpha(0.6)).a11y_hidden())
        .child(text(word).key("word").font(t.ty.body_medium).color(t.ink).grow(1.0).a11y_hidden())
        .child(
            el().key("knob-track")
                .size(38.0, 22.0)
                .radius(11.0)
                .fill(if on { t.accent } else { t.control_off })
                .motion(motion::SNAPPY)
                .a11y_hidden()
                .child(
                    el().key("knob")
                        .size(18.0, 18.0)
                        .absolute(if on { 18.0 } else { 2.0 }, 2.0)
                        .radius(9.0)
                        .fill(t.knob)
                        .shadow(1.0, 3.0, t.knob_shadow),
                ),
        )
        .mt(8.0)
}

/// The engine's state in a word or two, and a colour.
fn summary(s: &Ui, t: &Theme) -> (&'static str, Color) {
    if s.offline.is_some() {
        return ("Unavailable", t.bad);
    }
    match s.status.as_ref().map(|status| status.state) {
        None => ("Connecting", t.ink_faint),
        Some(EngineState::Off) => ("Combine", t.ink_faint),
        Some(EngineState::Starting) => ("Starting", t.warn),
        Some(EngineState::On) => ("Combining", t.accent),
        Some(EngineState::NoLinks) => ("No connection", t.warn),
        Some(EngineState::Failed) => ("Couldn't start", t.bad),
    }
}

// ---------------------------------------------------------------------------------------------
// Main panel.

fn main_panel(s: &Ui, t: &Theme) -> El {
    let total = s.total();
    let links = s.ordered();
    let in_use = links.iter().filter(|l| s.config.choice(&l.info.id).enabled).count();
    let title = match in_use {
        0 => "No connections".to_string(),
        1 => "1 connection".to_string(),
        n => format!("{n} connections"),
    };
    let mut content = col().key("content").w_full().px(28.0).pb(28.0).gap(26.0).child(picture(s, t, &links));
    if let Some(card) = link_settings(s, t) {
        content = content.child(card);
    }
    content = content.child(programs(s, t)).child(sharing(s, t));
    col()
        .key("main")
        .grow(1.0)
        .min_w(0.0)
        .mt(8.0)
        .mr(8.0)
        .mb(8.0)
        .radius(14.0)
        .clip_rounded(14.0)
        .fill(t.panel)
        .border(t.hair, t.card_border)
        .child(
            row()
                .key("bar")
                .h(52.0)
                .px(24.0)
                .gap(8.0)
                .center_y()
                .child(text("DualLink").key("crumb").font(t.ty.body).color(t.ink_faint))
                .child(text("/").key("slash").font(t.ty.body).color(t.ink_faint))
                .child(text(title).key("title").font(t.ty.body_medium).color(t.ink))
                .child(el().key("gap").grow(1.0))
                .child(text(format!("↑ {} Mbps", mbps(total.up))).key("up").font(t.ty.body).color(t.ink_soft).help("Upload"))
                .child(text(format!("Peak {}", mbps(s.peak))).key("peak").font(t.ty.body).color(t.ink_faint).ml(12.0)),
        )
        .children(notice(s, t))
        .child(el().key("scroll").grow(1.0).min_h(0.0).scroll(ScrollSpec { stick_bottom: false }).scroll_edge_fade(16.0).child(content))
}

/// A line under the bar, only when something needs the user's attention.
fn notice(s: &Ui, t: &Theme) -> Option<El> {
    let problem = s.status.as_ref().and_then(|status| status.problem.clone());
    let (message, color) = if s.offline.is_some() {
        ("The DualLink service isn't running. Restart the PC or run setup again.".to_string(), t.bad)
    } else if let Some(refused) = &s.refused {
        (format!("Not saved. {refused}"), t.warn)
    } else {
        (problem?, if s.state() == EngineState::Failed { t.bad } else { t.warn })
    };
    Some(
        row().key("notice").px(24.0).pb(8.0).child(
            row()
                .key("box")
                .w_full()
                .px(14.0)
                .py(10.0)
                .gap(10.0)
                .center_y()
                .radius(10.0)
                .fill(color.alpha(0.1))
                .border(t.hair, color.alpha(0.4))
                .live_region(rune::LiveRegion::Polite)
                .child(el().key("mark").size(6.0, 6.0).radius(3.0).fill(color).a11y_hidden())
                .child(text(message).key("message").font(t.ty.body).color(t.ink).wrap()),
        ),
    )
}

/// The lines and the combined speed.
fn picture(s: &Ui, t: &Theme, links: &[&LinkStatus]) -> El {
    let total = s.total();
    let on = s.state() == EngineState::On;
    col()
        .key("picture")
        .w_full()
        .gap(6.0)
        .child(
            row()
                .key("flow")
                .w_full()
                .h(PICTURE)
                .center_y()
                .child(streams(s, t, links))
                .child(
                    col()
                        .key("readout")
                        .w(230.0)
                        .items_end()
                        .gap(4.0)
                        .label(format!("{} megabits per second combined", mbps(total.down)))
                        .child(text(mbps(total.down)).key("speed").font(t.ty.display).color(if on { t.ink } else { t.ink_faint }).a11y_hidden())
                        .child(text("Mbps combined").key("unit").font(t.ty.body).color(t.ink_soft).a11y_hidden()),
                ),
        )
        .child(canvas(minute(s, t)).key("minute").w_full().h(36.0).a11y_hidden())
}

/// A connection's line: from its place on the left, curving into the meeting point. With
/// `onward` it carries on to the right edge, which is the way its pulses travel.
fn line(index: usize, count: usize, onward: bool) -> Path {
    let lane = PICTURE / count as f32;
    let (from, mid) = (lane * (index as f32 + 0.5), PICTURE / 2.0);
    let join = W * JOIN;
    let path = Path::new().move_to([6.0, from]).cubic_to([join * 0.55, from], [join * 0.45, mid], [join, mid]);
    if onward { path.line_to([W, mid]) } else { path }
}

/// The lines. Pulses travel along each one while it carries traffic, faster the busier it is.
fn streams(s: &Ui, t: &Theme, links: &[&LinkStatus]) -> El {
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
    let mid = PICTURE / 2.0;
    for (index, link) in links.iter().enumerate() {
        let enabled = s.config.choice(&link.info.id).enabled;
        let share = if total > 0.05 { speeds[index] / total } else { 0.0 };
        // Mixed into the panel rather than see-through: a see-through stroke shows its joints.
        let color = if !enabled {
            t.ink.mix(t.panel, 0.9)
        } else {
            t.link(index).mix(t.panel, if flowing { 0.65 - 0.45 * share } else { 0.75 })
        };
        base = base.stroke_path(&line(index, count, false), 1.25 + 1.5 * share, LineCap::Round, color);
        // Where the line starts, a dot in the connection's colour.
        let dot = if enabled { t.link(index) } else { t.ink_faint };
        base = base.fill(circle([6.0, PICTURE / count as f32 * (index as f32 + 0.5)], 3.5), dot);
    }
    if !links.is_empty() {
        let out = if moving { t.ink.mix(t.panel, 0.45) } else { t.ink.mix(t.panel, 0.82) };
        base = base.line([W * JOIN, mid], [W, mid], if moving { 2.0 } else { 1.25 }, LineCap::Round, out);
        base = base.fill(circle([W * JOIN, mid], 3.2), if moving { t.accent } else { t.ink_faint });
    }

    let mut layer = el().key("streams").grow(1.0).min_w(60.0).h(PICTURE).a11y_hidden().child(
        canvas(base.view_box(W, PICTURE)).key("lines").absolute(0.0, 0.0).w_full().h_full(),
    );
    if moving {
        for (index, link) in links.iter().enumerate().take(duallink_core::MAX_LINKS) {
            if speeds[index] <= 0.05 {
                continue;
            }
            let pulse = Shape::new().stroke_path(&line(index, count, true), 2.5, LineCap::Round, t.link(index));
            for n in 0..crate::flow::PULSES {
                layer = layer.child(
                    canvas(pulse.clone().view_box(W, PICTURE))
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

fn circle(centre: [f32; 2], radius: f32) -> Vec<[f32; 2]> {
    (0..24)
        .map(|k| {
            let a = k as f32 / 24.0 * std::f32::consts::TAU;
            [centre[0] + radius * a.cos(), centre[1] + radius * a.sin()]
        })
        .collect()
}

/// The last minute of speed: one thin line per connection, stacked.
fn minute(s: &Ui, t: &Theme) -> Shape {
    const MW: f32 = 900.0;
    const H: f32 = 36.0;
    let mut shape = Shape::new();
    let ids: Vec<&str> = s.ordered().iter().map(|l| l.info.id.as_str()).collect();
    if s.history.len() >= 2 && !ids.is_empty() {
        let highest = s.history.iter().map(|sample| sample.iter().map(|(_, v)| *v).sum::<f32>()).fold(1.0f32, f32::max) * 1.15;
        let step = MW / (HISTORY - 1) as f32;
        let start = HISTORY - s.history.len();
        let x = |i: usize| (start + i) as f32 * step;
        let y = |value: f32| H - 1.0 - (value / highest) * (H - 4.0);
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
        shape = shape.line([0.0, H - 1.0], [MW, H - 1.0], 1.0, LineCap::Butt, t.separator);
    }
    shape.view_box(MW, H)
}

/// The settings of the connection picked in the sidebar.
fn link_settings(s: &Ui, t: &Theme) -> Option<El> {
    let links = s.ordered();
    let index = links.iter().position(|l| Some(l.info.id.as_str()) == s.expanded.as_deref())?;
    let link = links[index];
    let choice = s.config.choice(&link.info.id);
    let id = link.info.id.clone();
    let stepper = |key: &str, value: String, change: fn(&mut duallink_core::LinkChoice, bool)| {
        let (less, more) = (id.clone(), id.clone());
        row()
            .key(key.to_string())
            .gap(8.0)
            .center_y()
            .child(sign_button(t, "less", "−", "Lower".into(), move |s: &mut Ui| s.edit_link(&less, |c| change(c, false))))
            .child(text(value).key("value").font(t.ty.body).color(t.ink).w(72.0).align(Align::Center))
            .child(sign_button(t, "more", "+", "Raise".into(), move |s: &mut Ui| s.edit_link(&more, |c| change(c, true))))
    };
    let use_id = id.clone();
    let mut rows = vec![
        setting(
            t,
            "use",
            "Use this connection",
            toggle(t, "use-switch", "Use this connection", choice.enabled, move |s: &mut Ui| {
                s.edit_link(&use_id, |c| c.enabled = !c.enabled)
            }),
        ),
        setting(
            t,
            "share",
            "Share",
            stepper("share-step", format!("{} of 10", choice.weight), |c, up| {
                c.weight = if up { (c.weight + 1).min(10) } else { c.weight.saturating_sub(1).max(1) }
            }),
        ),
        setting(
            t,
            "limit",
            "Speed limit",
            stepper(
                "limit-step",
                if choice.limit_mbps == 0 { "None".into() } else { format!("{} Mbps", choice.limit_mbps) },
                |c, up| c.limit_mbps = step_cap(c.limit_mbps, up),
            ),
        ),
    ];
    if index > 0 {
        let raise = id.clone();
        rows.push(setting(
            t,
            "order",
            "Try this one sooner",
            sign_button(t, "raise", "↑", "Move up the list".into(), move |s: &mut Ui| s.raise_link(&raise)),
        ));
    }
    rows.push(setting(
        t,
        "used",
        "Through DualLink",
        text(format!("↓ {}   ↑ {}", bytes(link.downloaded), bytes(link.uploaded))).key("value").font(t.ty.body).color(t.ink_soft),
    ));
    Some(section(t, "link-settings", &link_title(&link.info), rows))
}

fn programs(s: &Ui, t: &Theme) -> El {
    let everything = s.config.scope == Scope::Everything;
    let mut tiles = row().key("tiles").flex_wrap().gap(4.0).w_full().padding(8.0);
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
            .radius(10.0)
            .hover(t.hover)
            .press(t.press)
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
                    .border(1.25, t.separator)
                    .border_dash(4.0, 3.0)
                    .center()
                    .child(text("+").font(t.ty.title).color(t.ink_faint)),
            )
            .child(el().key("spacer").h(15.0)),
    );
    section(
        t,
        "programs",
        "Programs",
        vec![
            setting(
                t,
                "scope",
                if everything { "Combine everything except" } else { "Combine only" },
                segmented(t, "scope-pick", "Which programs", &["Everything", "Chosen"], usize::from(!everything), 96.0, |s, pick| {
                    let scope = if pick == 0 { Scope::Everything } else { Scope::Chosen };
                    if s.config.scope != scope {
                        s.config.scope = scope;
                        s.send();
                    }
                }),
            ),
            tiles,
        ],
    )
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
            .fill(t.track)
            .center()
            .child(text(name.chars().next().unwrap_or('?').to_uppercase().to_string()).font(t.ty.title).color(t.ink_soft)),
    };
    let badge = match kind {
        Tile::Chosen => Some((t.accent, "✓")),
        Tile::Excluded => Some((t.bad, "–")),
        Tile::Offered => None,
    };
    let mut icon = el().key("well").size(36.0, 36.0).center().child(picture.opacity(if kind == Tile::Excluded { 0.45 } else { 1.0 }));
    if let Some((color, sign)) = badge {
        icon = icon.child(
            el().key("badge")
                .size(15.0, 15.0)
                .radius(7.5)
                .absolute(25.0, -3.0)
                .fill(color)
                .border(2.0, t.card)
                .center()
                .a11y_hidden()
                .child(text(sign).font(t.ty.small).color(t.card)),
        );
    }
    col()
        .key(key)
        .w(84.0)
        .py(8.0)
        .gap(6.0)
        .items_center()
        .radius(10.0)
        .opacity(if kind == Tile::Offered { 0.5 } else { 1.0 })
        .hover(t.hover)
        .press(t.press)
        .focusable(true)
        .role("button")
        .child(icon)
        .child(text(name.to_string()).key("name").font(t.ty.small).color(t.ink_soft).w(78.0).align(Align::Center).wrap().max_lines(2).a11y_hidden())
}

fn sharing(s: &Ui, t: &Theme) -> El {
    let current = MODES.iter().position(|(mode, _)| *mode == s.config.mode).unwrap_or(0);
    let names: Vec<&str> = MODES.iter().map(|(_, name)| *name).collect();
    let switch = |key: &str, label: &str, on: bool, flip: fn(&mut Config)| {
        setting(
            t,
            key,
            label,
            toggle(t, &format!("{key}-switch"), label, on, move |s: &mut Ui| {
                flip(&mut s.config);
                s.send();
            }),
        )
    };
    section(
        t,
        "sharing",
        "Sharing",
        vec![
            setting(
                t,
                "mode",
                "Spread traffic",
                segmented(t, "mode-pick", "How traffic is spread", &names, current, 76.0, |s, pick| {
                    let mode = MODES[pick.min(MODES.len() - 1)].0;
                    if s.config.mode != mode {
                        s.config.mode = mode;
                        s.send();
                    }
                })
                .help(match s.config.mode {
                    Mode::Smart => "Uses whichever connection is free and quick, and keeps each site on one connection",
                    Mode::Balanced => "Shares traffic in turn, following each connection's share",
                    Mode::Backup => "Uses the first connection; the others step in only if it drops",
                }),
            ),
            switch("quic", "Boost browsers", s.config.steer_quic, |c| c.steer_quic = !c.steer_quic),
            switch("ipv4", "Prefer IPv4", s.config.prefer_ipv4, |c| c.prefer_ipv4 = !c.prefer_ipv4),
        ],
    )
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
