//! DualLink's icon in the notification area: click to open the window, right-click for a
//! short menu.

use crate::state::Ui;
use rune::AppHandle;
use tray_icon::menu::{Menu, MenuEvent, MenuItem, PredefinedMenuItem};
use tray_icon::{Icon, MouseButton, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent};

/// Puts the icon in the notification area. Keep what it returns alive for as long as the
/// icon should show. Call on the thread that runs the window's event loop.
pub fn install(app: AppHandle<Ui>) -> Option<TrayIcon> {
    let open = MenuItem::new("Open DualLink", true, None);
    let toggle = MenuItem::new("Turn combining on or off", true, None);
    let quit = MenuItem::new("Quit DualLink", true, None);
    let menu = Menu::new();
    menu.append_items(&[&open, &toggle, &PredefinedMenuItem::separator(), &quit]).ok()?;
    let (open_id, toggle_id, quit_id) = (open.id().clone(), toggle.id().clone(), quit.id().clone());

    let on_menu = app.clone();
    MenuEvent::set_event_handler(Some(move |event: MenuEvent| {
        if event.id == open_id {
            on_menu.open();
        } else if event.id == toggle_id {
            on_menu.update(|s: &mut Ui, _| {
                s.config.enabled = !s.config.enabled;
                s.send();
            });
        } else if event.id == quit_id {
            on_menu.quit();
        }
    }));
    TrayIconEvent::set_event_handler(Some(move |event: TrayIconEvent| {
        if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = event {
            app.open();
        }
    }));

    // Resource 1 is the icon build.rs embeds in the exe.
    let icon = Icon::from_resource(1, None).ok()?;
    TrayIconBuilder::new().with_tooltip("DualLink").with_icon(icon).with_menu(Box::new(menu)).build().ok()
}
