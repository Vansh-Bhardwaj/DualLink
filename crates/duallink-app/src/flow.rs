//! The pulses that travel along each connection's line in the picture.
//!
//! Each pulse is a short bright stretch of a line, moved by changing which part of the line
//! draws. A thread steps them about 60 times a second and hands the positions to the
//! renderer directly, so the window's layout never rebuilds for the motion.

use duallink_core::MAX_LINKS;
use rune::Live;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

/// Pulses on each line, evenly spaced.
pub const PULSES: usize = 2;
/// How much of a line one pulse lights up.
pub const LENGTH: f32 = 0.12;

#[derive(Clone)]
pub struct Flow {
    /// The lit stretch of each pulse, as [start, end] fractions of its line:
    /// `pulses[link * PULSES + n]`.
    pub pulses: Vec<Live<[f32; 2]>>,
    /// Trips along the line per second for each link, by its place on screen; 0 stops it.
    rates: Arc<Mutex<[f32; MAX_LINKS]>>,
    running: Arc<AtomicBool>,
}

impl Default for Flow {
    fn default() -> Self {
        Flow {
            pulses: (0..MAX_LINKS * PULSES).map(|_| Live::new([0.0, 0.0])).collect(),
            rates: Arc::new(Mutex::new([0.0; MAX_LINKS])),
            running: Arc::new(AtomicBool::new(false)),
        }
    }
}

impl Flow {
    /// How fast each line's pulses should travel, from each link's speed. A link carrying
    /// more moves its pulses faster; a link carrying nothing shows none.
    pub fn set_speeds(&self, speeds: &[f32], fastest: f32, moving: bool) {
        let mut rates = self.rates.lock().unwrap();
        for (index, rate) in rates.iter_mut().enumerate() {
            let speed = speeds.get(index).copied().unwrap_or(0.0);
            *rate = if moving && speed > 0.05 { 0.22 + 0.9 * (speed / fastest.max(speed)).sqrt() } else { 0.0 };
        }
    }

    /// Starts the thread that moves the pulses. Calling it again does nothing.
    pub fn start(&self) {
        if self.running.swap(true, Ordering::SeqCst) {
            return;
        }
        let flow = self.clone();
        std::thread::spawn(move || {
            let mut phases = [0.0f32; MAX_LINKS];
            let mut last = Instant::now();
            loop {
                std::thread::sleep(Duration::from_millis(16));
                let now = Instant::now();
                let elapsed = now.duration_since(last).as_secs_f32().min(0.1);
                last = now;
                let rates = *flow.rates.lock().unwrap();
                for (link, rate) in rates.iter().enumerate() {
                    if *rate <= 0.0 {
                        for n in 0..PULSES {
                            flow.pulses[link * PULSES + n].set([0.0, 0.0]);
                        }
                        continue;
                    }
                    phases[link] = (phases[link] + elapsed * rate) % 1.0;
                    for n in 0..PULSES {
                        let start = (phases[link] + n as f32 / PULSES as f32) % 1.0;
                        flow.pulses[link * PULSES + n].set(lit(start));
                    }
                }
            }
        });
    }
}

/// The stretch a pulse lights when its head is at `start`. It grows in at the start of the
/// line and shrinks away at the end rather than jumping.
fn lit(start: f32) -> [f32; 2] {
    let head = start * (1.0 + LENGTH);
    [(head - LENGTH).max(0.0), head.min(1.0)]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_pulse_enters_and_leaves_smoothly() {
        assert_eq!(lit(0.0), [0.0, 0.0]);
        let [a, b] = lit(0.5);
        assert!((b - a - LENGTH).abs() < 1e-6, "full length mid-line");
        assert_eq!(lit(1.0)[1], 1.0);
        assert!((lit(1.0)[0] - 1.0).abs() < 1e-6, "gone at the end");
    }

    #[test]
    fn busier_links_move_faster_and_idle_ones_stop() {
        let flow = Flow::default();
        flow.set_speeds(&[80.0, 20.0, 0.0], 80.0, true);
        let rates = *flow.rates.lock().unwrap();
        assert!(rates[0] > rates[1] && rates[1] > 0.0 && rates[2] == 0.0);
        flow.set_speeds(&[80.0], 80.0, false);
        assert_eq!(flow.rates.lock().unwrap()[0], 0.0);
    }
}
