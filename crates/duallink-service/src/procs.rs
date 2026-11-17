//! Finds which program owns a local port, so each new connection can be matched to a program.

use std::collections::HashMap;
use std::time::{Duration, Instant};
use windows::Win32::Foundation::{CloseHandle, ERROR_INSUFFICIENT_BUFFER, NO_ERROR};
use windows::Win32::NetworkManagement::IpHelper::{
    GetExtendedTcpTable, GetExtendedUdpTable, MIB_TCP6ROW_OWNER_PID, MIB_TCPROW_OWNER_PID, MIB_UDP6ROW_OWNER_PID,
    MIB_UDPROW_OWNER_PID, TCP_TABLE_OWNER_PID_ALL, UDP_TABLE_OWNER_PID,
};
use windows::Win32::Networking::WinSock::{AF_INET, AF_INET6};
use windows::Win32::System::Threading::{
    OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
};
use windows::core::PWSTR;

/// A process id can be handed to a new program once the old one exits, so paths are only
/// trusted briefly.
const PATH_TTL: Duration = Duration::from_secs(20);

#[derive(Clone, Copy, PartialEq, Eq, Hash)]
pub enum Table {
    Tcp4,
    Tcp6,
    Udp4,
    Udp6,
}

#[derive(Default)]
pub struct Processes {
    paths: HashMap<u32, (Option<String>, Instant)>,
}

impl Processes {
    /// The process that owns `port` right now. Reads the whole table each time, because a
    /// port that was just opened is by definition not in any older copy.
    pub fn owner(&self, table: Table, port: u16) -> Option<u32> {
        let rows = read_table(table)?;
        let pid = match table {
            Table::Tcp4 => find(&rows, |r: &MIB_TCPROW_OWNER_PID| (r.dwLocalPort, r.dwOwningPid), port),
            Table::Tcp6 => find(&rows, |r: &MIB_TCP6ROW_OWNER_PID| (r.dwLocalPort, r.dwOwningPid), port),
            Table::Udp4 => find(&rows, |r: &MIB_UDPROW_OWNER_PID| (r.dwLocalPort, r.dwOwningPid), port),
            Table::Udp6 => find(&rows, |r: &MIB_UDP6ROW_OWNER_PID| (r.dwLocalPort, r.dwOwningPid), port),
        };
        pid.filter(|&pid| pid != 0)
    }

    /// The program's full path, lower-cased.
    pub fn path(&mut self, pid: u32, now: Instant) -> Option<String> {
        if let Some((path, read)) = self.paths.get(&pid)
            && now.duration_since(*read) < PATH_TTL {
                return path.clone();
            }
        if self.paths.len() > 4096 {
            self.paths.retain(|_, (_, read)| now.duration_since(*read) < PATH_TTL);
        }
        let path = image_path(pid).map(|p| p.to_lowercase());
        self.paths.insert(pid, (path.clone(), now));
        path
    }
}

/// The tables start with a `u32` row count followed by the rows.
fn find<R>(buffer: &[u32], key: impl Fn(&R) -> (u32, u32), port: u16) -> Option<u32> {
    let count = *buffer.first()? as usize;
    let bytes = buffer.len() * 4;
    if 4 + count * size_of::<R>() > bytes {
        return None;
    }
    // Ports are stored in network byte order in the low 16 bits.
    let wanted = u32::from(port.to_be());
    let rows = unsafe { std::slice::from_raw_parts(buffer.as_ptr().add(1).cast::<R>(), count) };
    rows.iter().map(key).find(|(local_port, _)| local_port & 0xFFFF == wanted).map(|(_, pid)| pid)
}

fn read_table(table: Table) -> Option<Vec<u32>> {
    let mut size = 0u32;
    let call = |buffer: Option<*mut core::ffi::c_void>, size: &mut u32| unsafe {
        match table {
            Table::Tcp4 => GetExtendedTcpTable(buffer, size, false, AF_INET.0.into(), TCP_TABLE_OWNER_PID_ALL, 0),
            Table::Tcp6 => GetExtendedTcpTable(buffer, size, false, AF_INET6.0.into(), TCP_TABLE_OWNER_PID_ALL, 0),
            Table::Udp4 => GetExtendedUdpTable(buffer, size, false, AF_INET.0.into(), UDP_TABLE_OWNER_PID, 0),
            Table::Udp6 => GetExtendedUdpTable(buffer, size, false, AF_INET6.0.into(), UDP_TABLE_OWNER_PID, 0),
        }
    };
    let mut buffer: Vec<u32> = Vec::new();
    // The table can grow between the sizing call and the read.
    for _ in 0..4 {
        let result = call(if buffer.is_empty() { None } else { Some(buffer.as_mut_ptr().cast()) }, &mut size);
        if result == NO_ERROR.0 && !buffer.is_empty() {
            return Some(buffer);
        }
        if result != ERROR_INSUFFICIENT_BUFFER.0 && result != NO_ERROR.0 {
            return None;
        }
        buffer = vec![0u32; (size as usize).div_ceil(4) + 64];
        size = (buffer.len() * 4) as u32;
    }
    None
}

fn image_path(pid: u32) -> Option<String> {
    unsafe {
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buffer = [0u16; 1024];
        let mut length = buffer.len() as u32;
        let result = QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, PWSTR(buffer.as_mut_ptr()), &mut length);
        let _ = CloseHandle(process);
        result.ok()?;
        Some(String::from_utf16_lossy(&buffer[..length as usize]))
    }
}

/// Whether a program's path matches an entry: a full path matches exactly, a bare file name
/// matches any program with that name. Both sides are lower-case.
pub fn matches(path: &str, entry: &str) -> bool {
    if entry.contains('\\') || entry.contains('/') {
        path == entry
    } else {
        path.rsplit(['\\', '/']).next() == Some(entry)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::{TcpListener, UdpSocket};

    #[test]
    fn a_program_is_matched_by_name_or_full_path() {
        let path = r"c:\program files (x86)\steam\steam.exe";
        assert!(matches(path, "steam.exe"));
        assert!(matches(path, r"c:\program files (x86)\steam\steam.exe"));
        assert!(!matches(path, "team.exe"));
        assert!(!matches(path, r"d:\steam\steam.exe"));
    }

    #[test]
    fn this_process_owns_the_ports_it_opens() {
        let mut processes = Processes::default();
        let me = std::process::id();
        let tcp = TcpListener::bind("127.0.0.1:0").unwrap();
        assert_eq!(processes.owner(Table::Tcp4, tcp.local_addr().unwrap().port()), Some(me));
        let udp = UdpSocket::bind("127.0.0.1:0").unwrap();
        assert_eq!(processes.owner(Table::Udp4, udp.local_addr().unwrap().port()), Some(me));
        let tcp6 = TcpListener::bind("[::1]:0").unwrap();
        assert_eq!(processes.owner(Table::Tcp6, tcp6.local_addr().unwrap().port()), Some(me));

        let path = processes.path(me, Instant::now()).unwrap();
        let exe = std::env::current_exe().unwrap().to_string_lossy().to_lowercase();
        assert_eq!(path, exe);
    }
}
