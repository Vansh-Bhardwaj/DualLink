//! Finds the internet connections on this PC.

use crate::model::{LinkInfo, LinkKind};

/// Names an adapter the way a person would, from what Windows reports about it.
///
/// A phone sharing its connection over USB shows up as an ordinary Ethernet adapter, so its
/// driver description is the only thing that tells it apart.
pub fn classify(if_type: u32, description: &str) -> LinkKind {
    const ETHERNET: u32 = 6;
    const WIFI: u32 = 71;
    const WWAN: [u32; 2] = [243, 244];
    let d = description.to_ascii_lowercase();
    let tether = ["remote ndis", "rndis", "apple mobile device ethernet", "usb tethering", "android usb ethernet"];
    if WWAN.contains(&if_type) {
        LinkKind::Cellular
    } else if if_type == WIFI {
        LinkKind::Wifi
    } else if tether.iter().any(|t| d.contains(t)) {
        LinkKind::UsbTether
    } else if if_type == ETHERNET {
        LinkKind::Ethernet
    } else {
        LinkKind::Other
    }
}

/// Adapters that carry other software's private traffic and are never a way to the internet
/// in their own right.
pub fn is_virtual(description: &str) -> bool {
    let d = description.to_ascii_lowercase();
    ["hyper-v", "virtualbox", "vmware", "loopback", "wintun", "wireguard", "tap-windows", "wsl", "bluetooth"]
        .iter()
        .any(|v| d.contains(v))
}

/// Every adapter that is up and has an IPv4 address and a gateway, most usual kinds first.
#[cfg(windows)]
pub fn discover() -> std::io::Result<Vec<LinkInfo>> {
    use std::net::Ipv4Addr;
    use windows_sys::Win32::Foundation::{ERROR_BUFFER_OVERFLOW, ERROR_NO_DATA, ERROR_SUCCESS};
    use windows_sys::Win32::NetworkManagement::IpHelper::{
        GAA_FLAG_INCLUDE_GATEWAYS, GAA_FLAG_SKIP_ANYCAST, GAA_FLAG_SKIP_DNS_SERVER, GAA_FLAG_SKIP_MULTICAST,
        GetAdaptersAddresses, IP_ADAPTER_ADDRESSES_LH,
    };
    use windows_sys::Win32::NetworkManagement::Ndis::IfOperStatusUp;
    use windows_sys::Win32::Networking::WinSock::{AF_INET, SOCKADDR, SOCKADDR_IN};

    unsafe fn ipv4(address: *const SOCKADDR) -> Option<Ipv4Addr> {
        unsafe {
            if address.is_null() || (*address).sa_family != AF_INET {
                return None;
            }
            let v4 = &*address.cast::<SOCKADDR_IN>();
            Some(Ipv4Addr::from(v4.sin_addr.S_un.S_addr.to_ne_bytes()))
        }
    }

    unsafe fn wide(text: *const u16) -> String {
        unsafe {
            if text.is_null() {
                return String::new();
            }
            let len = (0..).take_while(|&i| *text.add(i) != 0).count();
            String::from_utf16_lossy(std::slice::from_raw_parts(text, len))
        }
    }

    let flags =
        GAA_FLAG_INCLUDE_GATEWAYS | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
    // The table can grow between the sizing call and the real one, so retry a few times.
    let mut size: u32 = 32 * 1024;
    let mut buffer: Vec<u64> = Vec::new();
    let mut result = ERROR_BUFFER_OVERFLOW;
    for _ in 0..4 {
        buffer.resize((size as usize).div_ceil(8), 0);
        result = unsafe {
            GetAdaptersAddresses(AF_INET as u32, flags, std::ptr::null(), buffer.as_mut_ptr().cast(), &mut size)
        };
        if result != ERROR_BUFFER_OVERFLOW {
            break;
        }
    }
    if result == ERROR_NO_DATA {
        return Ok(Vec::new());
    }
    if result != ERROR_SUCCESS {
        return Err(std::io::Error::from_raw_os_error(result as i32));
    }

    let mut links = Vec::new();
    let mut adapter = buffer.as_ptr().cast::<IP_ADAPTER_ADDRESSES_LH>();
    while !adapter.is_null() {
        let a = unsafe { &*adapter };
        adapter = a.Next;
        if a.OperStatus != IfOperStatusUp {
            continue;
        }
        let description = unsafe { wide(a.Description) };
        let kind = classify(a.IfType, &description);
        if kind == LinkKind::Other || is_virtual(&description) {
            continue;
        }
        let mut address = None;
        let mut unicast = a.FirstUnicastAddress;
        while !unicast.is_null() && address.is_none() {
            let u = unsafe { &*unicast };
            address = unsafe { ipv4(u.Address.lpSockaddr) }.filter(|ip| !ip.is_loopback() && !ip.is_link_local());
            unicast = u.Next;
        }
        let mut gateway = None;
        let mut next = a.FirstGatewayAddress;
        while !next.is_null() && gateway.is_none() {
            let g = unsafe { &*next };
            gateway = unsafe { ipv4(g.Address.lpSockaddr) }.filter(|ip| !ip.is_unspecified());
            next = g.Next;
        }
        let (Some(address), Some(gateway)) = (address, gateway) else { continue };
        let id = unsafe { std::ffi::CStr::from_ptr(a.AdapterName.cast()) }.to_string_lossy().into_owned();
        links.push(LinkInfo {
            id,
            name: unsafe { wide(a.FriendlyName) },
            description,
            kind,
            if_index: unsafe { a.Anonymous1.Anonymous.IfIndex },
            address,
            gateway,
        });
    }
    links.sort_by_key(|l| {
        let order = match l.kind {
            LinkKind::Ethernet => 0,
            LinkKind::Wifi => 1,
            LinkKind::UsbTether => 2,
            LinkKind::Cellular => 3,
            LinkKind::Other => 4,
        };
        (order, l.name.clone())
    });
    Ok(links)
}

#[cfg(not(windows))]
pub fn discover() -> std::io::Result<Vec<LinkInfo>> {
    Ok(Vec::new())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn adapters_are_named_as_people_know_them() {
        assert_eq!(classify(6, "Realtek PCIe GbE Family Controller"), LinkKind::Ethernet);
        assert_eq!(classify(71, "Intel(R) Wi-Fi 6 AX200 160MHz"), LinkKind::Wifi);
        assert_eq!(classify(6, "Remote NDIS based Internet Sharing Device"), LinkKind::UsbTether);
        assert_eq!(classify(6, "Apple Mobile Device Ethernet"), LinkKind::UsbTether);
        assert_eq!(classify(243, "Generic Mobile Broadband Adapter"), LinkKind::Cellular);
        assert_eq!(classify(131, "Some tunnel"), LinkKind::Other);
    }

    #[test]
    fn virtual_adapters_are_left_out() {
        assert!(is_virtual("Hyper-V Virtual Ethernet Adapter"));
        assert!(is_virtual("WireGuard Tunnel"));
        assert!(!is_virtual("Realtek PCIe GbE Family Controller"));
    }

    #[cfg(windows)]
    #[test]
    fn discovery_runs_on_this_pc() {
        for link in discover().unwrap() {
            assert!(!link.id.is_empty() && link.if_index != 0, "{link:?}");
            assert!(!link.address.is_loopback() && !link.gateway.is_unspecified(), "{link:?}");
        }
    }
}
