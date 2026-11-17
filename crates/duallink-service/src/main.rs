//! The DualLink service. It runs in the background with system rights, combines connections
//! as configured, and takes instructions from the app over a local pipe.
//!
//! `DualLinkService.exe`             started by Windows as a service
//! `DualLinkService.exe --console`   run in a terminal (as administrator) until Ctrl+C
//! `DualLinkService.exe --install`   register and start the service
//! `DualLinkService.exe --uninstall` stop and remove it

mod engine;
mod pipe;
mod procs;
mod store;
mod supervisor;

use std::ffi::OsString;
use std::process::{Command, ExitCode};
use std::sync::Arc;
use std::time::Duration;
use store::log;
use supervisor::Supervisor;
use tokio::sync::Notify;
use windows_service::service::{
    ServiceAccess, ServiceAction, ServiceActionType, ServiceControl, ServiceControlAccept, ServiceErrorControl,
    ServiceExitCode, ServiceFailureActions, ServiceFailureResetPeriod, ServiceInfo, ServiceStartType, ServiceState,
    ServiceStatus, ServiceType,
};
use windows_service::service_control_handler::{self, ServiceControlHandlerResult};
use windows_service::service_manager::{ServiceManager, ServiceManagerAccess};
use windows_service::{define_windows_service, service_dispatcher};

const SERVICE: &str = "DualLinkService";
const DISPLAY: &str = "DualLink";
const DESCRIPTION: &str = "Combines this PC's internet connections. Stopping it returns all traffic to normal.";
const FIREWALL_RULE: &str = "DualLink Service";

fn main() -> ExitCode {
    let argument = std::env::args().nth(1).unwrap_or_default();
    let result = match argument.as_str() {
        "--console" => console(),
        "--install" => install(),
        "--uninstall" => uninstall(),
        "" => service_dispatcher::start(SERVICE, ffi_service_main).map_err(|e| e.to_string()),
        other => Err(format!("Unknown option {other}. Use --console, --install or --uninstall.")),
    };
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(problem) => {
            eprintln!("{problem}");
            log(&problem);
            ExitCode::FAILURE
        }
    }
}

/// Runs the service's work until `stop` is notified.
async fn work(stop: Arc<Notify>) -> Result<(), String> {
    let (supervisor, listener) = Supervisor::new().await.map_err(|e| format!("The listener could not open: {e}"))?;
    log(&format!("DualLink service {} started.", env!("CARGO_PKG_VERSION")));
    let serving = tokio::spawn(pipe::serve(supervisor.clone()));
    supervisor.run(listener, stop).await;
    serving.abort();
    log("DualLink service stopped.");
    Ok(())
}

fn runtime() -> Result<tokio::runtime::Runtime, String> {
    tokio::runtime::Builder::new_multi_thread().enable_all().build().map_err(|e| e.to_string())
}

fn console() -> Result<(), String> {
    allow_through_firewall()?;
    let stop = Arc::new(Notify::new());
    let on_ctrl_c = stop.clone();
    runtime()?.block_on(async move {
        tokio::spawn(async move {
            let _ = tokio::signal::ctrl_c().await;
            on_ctrl_c.notify_one();
        });
        work(stop).await
    })
}

define_windows_service!(ffi_service_main, service_main);

fn service_main(_arguments: Vec<OsString>) {
    if let Err(problem) = run_as_service() {
        log(&format!("The service failed: {problem}"));
    }
}

fn run_as_service() -> Result<(), String> {
    let stop = Arc::new(Notify::new());
    let on_stop = stop.clone();
    let handle = service_control_handler::register(SERVICE, move |control| match control {
        ServiceControl::Stop | ServiceControl::Shutdown => {
            on_stop.notify_one();
            ServiceControlHandlerResult::NoError
        }
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        _ => ServiceControlHandlerResult::NotImplemented,
    })
    .map_err(|e| e.to_string())?;

    let report = |state: ServiceState, exit: u32| {
        let _ = handle.set_service_status(ServiceStatus {
            service_type: ServiceType::OWN_PROCESS,
            current_state: state,
            controls_accepted: if state == ServiceState::Running {
                ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN
            } else {
                ServiceControlAccept::empty()
            },
            exit_code: ServiceExitCode::Win32(exit),
            checkpoint: 0,
            wait_hint: Duration::from_secs(10),
            process_id: None,
        });
    };

    report(ServiceState::Running, 0);
    let result = runtime().and_then(|rt| rt.block_on(work(stop)));
    report(ServiceState::Stopped, u32::from(result.is_err()));
    result
}

fn install() -> Result<(), String> {
    let executable = std::env::current_exe().map_err(|e| e.to_string())?;
    let manager = ServiceManager::local_computer(
        None::<&str>,
        ServiceManagerAccess::CONNECT | ServiceManagerAccess::CREATE_SERVICE,
    )
    .map_err(|e| format!("Windows refused access to its services (run as administrator): {e}"))?;
    let info = ServiceInfo {
        name: SERVICE.into(),
        display_name: DISPLAY.into(),
        service_type: ServiceType::OWN_PROCESS,
        start_type: ServiceStartType::AutoStart,
        error_control: ServiceErrorControl::Normal,
        executable_path: executable,
        launch_arguments: Vec::new(),
        dependencies: Vec::new(),
        account_name: None, // LocalSystem
        account_password: None,
    };
    let access = ServiceAccess::CHANGE_CONFIG | ServiceAccess::START | ServiceAccess::QUERY_STATUS;
    let service = match manager.open_service(SERVICE, access) {
        Ok(service) => {
            service.change_config(&info).map_err(|e| e.to_string())?;
            service
        }
        Err(_) => manager.create_service(&info, access).map_err(|e| format!("The service could not be created: {e}"))?,
    };
    let _ = service.set_description(DESCRIPTION);
    // If the service ever stops by itself, Windows starts it again.
    let restart = |seconds| ServiceAction { action_type: ServiceActionType::Restart, delay: Duration::from_secs(seconds) };
    let _ = service.update_failure_actions(ServiceFailureActions {
        reset_period: ServiceFailureResetPeriod::After(Duration::from_secs(3600)),
        reboot_msg: None,
        command: None,
        actions: Some(vec![restart(2), restart(10), restart(60)]),
    });
    let _ = service.set_failure_actions_on_non_crash_failures(true);
    allow_through_firewall()?;
    if service.query_status().map_err(|e| e.to_string())?.current_state == ServiceState::Stopped {
        service.start::<&str>(&[]).map_err(|e| format!("The service could not be started: {e}"))?;
    }
    println!("DualLink service installed and started.");
    Ok(())
}

fn uninstall() -> Result<(), String> {
    let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
        .map_err(|e| format!("Windows refused access to its services (run as administrator): {e}"))?;
    let access = ServiceAccess::STOP | ServiceAccess::DELETE | ServiceAccess::QUERY_STATUS;
    if let Ok(service) = manager.open_service(SERVICE, access) {
        let _ = service.stop();
        for _ in 0..50 {
            if service.query_status().is_ok_and(|s| s.current_state == ServiceState::Stopped) {
                break;
            }
            std::thread::sleep(Duration::from_millis(100));
        }
        service.delete().map_err(|e| format!("The service could not be removed: {e}"))?;
    }
    let _ = netsh(&["advfirewall", "firewall", "delete", "rule", &format!("name={FIREWALL_RULE}")]);
    println!("DualLink service removed.");
    Ok(())
}

/// Connections the engine turns around arrive at this program's listener looking like
/// incoming connections, so Windows Firewall has to let this program accept them. The
/// listener closes anything that is not one of those connections.
fn allow_through_firewall() -> Result<(), String> {
    let executable = std::env::current_exe().map_err(|e| e.to_string())?;
    let name = format!("name={FIREWALL_RULE}");
    let _ = netsh(&["advfirewall", "firewall", "delete", "rule", &name]);
    let program = format!("program={}", executable.display());
    let added = netsh(&[
        "advfirewall", "firewall", "add", "rule", &name, "dir=in", "action=allow", &program, "protocol=TCP",
        "profile=any",
    ]);
    if added { Ok(()) } else { Err("Windows Firewall could not be told to allow DualLink (run as administrator).".into()) }
}

fn netsh(arguments: &[&str]) -> bool {
    let system = std::env::var_os("SystemRoot").map_or_else(|| r"C:\Windows".into(), std::path::PathBuf::from);
    Command::new(system.join("System32").join("netsh.exe"))
        .args(arguments)
        .output()
        .is_ok_and(|output| output.status.success())
}
