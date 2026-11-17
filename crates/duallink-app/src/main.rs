//! The DualLink window. It shows what the background service is doing and sends it the
//! user's choices; the combining itself happens in the service.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod client;
mod state;
mod theme;
mod view;

use state::Ui;

fn main() {
    if diagnose() {
        return;
    }
    rune::run(rune::AppSpec::<Ui> {
        min_size: Some((900.0, 540.0)),
        scenes: view::SCENES,
        setup: Some(|ui, wake| ui.connect(wake)),
        poll: Some(|ui, _clock, _tx| ui.absorb()),
        ..rune::AppSpec::new("DualLink", (1000.0, 560.0), Ui::default, view::view)
    });
}

/// `DualLink.exe --status | --on | --off` talks to the service from a terminal, for support.
fn diagnose() -> bool {
    let argument = std::env::args().nth(1).unwrap_or_default();
    let mut client = client::Client::default();
    let result = match argument.as_str() {
        "--status" => client.status(),
        "--on" | "--off" => client.status().and_then(|status| {
            client.apply(duallink_core::Config { enabled: argument == "--on", ..status.config })
        }),
        _ => return false,
    };
    match result {
        Ok(status) => println!("{}", serde_json::to_string_pretty(&status).expect("status always serialises")),
        Err(problem) => eprintln!("{problem}"),
    }
    true
}
