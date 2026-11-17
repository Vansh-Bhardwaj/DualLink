//! Draws the window, adding the framework's liquid-metal finish where the view asks for it.
//!
//! An element made with [`metal`] becomes a slot; this compositor paints a polished chrome
//! band round its edge, then the rest of the window draws as usual.

use rune::compose::{Compositor, Frame};
use rune::render::{EffectPhase, EffectSite, KIND_EFFECT};
use rune::El;
use rune_fx::{FillAnchor, Fx, FxFrame, FxLayer, FxOptions, FxRenderer, FxSlot, LiquidMetalParams, Shape};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

/// The slot kind for a chrome band.
const CHROME: u32 = 1;

/// A chrome band `width` points wide round the element's edge. Size and round it like any box.
pub fn metal(width: f32) -> El {
    rune::effect(CHROME).effect_data([width, 0.0, 0.0, 0.0])
}

pub struct Metal<S> {
    fx: Option<FxRenderer>,
    /// Set by the view while a metal band shows and may move.
    moving: Arc<AtomicBool>,
    _state: std::marker::PhantomData<fn(&S)>,
}

impl<S> Metal<S> {
    pub fn new(moving: Arc<AtomicBool>) -> Self {
        Metal { fx: None, moving, _state: std::marker::PhantomData }
    }
}

impl<S: 'static> Compositor<S> for Metal<S> {
    fn animating(&self) -> bool {
        self.moving.load(Ordering::Relaxed)
    }

    fn render(&mut self, f: &mut Frame<'_, S>) {
        let (device, queue, size) = (f.device, f.queue, f.size);
        let t = f.rt.t as f32;
        let mut at: Vec<u32> = Vec::new();
        let mut layers: Vec<FxLayer> = Vec::new();
        let mut slots: Vec<FxSlot> = Vec::new();
        for (n, i) in f.rt.instances.iter().enumerate() {
            if i.params[1] != KIND_EFFECT || f.rt.marks.get(n).copied().unwrap_or(0) & 0xff != CHROME {
                continue;
            }
            let k = i.params[3];
            let [x, y, w, h] = i.rect.map(|v| v / k);
            let radius = i.radii.iter().fold(0.0f32, |a, v| a.max(v.abs())) / k;
            let params = LiquidMetalParams { flow: 0.45, speed: 0.25, ..LiquidMetalParams::chrome() }
                .anchor(FillAnchor::Element)
                .border(i.uv[0].max(1.0));
            at.push(n as u32);
            layers.push(Fx::fill(params).on(Shape::rounded(x, y, w, h, radius)).at(t));
            slots.push(FxSlot::default().scale(k).clip(i.clip, i.fade));
        }
        let fx = self.fx.get_or_insert_with(|| FxRenderer::new(device, f.format, FxOptions { dither: true }));
        fx.set_srgb_blend(f.srgb_blend());
        fx.add_format(device, wgpu::TextureFormat::Rgba16Float);
        let frame = FxFrame { viewport: [size.0, size.1], scale: 1.0, layers: &layers, backdrop: None, sources: &[] };
        fx.prepare_with_slots(device, queue, &frame, &slots);
        let clear = f.rt.clear_color();
        f.ui.prepare(device, queue, &mut f.rt.text.atlas, &f.rt.instances, size);
        f.ui.draw_with_backdrop(f.encoder, f.target, clear, &mut |phase, site: &EffectSite<'_>| {
            let Some(layer) = at.iter().position(|n| *n == site.index) else { return };
            if let EffectPhase::Draw(pass) = phase {
                fx.draw_layer_in(pass, site.format, site.origin, layer);
            }
        });
    }
}
