//! The local pipe the app talks to the service through.

use crate::store::log;
use crate::supervisor::Supervisor;
use duallink_core::ipc::{self, MAX_MESSAGE_BYTES, PIPE_NAME, Request, Response};
use std::ffi::c_void;
use std::sync::Arc;
use std::time::Duration;
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
use windows::Win32::Security::Authorization::{ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1};
use windows::Win32::Security::{PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES};
use windows::core::w;

/// A client that sends nothing for this long is disconnected.
const IDLE: Duration = Duration::from_secs(120);

/// Who may use the pipe: the system and administrators fully, and people signed in at this
/// PC to read and write. Nobody over the network.
fn security() -> windows::core::Result<PSECURITY_DESCRIPTOR> {
    let mut descriptor = PSECURITY_DESCRIPTOR::default();
    unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            w!("D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)"),
            SDDL_REVISION_1,
            &mut descriptor,
            None,
        )?;
    }
    Ok(descriptor)
}

fn create(first: bool, descriptor: PSECURITY_DESCRIPTOR) -> std::io::Result<NamedPipeServer> {
    let mut attributes = SECURITY_ATTRIBUTES {
        nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: descriptor.0,
        bInheritHandle: false.into(),
    };
    unsafe {
        ServerOptions::new()
            .first_pipe_instance(first)
            .reject_remote_clients(true)
            .max_instances(16)
            .create_with_security_attributes_raw(PIPE_NAME, (&raw mut attributes).cast::<c_void>())
    }
}

/// Serves clients until the task is aborted.
pub async fn serve(supervisor: Arc<Supervisor>) {
    // The descriptor is allocated once and lives as long as the service.
    let descriptor = match security() {
        Ok(descriptor) => descriptor.0 as usize,
        Err(error) => {
            log(&format!("The control pipe could not be secured, so it was not opened: {error}"));
            return;
        }
    };
    let mut first = true;
    loop {
        let server = match create(first, PSECURITY_DESCRIPTOR(descriptor as *mut c_void)) {
            Ok(server) => server,
            Err(error) => {
                log(&format!("The control pipe could not be opened: {error}"));
                tokio::time::sleep(Duration::from_secs(if first { 5 } else { 1 })).await;
                continue;
            }
        };
        first = false;
        if server.connect().await.is_err() {
            continue;
        }
        tokio::spawn(client(server, supervisor.clone()));
    }
}

async fn client(pipe: NamedPipeServer, supervisor: Arc<Supervisor>) {
    let (read, mut write) = tokio::io::split(pipe);
    let mut reader = BufReader::new(read);
    let mut line = Vec::new();
    loop {
        line.clear();
        let limited = (&mut reader).take(MAX_MESSAGE_BYTES as u64 + 1);
        let mut limited = limited;
        let read = tokio::time::timeout(IDLE, limited.read_until(b'\n', &mut line)).await;
        match read {
            Ok(Ok(n)) if n > 0 && line.last() == Some(&b'\n') => {}
            _ => return, // closed, idle, failed, or a line with no end within the limit
        }
        let response = match ipc::decode::<Request>(&line[..line.len() - 1]) {
            Ok(request) => supervisor.answer(request),
            Err(message) => Response::Error { message },
        };
        if write.write_all(&ipc::encode(&response)).await.is_err() {
            return;
        }
    }
}
