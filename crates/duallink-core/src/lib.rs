//! Shared DualLink logic: the connection model, link discovery, the router that spreads
//! connections over links, and the messages the app and the service exchange.

pub mod ipc;
pub mod limiter;
pub mod links;
pub mod model;
pub mod nat;
pub mod packet;
pub mod relay;
pub mod router;

pub use model::*;
