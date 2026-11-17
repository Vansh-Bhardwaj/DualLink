//! DualLink's look: one palette and one type scale, used by every part of the window.

use rune::Color;
use rune::tok::TextStyle;
use rune_kit::{Kit, PaletteSeed};

fn hex(value: &str) -> Color {
    rune::color::hex(value).expect("colours in this file are valid")
}

/// The five sizes text comes in. Numbers use fixed-width digits so they don't jitter as they change.
pub struct Type {
    /// The combined speed.
    pub display: TextStyle,
    /// A connection's speed.
    pub figure: TextStyle,
    /// Names and the wordmark.
    pub title: TextStyle,
    /// Controls and supporting values.
    pub body: TextStyle,
    /// Section labels and units, set in capitals.
    pub label: TextStyle,
}

pub struct Theme {
    pub dark: bool,
    pub k: Kit,
    pub ty: Type,
    pub hair: f32,
    /// The window behind everything.
    pub ground: Color,
    /// Panels.
    pub panel: Color,
    /// Things that sit on panels.
    pub raised: Color,
    pub line: Color,
    pub ink: Color,
    pub ink_soft: Color,
    pub ink_faint: Color,
    /// DualLink's own colour: the combined stream and anything switched on.
    pub accent: Color,
    pub warn: Color,
    pub bad: Color,
}

pub fn theme(dark: bool, scale: f32) -> Theme {
    let (ground, ink, accent) = if dark {
        (hex("#0a0c0f"), hex("#f2f5f7"), hex("#3ee0b0"))
    } else {
        (hex("#f3f4f6"), hex("#0f1317"), hex("#00a47c"))
    };
    let mut k = Kit::from_palette(PaletteSeed { bg: ground, fg: ink, accent });
    k.c.knob_on = hex("#ffffff");
    let base = k.ty.ui;
    let style = |size: f32, weight: f32, tracking: f32, numeric: bool| TextStyle {
        size,
        weight,
        tracking,
        line_height: (size * 1.15).round(),
        features: if numeric { "tnum" } else { base.features },
        ..base
    };
    Theme {
        dark,
        ty: Type {
            display: style(96.0, 600.0, -3.0, true),
            figure: style(20.0, 600.0, -0.3, true),
            title: style(15.0, 550.0, -0.1, false),
            body: style(13.0, 450.0, 0.0, true),
            label: style(11.0, 600.0, 0.9, false),
        },
        hair: 1.0 / scale,
        ground,
        panel: if dark { hex("#111418") } else { hex("#ffffff") },
        raised: if dark { hex("#191d23") } else { hex("#eef0f3") },
        line: ink.alpha(if dark { 0.09 } else { 0.1 }),
        ink,
        ink_soft: ink.alpha(0.62),
        ink_faint: ink.alpha(0.36),
        accent,
        warn: if dark { hex("#f5b94e") } else { hex("#b06f00") },
        bad: if dark { hex("#ff6f81") } else { hex("#cc2c44") },
        k,
    }
}

impl Theme {
    /// One colour per connection, by its place in the list, spaced round the colour wheel
    /// at equal lightness so no connection looks more important than another.
    pub fn link(&self, index: usize) -> Color {
        const HUES: [f32; 8] = [168.0, 250.0, 75.0, 320.0, 30.0, 205.0, 120.0, 355.0];
        rune::oklch(if self.dark { 0.8 } else { 0.62 }, 0.15, HUES[index % HUES.len()])
    }
}
