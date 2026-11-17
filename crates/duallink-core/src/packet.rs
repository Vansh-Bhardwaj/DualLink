//! Reads and rewrites the Ethernet frames the packet engine sees. No I/O happens here.

use std::net::{Ipv4Addr, Ipv6Addr, SocketAddrV4};

const ETH: usize = 14;
const ETHERTYPE_V4: [u8; 2] = [0x08, 0x00];
const ETHERTYPE_V6: [u8; 2] = [0x86, 0xDD];
pub const TCP: u8 = 6;
pub const UDP: u8 = 17;

pub const FIN: u8 = 0x01;
pub const SYN: u8 = 0x02;
pub const RST: u8 = 0x04;
pub const ACK: u8 = 0x10;

/// The addresses of one IPv4 TCP or UDP packet inside a frame.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct V4 {
    pub protocol: u8,
    pub source: SocketAddrV4,
    pub destination: SocketAddrV4,
    /// TCP flags; 0 for UDP.
    pub flags: u8,
    /// Where the TCP or UDP header starts in the frame.
    l4: usize,
    /// Where the IP packet ends in the frame (frames can carry padding after it).
    end: usize,
}

impl V4 {
    pub fn is_tcp(&self) -> bool {
        self.protocol == TCP
    }

    /// The first packet of a new connection.
    pub fn opens_connection(&self) -> bool {
        self.protocol == TCP && self.flags & (SYN | ACK) == SYN
    }
}

/// Reads an unfragmented IPv4 TCP or UDP packet. Anything else is `None` and should pass untouched.
pub fn parse_v4(frame: &[u8]) -> Option<V4> {
    if frame.len() < ETH + 20 || frame[12..14] != ETHERTYPE_V4 {
        return None;
    }
    let ip = &frame[ETH..];
    if ip[0] >> 4 != 4 {
        return None;
    }
    let header = usize::from(ip[0] & 0x0F) * 4;
    let total = usize::from(u16::from_be_bytes([ip[2], ip[3]]));
    // A later fragment has no ports, and a first fragment's checksum can't be redone alone.
    let fragmented = u16::from_be_bytes([ip[6], ip[7]]) & 0x3FFF != 0;
    if header < 20 || total < header || ip.len() < total || fragmented {
        return None;
    }
    let protocol = ip[9];
    let l4 = ETH + header;
    let end = ETH + total;
    let need = match protocol {
        TCP => 20,
        UDP => 8,
        _ => return None,
    };
    if end < l4 + need {
        return None;
    }
    let port = |at: usize| u16::from_be_bytes([frame[at], frame[at + 1]]);
    let address = |at: usize| Ipv4Addr::new(frame[at], frame[at + 1], frame[at + 2], frame[at + 3]);
    Some(V4 {
        protocol,
        source: SocketAddrV4::new(address(ETH + 12), port(l4)),
        destination: SocketAddrV4::new(address(ETH + 16), port(l4 + 2)),
        flags: if protocol == TCP { frame[l4 + 13] } else { 0 },
        l4,
        end,
    })
}

/// Gives a TCP packet new addresses and makes it look as if it arrived from the network:
/// the Ethernet addresses swap, and both checksums are recomputed in full (outgoing packets
/// are often handed over with checksums left for the network card to fill in).
pub fn turn_around(frame: &mut [u8], packet: &V4, source: SocketAddrV4, destination: SocketAddrV4) {
    debug_assert_eq!(packet.protocol, TCP);
    let (to, from) = frame.split_at_mut(6);
    to.swap_with_slice(&mut from[..6]);

    frame[ETH + 12..ETH + 16].copy_from_slice(&source.ip().octets());
    frame[ETH + 16..ETH + 20].copy_from_slice(&destination.ip().octets());
    frame[packet.l4..packet.l4 + 2].copy_from_slice(&source.port().to_be_bytes());
    frame[packet.l4 + 2..packet.l4 + 4].copy_from_slice(&destination.port().to_be_bytes());

    frame[ETH + 10..ETH + 12].fill(0);
    let ip_sum = finish(sum(&frame[ETH..packet.l4], 0));
    frame[ETH + 10..ETH + 12].copy_from_slice(&ip_sum.to_be_bytes());

    frame[packet.l4 + 16..packet.l4 + 18].fill(0);
    let length = (packet.end - packet.l4) as u32;
    let pseudo = sum(&frame[ETH + 12..ETH + 20], u32::from(TCP) + length);
    let tcp_sum = finish(sum(&frame[packet.l4..packet.end], pseudo));
    frame[packet.l4 + 16..packet.l4 + 18].copy_from_slice(&tcp_sum.to_be_bytes());
}

/// An IPv6 packet DualLink may want to turn away so the program retries over IPv4.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct V6 {
    pub protocol: u8,
    pub source: Ipv6Addr,
    pub source_port: u16,
    pub destination: Ipv6Addr,
    pub destination_port: u16,
    pub flags: u8,
}

impl V6 {
    pub fn opens_connection(&self) -> bool {
        self.protocol == TCP && self.flags & (SYN | ACK) == SYN
    }

    /// Whether the destination is on the internet, as opposed to this PC or the local network.
    pub fn is_global(&self) -> bool {
        self.destination.segments()[0] & 0xE000 == 0x2000
    }
}

/// Reads an IPv6 TCP or UDP packet that has no extension headers.
pub fn parse_v6(frame: &[u8]) -> Option<V6> {
    if frame.len() < ETH + 40 + 8 || frame[12..14] != ETHERTYPE_V6 || frame[ETH] >> 4 != 6 {
        return None;
    }
    let protocol = frame[ETH + 6];
    let l4 = ETH + 40;
    let need = match protocol {
        TCP => 20,
        UDP => 8,
        _ => return None,
    };
    if frame.len() < l4 + need {
        return None;
    }
    let address = |at: usize| Ipv6Addr::from(<[u8; 16]>::try_from(&frame[at..at + 16]).unwrap());
    Some(V6 {
        protocol,
        source: address(ETH + 8),
        source_port: u16::from_be_bytes([frame[l4], frame[l4 + 1]]),
        destination: address(ETH + 24),
        destination_port: u16::from_be_bytes([frame[l4 + 2], frame[l4 + 3]]),
        flags: if protocol == TCP { frame[l4 + 13] } else { 0 },
    })
}

/// Turns an outgoing IPv6 connection attempt into the refusal the program would get from a
/// closed port, in place, and returns the new frame length. The program then tries IPv4
/// straight away instead of waiting for a timeout.
pub fn refuse_v6(frame: &mut [u8]) -> usize {
    let l4 = ETH + 40;
    let (to, from) = frame.split_at_mut(6);
    to.swap_with_slice(&mut from[..6]);
    let (source, destination) = frame[ETH + 8..ETH + 40].split_at_mut(16);
    source.swap_with_slice(destination);
    frame[ETH + 4..ETH + 6].copy_from_slice(&20u16.to_be_bytes()); // payload: a bare TCP header
    frame[ETH + 7] = 64; // hop limit

    let their_sequence = u32::from_be_bytes(frame[l4 + 4..l4 + 8].try_into().unwrap());
    let (source_port, destination_port) = ([frame[l4], frame[l4 + 1]], [frame[l4 + 2], frame[l4 + 3]]);
    frame[l4..l4 + 2].copy_from_slice(&destination_port);
    frame[l4 + 2..l4 + 4].copy_from_slice(&source_port);
    frame[l4 + 4..l4 + 8].fill(0);
    frame[l4 + 8..l4 + 12].copy_from_slice(&their_sequence.wrapping_add(1).to_be_bytes());
    frame[l4 + 12] = 5 << 4; // header length, no options
    frame[l4 + 13] = RST | ACK;
    frame[l4 + 14..l4 + 20].fill(0); // window, checksum, urgent

    let pseudo = sum(&frame[ETH + 8..ETH + 40], u32::from(TCP) + 20);
    let tcp_sum = finish(sum(&frame[l4..l4 + 20], pseudo));
    frame[l4 + 16..l4 + 18].copy_from_slice(&tcp_sum.to_be_bytes());
    l4 + 20
}

fn sum(bytes: &[u8], mut total: u32) -> u32 {
    let (pairs, rest) = bytes.as_chunks::<2>();
    for pair in pairs {
        total += u32::from(u16::from_be_bytes(*pair));
    }
    if let [last] = rest {
        total += u32::from(*last) << 8;
    }
    total
}

fn finish(mut total: u32) -> u16 {
    while total >> 16 != 0 {
        total = (total & 0xFFFF) + (total >> 16);
    }
    !(total as u16)
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// Builds a TCP frame with correct checksums, as the stack would send it.
    pub(crate) fn tcp_frame(source: &str, destination: &str, flags: u8, payload: &[u8]) -> Vec<u8> {
        let (source, destination): (SocketAddrV4, SocketAddrV4) = (source.parse().unwrap(), destination.parse().unwrap());
        let mut frame = vec![0u8; ETH + 20 + 20 + payload.len()];
        frame[..6].copy_from_slice(&[0xAA; 6]); // to the gateway
        frame[6..12].copy_from_slice(&[0xBB; 6]); // from this PC
        frame[12..14].copy_from_slice(&ETHERTYPE_V4);
        frame[ETH] = 0x45;
        let total = (40 + payload.len()) as u16;
        frame[ETH + 2..ETH + 4].copy_from_slice(&total.to_be_bytes());
        frame[ETH + 8] = 128;
        frame[ETH + 9] = TCP;
        frame[ETH + 32] = 5 << 4;
        frame[ETH + 33] = flags;
        frame[ETH + 40..].copy_from_slice(payload);
        let packet = V4 { protocol: TCP, source, destination, flags, l4: ETH + 20, end: frame.len() };
        // Writing the addresses through `turn_around` also fills in both checksums.
        turn_around(&mut frame, &packet, source, destination);
        let (to, from) = frame.split_at_mut(6);
        to.swap_with_slice(&mut from[..6]);
        frame
    }

    fn checksums_hold(frame: &[u8]) -> bool {
        let p = parse_v4(frame).unwrap();
        let ip_ok = finish(sum(&frame[ETH..p.l4], 0)) == 0;
        let pseudo = sum(&frame[ETH + 12..ETH + 20], u32::from(TCP) + (p.end - p.l4) as u32);
        ip_ok && finish(sum(&frame[p.l4..p.end], pseudo)) == 0
    }

    #[test]
    fn a_packet_is_read_back_as_built() {
        let frame = tcp_frame("192.168.1.5:50000", "93.184.216.34:443", SYN, &[]);
        let p = parse_v4(&frame).unwrap();
        assert_eq!(p.source, "192.168.1.5:50000".parse().unwrap());
        assert_eq!(p.destination, "93.184.216.34:443".parse().unwrap());
        assert!(p.opens_connection() && checksums_hold(&frame));
    }

    #[test]
    fn turning_a_packet_around_keeps_it_valid() {
        for payload in [&b""[..], b"x", b"hello world"] {
            let mut frame = tcp_frame("192.168.1.5:50000", "93.184.216.34:443", ACK, payload);
            let p = parse_v4(&frame).unwrap();
            turn_around(&mut frame, &p, "93.184.216.34:50000".parse().unwrap(), "192.168.1.5:61000".parse().unwrap());
            let q = parse_v4(&frame).unwrap();
            assert_eq!(q.source, "93.184.216.34:50000".parse().unwrap());
            assert_eq!(q.destination, "192.168.1.5:61000".parse().unwrap());
            assert_eq!((&frame[..6], &frame[6..12]), (&[0xBB; 6][..], &[0xAA; 6][..]), "now addressed to this PC");
            assert!(checksums_hold(&frame), "payload {payload:?}");
            assert_eq!(&frame[ETH + 40..], payload);
        }
    }

    #[test]
    fn padding_after_the_packet_is_ignored() {
        let mut frame = tcp_frame("10.0.0.2:1234", "1.1.1.1:80", ACK, b"abc");
        frame.extend_from_slice(&[0xEE; 7]);
        let p = parse_v4(&frame).unwrap();
        turn_around(&mut frame, &p, "1.1.1.1:1234".parse().unwrap(), "10.0.0.2:9".parse().unwrap());
        assert!(checksums_hold(&frame));
    }

    #[test]
    fn other_traffic_is_left_alone() {
        let mut frame = tcp_frame("10.0.0.2:1234", "1.1.1.1:80", ACK, b"abc");
        frame[ETH + 6] = 0x20; // more fragments
        assert_eq!(parse_v4(&frame), None);
        frame[ETH + 6] = 0;
        frame[ETH + 9] = 1; // ICMP
        assert_eq!(parse_v4(&frame), None);
        assert_eq!(parse_v4(&frame[..20]), None);
    }

    #[test]
    fn an_ipv6_attempt_is_refused_like_a_closed_port() {
        let mut frame = vec![0u8; ETH + 40 + 32];
        frame[..6].copy_from_slice(&[0xAA; 6]);
        frame[6..12].copy_from_slice(&[0xBB; 6]);
        frame[12..14].copy_from_slice(&ETHERTYPE_V6);
        frame[ETH] = 0x60;
        frame[ETH + 4..ETH + 6].copy_from_slice(&32u16.to_be_bytes());
        frame[ETH + 6] = TCP;
        let (local, remote): (Ipv6Addr, Ipv6Addr) = ("2001:db8::5".parse().unwrap(), "2606:4700::1111".parse().unwrap());
        frame[ETH + 8..ETH + 24].copy_from_slice(&local.octets());
        frame[ETH + 24..ETH + 40].copy_from_slice(&remote.octets());
        let l4 = ETH + 40;
        frame[l4..l4 + 2].copy_from_slice(&50123u16.to_be_bytes());
        frame[l4 + 2..l4 + 4].copy_from_slice(&443u16.to_be_bytes());
        frame[l4 + 4..l4 + 8].copy_from_slice(&1000u32.to_be_bytes());
        frame[l4 + 12] = 8 << 4;
        frame[l4 + 13] = SYN;

        let seen = parse_v6(&frame).unwrap();
        assert!(seen.opens_connection() && seen.is_global());
        let length = refuse_v6(&mut frame);
        let reply = parse_v6(&frame[..length]).unwrap();
        assert_eq!((reply.source, reply.source_port), (remote, 443));
        assert_eq!((reply.destination, reply.destination_port), (local, 50123));
        assert_eq!(reply.flags, RST | ACK);
        assert_eq!(u32::from_be_bytes(frame[l4 + 8..l4 + 12].try_into().unwrap()), 1001, "acknowledges the attempt");
        let pseudo = sum(&frame[ETH + 8..ETH + 40], u32::from(TCP) + 20);
        assert_eq!(finish(sum(&frame[l4..l4 + 20], pseudo)), 0);
    }
}
