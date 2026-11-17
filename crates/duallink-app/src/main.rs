//! The DualLink window. It shows what the background service is doing and sends it the
//! user's choices; the combining itself happens in the service.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod apps;
mod client;
mod flow;
mod fx;
mod prefs;
mod state;
mod theme;
mod tray;
mod view;

use state::Ui;

fn main() {
    if diagnose() {
        return;
    }
    let spec = rune::AppSpec::<Ui> {
        min_size: Some((640.0, 480.0)),
        scenes: view::SCENES,
        // Headless renders show the named scenes, not this PC's service.
        setup: Some(|ui, wake| {
            if rune::tooling::headless_run().is_none() {
                ui.connect(wake);
            }
        }),
        poll: Some(|ui, _clock, _tx| ui.absorb()),
        ..rune::AppSpec::new("DualLink", (1000.0, 660.0), Ui::default, view::view)
    };
    let headless = std::env::args().any(|a| a.starts_with("--snap") || a.starts_with("--tree") || a.starts_with("--script"));
    let background = std::env::args().any(|a| a == "--background");
    let mut ui = Ui::default();
    ui.prefs = prefs::Prefs::load();
    ui.autostart = prefs::autostart::enabled();
    let metal = fx::Metal::new(ui.metal_moving.clone());
    let mut app = rune::App::new(spec)
        .init(move || ui)
        .titlebar(rune::Titlebar::new(40.0))
        .compositor(metal)
        .keep_running()
        // Closing hides the window while DualLink stays in the notification area, unless the
        // user turned that off.
        .on_close(|ui, tx| {
            if !ui.prefs.keep_in_tray {
                tx.quit();
            }
        })
        .on_window(set_window_icon);
    if background {
        app = app.windowless();
    }
    // The icon lives as long as the app; it is made on this thread, which runs the window's loop.
    let _tray = if headless { None } else { tray::install(app.handle()) };
    app.run();
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

/// Puts DualLink's icon (embedded in the exe by build.rs) in the title bar and Alt+Tab.
fn set_window_icon(window: &rune::WindowHandle) {
    use rune::raw_window_handle::RawWindowHandle;
    use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::UI::WindowsAndMessaging::{
        ICON_BIG, ICON_SMALL, IMAGE_ICON, LR_DEFAULTCOLOR, LoadImageW, SM_CXICON, SM_CXSMICON, SendMessageW, WM_SETICON,
        GetSystemMetrics,
    };
    use windows::core::PCWSTR;
    let _ = window.with_raw_handle(|handle| {
        let RawWindowHandle::Win32(win32) = handle.as_raw() else { return };
        let hwnd = HWND(win32.hwnd.get() as *mut _);
        unsafe {
            let Ok(module) = GetModuleHandleW(None) else { return };
            for (kind, metric) in [(ICON_BIG, SM_CXICON), (ICON_SMALL, SM_CXSMICON)] {
                let size = GetSystemMetrics(metric);
                // Resource 1 is the icon build.rs embeds, named by number as Windows expects.
                let resource = PCWSTR(std::ptr::without_provenance(1));
                if let Ok(icon) = LoadImageW(Some(module.into()), resource, IMAGE_ICON, size, size, LR_DEFAULTCOLOR) {
                    SendMessageW(hwnd, WM_SETICON, Some(WPARAM(kind as usize)), Some(LPARAM(icon.0 as isize)));
                }
            }
        }
    });
}