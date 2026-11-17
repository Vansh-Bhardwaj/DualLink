//! DualLink's look: native surfaces and type from the framework's own tokens, DualLink's
//! indigo for anything switched on, and one colour per connection.

use rune::Color;
use rune::tok::{self, TextStyle, type_};

fn hex(value: &str) -> Color {
    rune::color::hex(value).expect("colours in this file are valid")
}

/// The type scale. Sentence case throughout; numbers use fixed-width digits so they hold
/// still as they change.
pub struct Type {
    /// The combined speed.
    pub display: TextStyle,
    /// A connection's speed in the sidebar.
    pub figure: TextStyle,
    /// The wordmark and page titles.
    pub title: TextStyle,
    /// Row labels and values.
    pub body: TextStyle,
    /// Names that need a little weight.
    pub body_medium: TextStyle,
    /// Section headings above cards.
    pub section: TextStyle,
    /// Supporting details.
    pub small: TextStyle,
}

pub struct Theme {
    pub dark: bool,
    pub ty: Type,
    pub hair: f32,
    /// Behind everything; the sidebar sits straight on it.
    pub ground: Color,
    /// The main panel.
    pub panel: Color,
    /// Grouped rows.
    pub card: Color,
    pub card_border: Color,
    pub separator: Color,
    pub hover: Color,
    pub press: Color,
    pub selected: Color,
    pub ink: Color,
    pub ink_soft: Color,
    pub ink_faint: Color,
    pub accent: Color,
    pub warn: Color,
    pub bad: Color,
    /// Switch and segmented-control parts.
    pub track: Color,
    pub control_off: Color,
    pub indicator: Color,
    pub knob: Color,
    pub knob_shadow: Color,
    pub edge: Color,
    pub shadow: Color,
    pub shadow_soft: Color,
    /// Windows asks for less motion: nothing moves for decoration.
    pub still: bool,
}

pub fn theme(dark: bool, scale: f32) -> Theme {
    let n = tok::theme(dark);
    let accent = if dark { hex("#7d8fff") } else { hex("#4f63e6") };
    let (ground, panel) = if dark { (hex("#0b0b0d"), hex("#151618")) } else { (hex("#e9eaee"), hex("#f6f6f8")) };
    let digits = |style: TextStyle| TextStyle { features: "tnum", ..style };
    Theme {
        dark,
        ty: Type {
            display: digits(TextStyle { size: 76.0, weight: 600.0, tracking: -2.4, line_height: 80.0, ..type_::BODY }),
            figure: digits(TextStyle { size: 13.0, weight: 600.0, ..type_::BODY }),
            title: TextStyle { size: 14.0, ..type_::TITLE },
            body: digits(type_::BODY),
            body_medium: TextStyle { weight: 500.0, ..type_::BODY },
            section: type_::SECTION,
            small: digits(TextStyle { size: 11.5, weight: 450.0, line_height: 14.0, ..type_::BODY }),
        },
        hair: 1.0 / scale,
        ground,
        panel,
        card: if dark { hex("#1d1e21") } else { hex("#ffffff") },
        card_border: n.card_border,
        separator: n.separator,
        hover: n.hover,
        press: n.press,
        selected: if dark { hex("#26272b") } else { hex("#dcdde3") },
        ink: n.text,
        ink_soft: n.text_secondary,
        ink_faint: n.text_secondary.alpha(0.6),
        accent,
        warn: if dark { hex("#f5b94e") } else { hex("#b06f00") },
        bad: if dark { hex("#ff6f81") } else { hex("#cc2c44") },
        track: n.control_track,
        control_off: n.control_off,
        indicator: n.indicator,
        knob: n.knob,
        knob_shadow: n.knob_shadow,
        edge: n.edge,
        shadow: n.shadow,
        shadow_soft: n.shadow_soft,
        still: false,
    }
}

impl Theme {
    /// One colour per connection, by its place in the list, spaced round the colour wheel
    /// at equal lightness so no connection looks more important than another.
    pub fn link(&self, index: usize) -> Color {
        // Amber and cyan come first: they have always stood for the first two connections.
        const HUES: [f32; 8] = [80.0, 220.0, 305.0, 160.0, 30.0, 270.0, 125.0, 355.0];
        rune::oklch(if self.dark { 0.8 } else { 0.62 }, 0.15, HUES[index % HUES.len()])
    }
}
