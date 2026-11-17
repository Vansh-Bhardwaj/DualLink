//! How the window is drawn.
//!
//! DualLink draws its own title bar. Below it, a sidebar holds the pages and the connections,
//! and narrows to icons in a small window; the main panel is raised beside it. The overview
//! shows each connection as a thin line in its own colour running into one line that ends at
//! the combined speed, with pulses travelling along each line while it carries traffic.

use crate::prefs::{Appearance, autostart};
use crate::state::{HISTORY, Page, Speed, Ui, step_cap};
use crate::theme::{Theme, theme};
use duallink_core::{Config, EngineState, LinkInfo, LinkKind, LinkStatus, Mode, Scope, Status};
use rune::tok::{TextStyle, motion, type_};
use rune::{Align, Color, Cx, El, FileDialogOptions, LineCap, Path, ScrollSpec, Shape, Tx, canvas, col, el, row, text};
use rune_kit::{ScrollBar, ScrollBarStyle};

const MODES: [(Mode, &str); 3] = [(Mode::Smart, "Smart"), (Mode::Balanced, "Even"), (Mode::Backup, "Backup")];
const TITLEBAR: f32 = 40.0;
const SIDEBAR: f32 = 236.0;
const SIDEBAR_COMPACT: f32 = 60.0;
/// Below this window width the sidebar shows icons only.
const COMPACT_BELOW: f32 = 880.0;
/// Below this panel width the speed sits above the lines instead of beside them.
const NARROW_BELOW: f32 = 620.0;
/// Height of the picture.
const PICTURE: f32 = 196.0;
/// Width of the picture's drawing space; it stretches to fit.
const W: f32 = 400.0;
/// Where the lines meet, as a share of the width.
const JOIN: f32 = 0.62;
const RELEASES: &str = "https://github.com/Vansh-Bhardwaj/DualLink/releases";

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

/// How the window is laid out at its current width.
#[derive(Clone, Copy)]
struct Fit {
    compact: bool,
    narrow: bool,
}

pub fn view(s: &Ui, cx: &mut Cx<Ui>) -> El {
    let dark = match s.prefs.appearance {
        Appearance::Auto => cx.system_dark(),
        Appearance::Light => false,
        Appearance::Dark => true,
    };
    let mut t = theme(dark, cx.env().scale);
    t.still = cx.system_reduced_motion();
    cx.caption_color(t.ink_soft);
    let trailing = cx.chrome_insets().trailing;
    s.flow.start();

    let compact = s.width < COMPACT_BELOW;
    let panel_width = s.width - if compact { SIDEBAR_COMPACT } else { SIDEBAR } - 8.0;
    let fit = Fit { compact, narrow: panel_width < NARROW_BELOW };
    let body = col()
        .key("window")
        .w_full()
        .h_full()
        .fill(t.ground)
        .on_layout(|s: &mut Ui, rect| {
            if (s.width - rect[2]).abs() > 0.5 {
                s.width = rect[2];
            }
        })
        .child(titlebar(s, &t, trailing))
        .child(row().key("body").w_full().grow(1.0).min_h(0.0).child(sidebar(s, &t, fit)).child(main_panel(s, &t, fit)));
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

/// A small button with a word on it.
fn word_button(t: &Theme, key: &str, label: &str) -> El {
    el().key(key.to_string())
        .role("button")
        .label(label.to_string())
        .focusable(true)
        .h(28.0)
        .px(12.0)
        .radius(7.0)
        .fill(t.track)
        .hover(t.hover)
        .press(t.press)
        .center()
        .child(text(label.to_string()).font(t.ty.body_medium).color(t.ink).a11y_hidden())
}

/// A heading above a card, as in the platform's settings.
fn section(t: &Theme, key: &str, title: &str, rows: Vec<El>) -> El {
    let mut card = col()
        .key("card")
        .w_full()
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
        .w_full()
        .gap(8.0)
        .child(text(title.to_string()).key("heading").font(t.ty.section).color(t.ink_soft).ml(12.0))
        .child(card)
}

/// One row of a card: a label on the left, its control on the right.
fn setting(t: &Theme, key: &str, label: &str, control: El) -> El {
    row()
        .key(key.to_string())
        .w_full()
        .min_h(44.0)
        .px(12.0)
        .py(6.0)
        .center_y()
        .justify_between()
        .gap(16.0)
        .child(text(label.to_string()).key("label").font(t.ty.body).color(t.ink).shrink().min_w(0.0).wrap())
        .child(control)
}

/// One line of explanation inside a card.
fn note(t: &Theme, key: &str, words: &str) -> El {
    text(words.to_string()).key(key.to_string()).font(t.ty.small).color(t.ink_soft).wrap().px(12.0).py(10.0)
}

// ---------------------------------------------------------------------------------------------
// Title bar and sidebar.

/// DualLink's own title bar. Windows draws its minimise, maximise and close buttons over
/// the right-hand end; the rest of the bar drags the window.
fn titlebar(s: &Ui, t: &Theme, trailing: f32) -> El {
    let (word, dot) = summary(s, t);
    row()
        .key("titlebar")
        .w_full()
        .h(TITLEBAR)
        .pl(14.0)
        .pr(trailing + 12.0)
        .gap(10.0)
        .center_y()
        .window_drag()
        .child(mark(t, 20.0))
        .child(text("DualLink").key("name").font(t.ty.body_medium).color(t.ink))
        .child(el().key("gap").grow(1.0))
        .child(
            row()
                .key("state")
                .h(24.0)
                .px(10.0)
                .gap(7.0)
                .center_y()
                .radius(12.0)
                .fill(t.card)
                .border(t.hair, t.card_border)
                .child(el().key("dot").size(6.0, 6.0).radius(3.0).fill(dot).shadow(0.0, 6.0, dot.alpha(0.6)).a11y_hidden())
                .child(text(word).key("word").font(t.ty.small).color(t.ink_soft)),
        )
}

/// DualLink's mark: two paths running together into one.
fn mark(t: &Theme, size: f32) -> El {
    let (top, bottom) = (t.link(1), t.link(0));
    let shape = Shape::new()
        .stroke_path(&Path::new().move_to([10.0, 14.0]).cubic_to([19.0, 14.0], [19.0, 24.0], [27.0, 24.0]), 5.5, LineCap::Round, top)
        .stroke_path(&Path::new().move_to([10.0, 34.0]).cubic_to([19.0, 34.0], [19.0, 24.0], [27.0, 24.0]), 5.5, LineCap::Round, bottom)
        .line([27.0, 24.0], [38.0, 24.0], 5.5, LineCap::Round, t.ink)
        .view_box(48.0, 48.0);
    el().key("mark").size(size, size).radius(size / 4.0).fill(t.card).border(t.hair, t.card_border).shape(shape).a11y_hidden()
}

/// A small drawn icon for each page, in a 16-point square.
fn page_icon(page: Page, color: Color) -> Shape {
    let shape = match page {
        Page::Overview => Shape::new()
            .stroke_path(&Path::new().move_to([2.0, 4.0]).cubic_to([6.0, 4.0], [6.0, 8.0], [10.0, 8.0]), 1.6, LineCap::Round, color)
            .stroke_path(&Path::new().move_to([2.0, 12.0]).cubic_to([6.0, 12.0], [6.0, 8.0], [10.0, 8.0]), 1.6, LineCap::Round, color)
            .line([10.0, 8.0], [14.0, 8.0], 1.6, LineCap::Round, color),
        Page::Programs => {
            let square = |x: f32, y: f32| vec![[x, y], [x + 5.0, y], [x + 5.0, y + 5.0], [x, y + 5.0]];
            Shape::new().fill(square(2.0, 2.0), color).fill(square(9.0, 2.0), color).fill(square(2.0, 9.0), color).fill(square(9.0, 9.0), color)
        }
        Page::Settings => Shape::new()
            .line([2.0, 5.0], [14.0, 5.0], 1.6, LineCap::Round, color)
            .line([2.0, 11.0], [14.0, 11.0], 1.6, LineCap::Round, color)
            .fill(circle([10.0, 5.0], 2.3), color)
            .fill(circle([6.0, 11.0], 2.3), color),
        Page::Activity => Shape::new().stroke([[1.5, 8.5], [4.5, 8.5], [6.5, 3.0], [9.5, 13.0], [11.5, 8.5], [14.5, 8.5]], 1.6, color),
    };
    shape.view_box(16.0, 16.0)
}

fn page_name(page: Page) -> &'static str {
    match page {
        Page::Overview => "Overview",
        Page::Programs => "Programs",
        Page::Settings => "Settings",
        Page::Activity => "Activity",
    }
}

fn nav_item(s: &Ui, t: &Theme, page: Page, compact: bool) -> El {
    let here = s.page == page;
    let color = if here { t.ink } else { t.ink_soft };
    let mut item = row()
        .key(format!("nav-{}", page_name(page)))
        .h(34.0)
        .px(if compact { 0.0 } else { 10.0 })
        .gap(10.0)
        .center_y()
        .radius(8.0)
        .fill(if here { t.selected } else { Color::TRANSPARENT })
        .hover(t.hover)
        .press(t.press)
        .focusable(true)
        .role("tab")
        .label(page_name(page))
        .checked(here)
        .on_press(move |s: &mut Ui| s.page = page)
        .child(el().key("icon").size(16.0, 16.0).shape(page_icon(page, color)).a11y_hidden());
    if compact {
        item = item.w(40.0).justify_center().help(page_name(page));
    } else {
        item = item.w_full().child(text(page_name(page)).key("name").font(t.ty.body_medium).color(color).a11y_hidden());
    }
    item
}

fn sidebar(s: &Ui, t: &Theme, fit: Fit) -> El {
    let mut bar = col()
        .key("sidebar")
        .w(if fit.compact { SIDEBAR_COMPACT } else { SIDEBAR })
        .h_full()
        .px(10.0)
        .pt(4.0)
        .pb(10.0)
        .gap(2.0)
        .items_center();
    for page in [Page::Overview, Page::Programs, Page::Settings, Page::Activity] {
        bar = bar.child(nav_item(s, t, page, fit.compact));
    }
    if fit.compact {
        bar = bar.child(el().key("gap").grow(1.0));
    } else {
        let links = s.ordered();
        let mut list = col().key("links").w_full().gap(2.0);
        if links.is_empty() {
            let message = "Plug in a cable, join Wi-Fi or tether a phone";
            list = list.child(text(message).key("empty").font(t.ty.small).color(t.ink_faint).wrap().px(10.0).py(8.0));
        }
        for (index, link) in links.iter().enumerate() {
            list = list.child(link_row(s, t, link, index));
        }
        bar = bar
            .child(text("Connections").key("heading").font(t.ty.section).color(t.ink_faint).self_start().ml(10.0).mt(18.0).mb(4.0))
            .child(col().key("scroll").w_full().grow(1.0).min_h(0.0).scroll(ScrollSpec { stick_bottom: false }).child(list).scroll_bar_styled(ScrollBarStyle::neutral(t.dark)));
    }
    bar.child(master(s, t, fit.compact))
}

fn link_title(info: &LinkInfo) -> String {
    match info.kind {
        LinkKind::UsbTether => "Phone (USB)".into(),
        _ => info.name.clone(),
    }
}

/// The colour a connection shows in.
fn link_color(s: &Ui, t: &Theme, link: &LinkStatus, index: usize) -> Color {
    if !s.config.choice(&link.info.id).enabled {
        t.ink_faint
    } else if !link.healthy {
        t.bad
    } else {
        t.link(index)
    }
}

fn link_detail(s: &Ui, link: &LinkStatus) -> String {
    if !s.config.choice(&link.info.id).enabled {
        "Not used".into()
    } else if !link.healthy {
        "Not responding".into()
    } else {
        link.latency_ms.map_or_else(|| link.info.kind.label().to_string(), |ms| format!("{ms:.0} ms"))
    }
}

/// A connection: its colour, name and speed. Pressing it opens its settings on the overview.
fn link_row(s: &Ui, t: &Theme, link: &LinkStatus, index: usize) -> El {
    let id = link.info.id.clone();
    let enabled = s.config.choice(&id).enabled;
    let speed = s.speeds.get(&id).copied().unwrap_or(Speed::default());
    let open = s.expanded.as_deref() == Some(id.as_str()) && s.page == Page::Overview;
    let color = link_color(s, t, link, index);
    let detail = link_detail(s, link);
    let ink = if enabled { t.ink } else { t.ink_faint };
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
            let showing = s.page == Page::Overview && s.expanded.as_deref() == Some(id.as_str());
            s.expanded = if showing { None } else { Some(id.clone()) };
            s.page = Page::Overview;
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

/// The one control that matters: combining on or off. On, it fills with DualLink's indigo and
/// glows softly; off, it sits quietly like any other control.
fn master(s: &Ui, t: &Theme, compact: bool) -> El {
    let on = s.config.enabled;
    let available = s.offline.is_none();
    let lit = on && available;
    let (word, dot) = summary(s, t);
    let size = if compact { 44.0 } else { 46.0 };
    let ink = if lit { Color::WHITE } else { t.ink };
    let mut pill = row()
        .key("master")
        .h(size)
        .radius(size / 2.0)
        .gap(10.0)
        .center_y()
        .mt(8.0)
        .role("switch")
        .label("Combine connections")
        .checked(on)
        .help(if available { "Combine connections" } else { "The DualLink service isn't running" })
        .focusable(available)
        .motion(motion::SNAPPY);
    pill = if lit {
        let deep = t.accent.mix(Color::BLACK, 0.18);
        pill.fill(t.accent)
            .linear_gradient(std::f32::consts::FRAC_PI_2, [(0.0, deep), (1.0, t.accent)])
            .border(t.hair, Color::WHITE.alpha(0.18))
            .shadow(6.0, 22.0, t.accent.alpha(0.32))
            .hover(Color::WHITE.alpha(0.06))
            .press(Color::BLACK.alpha(0.08))
    } else {
        pill.fill(t.card).border(t.hair, t.card_border).shadow(1.0, 2.0, t.shadow).hover(t.hover).press(t.press)
    };
    pill = if compact { pill.w(size).justify_center() } else { pill.w_full().pl(16.0).pr(6.0) };
    if available {
        pill = pill.on_press(|s: &mut Ui| {
            s.config.enabled = !s.config.enabled;
            s.send();
        });
    }
    let light = if lit { Color::WHITE } else { dot };
    pill = pill.child(el().key("dot").size(8.0, 8.0).radius(4.0).fill(light).shadow(0.0, 8.0, light.alpha(0.7)).a11y_hidden());
    if compact {
        return pill;
    }
    pill.child(text(word).key("word").font(t.ty.body_medium).color(ink).grow(1.0).a11y_hidden()).child(
        el().key("knob-track")
            .size(44.0, 26.0)
            .radius(13.0)
            .fill(if lit { Color::WHITE.alpha(0.22) } else { t.control_off })
            .motion(motion::SNAPPY)
            .a11y_hidden()
            .child(
                el().key("knob")
                    .size(22.0, 22.0)
                    .absolute(if on { 20.0 } else { 2.0 }, 2.0)
                    .radius(11.0)
                    .fill(Color::WHITE)
                    .shadow(1.0, 3.0, t.knob_shadow)
                    .shadow(0.0, 0.5, t.edge),
            ),
    )
}
/// The engine's state in a word or two, and a colour.
fn summary(s: &Ui, t: &Theme) -> (&'static str, Color) {
    if s.offline.is_some() {
        return ("Service not running", t.bad);
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

// ---------------------------------------------------------------------------------------------
// Main panel.

fn main_panel(s: &Ui, t: &Theme, fit: Fit) -> El {
    let total = s.total();
    let title = match s.page {
        Page::Overview => {
            let in_use = s.ordered().iter().filter(|l| s.config.choice(&l.info.id).enabled).count();
            match in_use {
                0 => "No connections".to_string(),
                1 => "1 connection".to_string(),
                n => format!("{n} connections"),
            }
        }
        page => page_name(page).to_string(),
    };
    let content = match s.page {
        Page::Overview => overview(s, t, fit),
        Page::Programs => programs_page(s, t),
        Page::Settings => settings_page(s, t),
        Page::Activity => activity_page(s, t),
    };
    let mut bar = row().key("bar").w_full().h(48.0).px(if fit.narrow { 18.0 } else { 24.0 }).gap(12.0).center_y().child(
        text(title).key("title").font(t.ty.body_medium).color(t.ink).grow(1.0).min_w(0.0).truncate(),
    );
    if s.page == Page::Overview {
        bar = bar
            .child(text(format!("↑ {} Mbps", mbps(total.up))).key("up").font(t.ty.body).color(t.ink_soft).help("Upload"))
            .child(text(format!("Peak {}", mbps(s.peak))).key("peak").font(t.ty.body).color(t.ink_faint));
    }
    col()
        .key("main")
        .grow(1.0)
        .min_w(0.0)
        .h_full()
        .mr(8.0)
        .mb(8.0)
        .radius(12.0)
        .clip_rounded(12.0)
        .fill(t.panel)
        .border(t.hair, t.card_border)
        .child(bar)
        .children(notice(s, t, fit))
        .child(
            col().key(format!("scroll-{}", page_name(s.page)))
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .scroll(ScrollSpec { stick_bottom: false })
                .scroll_edge_fade(16.0)
                .child(content.w_full().px(if fit.narrow { 18.0 } else { 28.0 }).pt(4.0).pb(28.0))
                .scroll_bar_styled(ScrollBarStyle::neutral(t.dark)),
        )
}

/// A line under the bar, only when something needs the user's attention.
fn notice(s: &Ui, t: &Theme, fit: Fit) -> Option<El> {
    let problem = s.status.as_ref().and_then(|status| status.problem.clone());
    let (message, color) = if s.offline.is_some() {
        ("The DualLink service isn't running, so nothing is being combined. Restart the PC, or run DualLink's setup again.".to_string(), t.bad)
    } else if let Some(refused) = &s.refused {
        (format!("Not saved. {refused}"), t.warn)
    } else {
        (problem?, if s.state() == EngineState::Failed { t.bad } else { t.warn })
    };
    Some(
        row().key("notice").w_full().px(if fit.narrow { 18.0 } else { 24.0 }).pb(8.0).child(
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
                .child(text(message).key("message").font(t.ty.body).color(t.ink).grow(1.0).min_w(0.0).wrap()),
        ),
    )
}

// ---------------------------------------------------------------------------------------------
// Overview.

fn overview(s: &Ui, t: &Theme, fit: Fit) -> El {
    let links = s.ordered();
    let mut page = col().key("overview").gap(20.0).child(picture(s, t, &links, fit.narrow));
    if !links.is_empty() {
        page = page.child(link_cards(s, t, &links));
    }
    page = page.child(totals(s, t));
    if let Some(card) = link_settings(s, t) {
        page = page.child(card);
    } else if fit.compact && links.is_empty() {
        // The sidebar has no room for the connections, so they're listed here.
        let mut rows: Vec<El> = links.iter().enumerate().map(|(index, link)| link_row(s, t, link, index)).collect();
        if rows.is_empty() {
            rows.push(note(t, "none", "No connections found. Plug in a cable, join Wi-Fi or tether a phone."));
        }
        page = page.child(section(t, "connections", "Connections", rows));
    }
    page
}

/// Each connection as a card: its speed, its share of the traffic, and how quickly it answers.
/// Pressing one opens its settings.
fn link_cards(s: &Ui, t: &Theme, links: &[&LinkStatus]) -> El {
    let on = s.state() == EngineState::On;
    let speeds: Vec<f32> = links.iter().map(|l| s.speeds.get(&l.info.id).map_or(0.0, |v| v.down)).collect();
    let total: f32 = speeds.iter().sum();
    let mut cards = row().key("cards").w_full().gap(12.0).flex_wrap();
    for (index, link) in links.iter().enumerate() {
        let id = link.info.id.clone();
        let enabled = s.config.choice(&id).enabled;
        let color = link_color(s, t, link, index);
        let share = if on && total > 0.05 { speeds[index] / total } else { 0.0 };
        let state = if !enabled {
            "Not used"
        } else if !link.healthy {
            "Not responding"
        } else if on {
            "In use"
        } else {
            "Ready"
        };
        let mut facts = Vec::new();
        if on && total > 0.05 {
            facts.push(format!("{:.0}% of traffic", share * 100.0));
        }
        if let Some(ms) = link.latency_ms {
            facts.push(format!("{ms:.0} ms"));
        }
        if !link_title(&link.info).eq_ignore_ascii_case(link.info.kind.label()) && link.info.kind != LinkKind::UsbTether {
            facts.push(link.info.kind.label().to_string());
        }
        let open = s.expanded.as_deref() == Some(id.as_str());
        cards = cards.child(
            col()
                .key(format!("card-{id}"))
                .grow(1.0)
                .min_w(200.0)
                .padding(14.0)
                .gap(10.0)
                .radius(12.0)
                .fill(t.card)
                .border(t.hair, if open { color.alpha(0.6) } else { t.card_border })
                .hover(t.hover)
                .press(t.press)
                .focusable(true)
                .role("button")
                .label(format!("{}, {} megabits per second. Settings", link_title(&link.info), mbps(speeds[index])))
                .on_press(move |s: &mut Ui| {
                    s.expanded = if s.expanded.as_deref() == Some(id.as_str()) { None } else { Some(id.clone()) };
                })
                .child(
                    row()
                        .key("top")
                        .gap(8.0)
                        .center_y()
                        .child(el().key("dot").size(8.0, 8.0).radius(4.0).fill(color).shadow(0.0, 6.0, color.alpha(0.5)).a11y_hidden())
                        .child(text(link_title(&link.info)).key("name").font(t.ty.body_medium).color(t.ink).grow(1.0).min_w(0.0).truncate())
                        .child(text(state).key("state").font(t.ty.small).color(if on && enabled && link.healthy { color } else { t.ink_faint })),
                )
                .child(
                    row()
                        .key("speed")
                        .gap(6.0)
                        .items_end()
                        .child(text(mbps(speeds[index])).key("value").font(TextStyle { size: 26.0, line_height: 28.0, ..t.ty.figure }).color(t.ink))
                        .child(text("Mbps").key("unit").font(t.ty.small).color(t.ink_soft).pb(3.0)),
                )
                .child(
                    el().key("bar")
                        .w_full()
                        .h(4.0)
                        .radius(2.0)
                        .fill(t.track)
                        .child(el().key("share").w_frac(share.max(0.0)).h(4.0).radius(2.0).fill(color).motion(motion::SNAPPY)),
                )
                .child(text(facts.join("  ·  ")).key("facts").font(t.ty.small).color(t.ink_faint).truncate()),
        );
    }
    cards
}

/// A few numbers for this session, each in its own tile.
fn totals(s: &Ui, t: &Theme) -> El {
    let links = s.ordered();
    // Totals for this session of combining; with combining off there is none.
    let on = s.state() == EngineState::On;
    let down: u64 = if on { links.iter().map(|l| l.downloaded).sum() } else { 0 };
    let up: u64 = if on { links.iter().map(|l| l.uploaded).sum() } else { 0 };
    let open = s.status.as_ref().filter(|_| s.offline.is_none()).map_or(0, |status| status.active_connections);
    let fastest = links
        .iter()
        .enumerate()
        .max_by(|a, b| {
            let speed = |l: &LinkStatus| s.speeds.get(&l.info.id).map_or(0.0, |v| v.down);
            speed(a.1).total_cmp(&speed(b.1))
        })
        .filter(|(_, l)| s.speeds.get(&l.info.id).is_some_and(|v| v.down > 0.05));
    let tile = |key: &str, label: &str, value: String, dot: Option<Color>| {
        let mut figure = row().key("figure").gap(8.0).center_y();
        if let Some(color) = dot {
            figure = figure.child(el().key("dot").size(8.0, 8.0).radius(4.0).fill(color).a11y_hidden());
        }
        col()
            .key(key.to_string())
            .grow(1.0)
            .min_w(140.0)
            .padding(14.0)
            .gap(6.0)
            .radius(12.0)
            .fill(t.card)
            .border(t.hair, t.card_border)
            .child(text(label.to_string()).key("label").font(t.ty.small).color(t.ink_soft))
            .child(figure.child(text(value).key("value").font(TextStyle { size: 18.0, ..t.ty.figure }).color(t.ink).truncate()))
    };
    row()
        .key("totals")
        .w_full()
        .gap(12.0)
        .flex_wrap()
        .child(tile("down", "Downloaded", bytes(down), None))
        .child(tile("up", "Uploaded", bytes(up), None))
        .child(tile("open", "Open now", open.to_string(), None))
        .child(match fastest {
            Some((index, link)) => tile("fastest", "Busiest", link_title(&link.info), Some(t.link(index))),
            None => tile("fastest", "Busiest", "—".into(), None),
        })
}

/// The lines and the combined speed.
fn picture(s: &Ui, t: &Theme, links: &[&LinkStatus], narrow: bool) -> El {
    let total = s.total();
    let on = s.state() == EngineState::On;
    let size = if narrow { 52.0 } else { 72.0 };
    let display = TextStyle { size, line_height: size * 1.05, tracking: -size * 0.03, ..t.ty.display };
    let readout = col()
        .key("readout")
        .gap(2.0)
        .label(format!("{} megabits per second combined", mbps(total.down)))
        .child(text(mbps(total.down)).key("speed").font(display).color(if on { t.ink } else { t.ink_faint }).a11y_hidden())
        .child(text("Mbps combined").key("unit").font(t.ty.body).color(t.ink_soft).a11y_hidden());
    let flow = if narrow {
        col().key("flow").w_full().gap(10.0).child(readout).child(streams(s, t, links))
    } else {
        row().key("flow").w_full().h(PICTURE).center_y().gap(16.0).child(streams(s, t, links)).child(readout.w(200.0).items_end())
    };
    col().key("picture").w_full().gap(6.0).child(flow).child(canvas(minute(s, t)).key("minute").w_full().h(40.0).a11y_hidden())
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
        base = base.fill(circle([6.0, PICTURE / count as f32 * (index as f32 + 0.5)], 3.5), link_color(s, t, link, index));
    }
    if !links.is_empty() {
        let out = if moving { t.ink.mix(t.panel, 0.45) } else { t.ink.mix(t.panel, 0.82) };
        base = base.line([W * JOIN, mid], [W, mid], if moving { 2.0 } else { 1.25 }, LineCap::Round, out);
        base = base.fill(circle([W * JOIN, mid], 3.2), if moving { t.accent } else { t.ink_faint });
    }

    let mut layer = el().key("streams").grow(1.0).w_full().min_w(60.0).h(PICTURE).a11y_hidden().child(
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
    const H: f32 = 32.0;
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
        text(if s.state() == EngineState::On { format!("↓ {}   ↑ {}", bytes(link.downloaded), bytes(link.uploaded)) } else { "Off".into() })
            .key("value")
            .font(t.ty.body)
            .color(t.ink_soft),
    ));
    Some(section(t, "link-settings", &link_title(&link.info), rows))
}

// ---------------------------------------------------------------------------------------------
// Programs.

fn programs_page(s: &Ui, t: &Theme) -> El {
    let everything = s.config.scope == Scope::Everything;
    let explain = if everything {
        "Every program on this PC is combined, apart from the ones in the list below."
    } else {
        "Only the programs in the list below are combined. Everything else uses the connection Windows picks."
    };
    let choose = section(
        t,
        "choose",
        "What to combine",
        vec![
            setting(
                t,
                "scope",
                "Combine",
                segmented(t, "scope-pick", "What to combine", &["Everything", "Only chosen"], usize::from(!everything), 104.0, |s, pick| {
                    let scope = if pick == 0 { Scope::Everything } else { Scope::Chosen };
                    if s.config.scope != scope {
                        s.config.scope = scope;
                        s.send();
                    }
                }),
            ),
            note(t, "explain", explain),
        ],
    );

    // Each choice keeps its own list; only the one in use shows.
    let (title, empty, kind, hint) = if everything {
        ("Never combine", "Nothing is left out. Add a program here to keep it on its usual connection.", Tile::Excluded, "Left alone · click to remove from this list")
    } else {
        ("Combine only these", "No programs yet. Add the ones you want combined.", Tile::Chosen, "Combined · click to remove from this list")
    };
    let mut listed = row().key("listed").flex_wrap().gap(4.0).w_full().padding(8.0);
    for (index, entry) in s.programs().iter().enumerate() {
        let info = s.info(entry);
        let name = info.map_or_else(|| crate::apps::display_name(entry), |app| app.name.clone());
        listed = listed.child(
            app_tile(t, format!("program-{index}"), info, &name, kind)
                .label(format!("{name}. Remove from this list"))
                .help(hint)
                .on_press(move |s: &mut Ui| s.remove_program(index)),
        );
    }
    listed = listed.child(browse_tile(t));
    let mut rows = Vec::new();
    if s.programs().is_empty() {
        rows.push(note(t, "empty", empty));
    }
    rows.push(listed);
    let mut page = col().key("programs").gap(24.0).child(choose).child(section(t, "listed", title, rows));

    let offered = s.suggestions();
    if !offered.is_empty() {
        let mut open = row().key("open").flex_wrap().gap(4.0).w_full().padding(8.0);
        for app in offered {
            let path = app.path.clone();
            open = open.child(
                app_tile(t, format!("open-{}", app.path), Some(app), &app.name, Tile::Offered)
                    .label(format!("{}. Add to {}", app.name, title.to_lowercase()))
                    .help("Click to add to the list above")
                    .on_press(move |s: &mut Ui| s.add_program(&path)),
            );
        }
        page = page.child(section(t, "offered", "Open now", vec![open]));
    }
    page
}

/// The tile that opens a file picker for a program not on screen.
fn browse_tile(t: &Theme) -> El {
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
        .child(text("Browse").key("name").font(t.ty.small).color(t.ink_faint).a11y_hidden())
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
        Tile::Chosen => (t.accent, "✓"),
        Tile::Excluded => (t.bad, "–"),
        Tile::Offered => (t.ink_soft, "+"),
    };
    let icon = el()
        .key("well")
        .size(36.0, 36.0)
        .center()
        .child(picture.opacity(if kind == Tile::Excluded { 0.5 } else { 1.0 }))
        .child(
            el().key("badge")
                .size(15.0, 15.0)
                .radius(7.5)
                .absolute(25.0, -3.0)
                .fill(badge.0)
                .border(2.0, t.card)
                .center()
                .a11y_hidden()
                .child(text(badge.1).font(t.ty.small).color(t.card)),
        );
    col()
        .key(key)
        .w(84.0)
        .py(8.0)
        .gap(6.0)
        .items_center()
        .radius(10.0)
        .opacity(if kind == Tile::Offered { 0.7 } else { 1.0 })
        .hover(t.hover)
        .press(t.press)
        .focusable(true)
        .role("button")
        .child(icon)
        .child(text(name.to_string()).key("name").font(t.ty.small).color(t.ink_soft).w(78.0).align(Align::Center).wrap().max_lines(2).a11y_hidden())
}

// ---------------------------------------------------------------------------------------------
// Settings.

fn settings_page(s: &Ui, t: &Theme) -> El {
    let current = MODES.iter().position(|(mode, _)| *mode == s.config.mode).unwrap_or(0);
    let names: Vec<&str> = MODES.iter().map(|(_, name)| *name).collect();
    let engine_switch = |key: &str, label: &str, on: bool, flip: fn(&mut Config)| {
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
    let mode_note = match s.config.mode {
        Mode::Smart => "Uses whichever connection is free and quick, and keeps each website on one connection so sign-ins stay put.",
        Mode::Balanced => "Shares new downloads in turn, following each connection's share.",
        Mode::Backup => "Uses the first connection. The others step in only if it drops.",
    };
    let sharing = section(
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
                }),
            ),
            note(t, "mode-note", mode_note),
            engine_switch("quic", "Boost browsers", s.config.steer_quic, |c| c.steer_quic = !c.steer_quic),
            engine_switch("ipv4", "Prefer IPv4", s.config.prefer_ipv4, |c| c.prefer_ipv4 = !c.prefer_ipv4),
        ],
    );

    let appearance = match s.prefs.appearance {
        Appearance::Auto => 0,
        Appearance::Light => 1,
        Appearance::Dark => 2,
    };
    let general = section(
        t,
        "general",
        "General",
        vec![
            setting(
                t,
                "autostart",
                "Open when Windows starts",
                toggle(t, "autostart-switch", "Open when Windows starts", s.autostart, |s: &mut Ui| {
                    let want = !s.autostart;
                    if autostart::set(want) {
                        s.autostart = want;
                    }
                }),
            ),
            setting(
                t,
                "tray",
                "Keep running in the notification area when closed",
                toggle(t, "tray-switch", "Keep running in the notification area when closed", s.prefs.keep_in_tray, |s: &mut Ui| {
                    s.prefs.keep_in_tray = !s.prefs.keep_in_tray;
                    s.prefs.save();
                }),
            ),
            setting(
                t,
                "appearance",
                "Appearance",
                segmented(t, "appearance-pick", "Appearance", &["Auto", "Light", "Dark"], appearance, 64.0, |s, pick| {
                    s.prefs.appearance = [Appearance::Auto, Appearance::Light, Appearance::Dark][pick.min(2)];
                    s.prefs.save();
                }),
            ),
        ],
    );

    let service = match (&s.offline, &s.status) {
        (Some(_), _) | (None, None) => "Not running".to_string(),
        (None, Some(status)) => format!("Running · {}", status.version),
    };
    let about = section(
        t,
        "about",
        "About",
        vec![
            setting(t, "version", "Version", text(env!("CARGO_PKG_VERSION")).key("value").font(t.ty.body).color(t.ink_soft)),
            setting(t, "service", "Background service", text(service).key("value").font(t.ty.body).color(t.ink_soft)),
            setting(
                t,
                "updates",
                "Updates",
                word_button(t, "check", "Check online").help("Opens DualLink's releases page").on_press_tx(|_: &mut Ui, tx: &mut Tx| {
                    let _ = tx.open_url(RELEASES);
                }),
            ),
            setting(
                t,
                "logs",
                "Log files",
                word_button(t, "open-logs", "Show folder").on_press(|_: &mut Ui| {
                    let base = std::env::var_os("ProgramData").map_or_else(|| std::path::PathBuf::from(r"C:\ProgramData"), std::path::PathBuf::from);
                    let _ = std::process::Command::new("explorer.exe").arg(base.join("DualLink")).spawn();
                }),
            ),
        ],
    );
    col().key("settings").gap(24.0).child(sharing).child(general).child(about)
}

// ---------------------------------------------------------------------------------------------
// Activity.

fn activity_page(s: &Ui, t: &Theme) -> El {
    let value = |v: String| text(v).key("value").font(t.ty.body).color(t.ink_soft);
    // The numbers describe this session of combining; with combining off there is none.
    let on = s.state() == EngineState::On;
    let (open, routed, bypassed) = s
        .status
        .as_ref()
        .filter(|_| on)
        .map_or((0, 0, 0), |status| (status.active_connections, status.routed_connections, status.bypassed_connections));
    let now = section(
        t,
        "now",
        "Right now",
        vec![
            setting(t, "open", "Open through DualLink", value(open.to_string())),
            setting(t, "routed", "Combined since switched on", value(routed.to_string())),
            setting(t, "bypassed", "Left on their usual connection", value(bypassed.to_string())),
        ],
    );

    let links = s.ordered();
    let mut rows: Vec<El> = links
        .iter()
        .enumerate()
        .map(|(index, link)| {
            let mut facts = vec![link.info.address.to_string()];
            if on {
                facts.push(format!("↓ {}  ↑ {}", bytes(link.downloaded), bytes(link.uploaded)));
            }
            if let Some(ms) = link.latency_ms {
                facts.push(format!("{ms:.0} ms"));
            }
            if link.failures > 0 {
                facts.push(format!("{} failed", link.failures));
            }
            row()
                .key(format!("detail-{}", link.info.id))
                .w_full()
                .min_h(52.0)
                .px(12.0)
                .py(8.0)
                .gap(12.0)
                .center_y()
                .child(el().key("colour").size(8.0, 8.0).radius(4.0).fill(link_color(s, t, link, index)).a11y_hidden())
                .child(
                    col()
                        .key("names")
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(text(link_title(&link.info)).key("name").font(t.ty.body_medium).color(t.ink))
                        .child(text(link.info.description.clone()).key("adapter").font(t.ty.small).color(t.ink_faint).truncate()),
                )
                .child(text(facts.join("   ")).key("facts").font(t.ty.small).color(t.ink_soft).shrink().min_w(0.0).align(Align::End).wrap())
        })
        .collect();
    if rows.is_empty() {
        rows.push(note(t, "none", "No connections found."));
    }

    let mono = TextStyle { size: 11.5, line_height: 17.0, ..type_::MONO_CAPTION };
    let mut log = col().key("lines").w_full().px(12.0).py(10.0).gap(2.0);
    if s.log.is_empty() {
        log = log.child(text("Nothing yet.").font(t.ty.small).color(t.ink_faint));
    }
    for (index, line) in s.log.iter().rev().enumerate() {
        log = log.child(text(line.clone()).key(format!("line-{index}")).font(mono).color(t.ink_soft).wrap());
    }
    col()
        .key("activity")
        .gap(24.0)
        .child(now)
        .child(section(t, "details", "Connections", rows))
        .child(section(
            t,
            "log",
            "Recent events, newest first",
            vec![col()
                .key("log-scroll")
                .w_full()
                .max_h(220.0)
                .scroll(ScrollSpec { stick_bottom: false })
                .scroll_edge_fade(10.0)
                .child(log)
                .scroll_bar_styled(ScrollBarStyle::neutral(t.dark))],
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
    ("programs", |s| {
        demo(s, EngineState::On, 2);
        s.page = Page::Programs;
        s.config.excluded = vec![DEMO_PROGRAMS[3].into()];
        known(s, DEMO_PROGRAMS);
        s.open_now = DEMO_PROGRAMS[..3].iter().map(|p| p.to_lowercase()).collect();
    }),
    ("settings", |s| {
        demo(s, EngineState::On, 2);
        s.page = Page::Settings;
    }),
    ("activity", |s| {
        demo(s, EngineState::On, 3);
        s.page = Page::Activity;
        s.log = vec!["2026-11-17 12:00:01Z Connections: Ethernet (192.168.1.20), Wi-Fi (192.168.0.104)".into(), "2026-11-17 12:00:02Z Combining switched on.".into()];
    }),
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
