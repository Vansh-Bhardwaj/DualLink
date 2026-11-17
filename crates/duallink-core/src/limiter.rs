//! A speed cap shared by every connection on one link.

use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;
use tokio::time::Instant;

/// The longest one wait lasts, so a changed cap takes effect within this long.
const MAX_WAIT: Duration = Duration::from_millis(100);

/// A token bucket. Bytes are paid for after they are read, so a burst never exceeds one
/// buffer and the average holds to the cap.
pub struct Limiter {
    /// Bytes per second; 0 means no cap.
    rate: AtomicU64,
    bucket: Mutex<Bucket>,
}

struct Bucket {
    /// Bytes that may pass right now. Negative while earlier reads are still being paid off.
    tokens: f64,
    filled_at: Instant,
}

impl Default for Limiter {
    fn default() -> Self {
        Limiter::new(0)
    }
}

impl Limiter {
    pub fn new(mbps: u32) -> Self {
        let limiter =
            Limiter { rate: AtomicU64::new(0), bucket: Mutex::new(Bucket { tokens: 0.0, filled_at: Instant::now() }) };
        limiter.set_mbps(mbps);
        limiter
    }

    pub fn mbps(&self) -> u32 {
        (self.rate.load(Ordering::Relaxed) * 8 / 1_000_000) as u32
    }

    pub fn set_mbps(&self, mbps: u32) {
        let rate = u64::from(mbps) * 1_000_000 / 8;
        if self.rate.swap(rate, Ordering::Relaxed) != rate {
            // Debt run up under the old cap must not stall transfers under the new one.
            let mut bucket = self.bucket.lock().unwrap();
            bucket.tokens = 0.0;
            bucket.filled_at = Instant::now();
        }
    }

    /// Waits until `bytes` more may pass.
    pub async fn take(&self, bytes: usize) {
        let mut owed = bytes as f64;
        loop {
            let rate = self.rate.load(Ordering::Relaxed);
            if rate == 0 {
                return;
            }
            let wait = {
                let mut bucket = self.bucket.lock().unwrap();
                let now = Instant::now();
                let rate = rate as f64;
                // At most a tenth of a second of unused allowance carries over.
                bucket.tokens = (bucket.tokens + now.duration_since(bucket.filled_at).as_secs_f64() * rate)
                    .min(rate / 10.0);
                bucket.filled_at = now;
                bucket.tokens -= owed;
                owed = 0.0;
                if bucket.tokens >= 0.0 {
                    return;
                }
                Duration::from_secs_f64(-bucket.tokens / rate)
            };
            if wait <= MAX_WAIT {
                tokio::time::sleep(wait).await;
                return;
            }
            tokio::time::sleep(MAX_WAIT).await;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test(start_paused = true)]
    async fn a_cap_holds_the_average_rate() {
        let limiter = Limiter::new(8); // 1 MB a second
        let started = Instant::now();
        for _ in 0..50 {
            limiter.take(100_000).await; // 5 MB in all
        }
        let secs = started.elapsed().as_secs_f64();
        assert!((4.5..=5.5).contains(&secs), "5 MB at 1 MB/s took {secs:.2}s");
    }

    #[tokio::test(start_paused = true)]
    async fn no_cap_never_waits() {
        let limiter = Limiter::new(0);
        let started = Instant::now();
        limiter.take(usize::MAX / 2).await;
        assert_eq!(started.elapsed(), Duration::ZERO);
    }

    #[tokio::test(start_paused = true)]
    async fn lifting_a_cap_releases_a_waiting_transfer() {
        let limiter = std::sync::Arc::new(Limiter::new(1));
        let waiting = limiter.clone();
        let started = Instant::now();
        let task = tokio::spawn(async move { waiting.take(10_000_000).await });
        tokio::time::sleep(Duration::from_millis(250)).await;
        limiter.set_mbps(0);
        task.await.unwrap();
        assert!(started.elapsed() < Duration::from_millis(500), "waited {:?}", started.elapsed());
    }
}
