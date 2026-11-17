//! Carries bytes between a program's connection and the connection made for it on a link.

use crate::router::Lease;
use std::io;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;

const BUFFER: usize = 64 * 1024;

/// Copies both ways until the far side finishes or either side fails.
///
/// When the program finishes sending, that is passed on and the reply keeps flowing, so a
/// request that half-closes still gets its whole response. When the reply ends, the relay ends.
pub async fn relay(mut app: TcpStream, mut remote: TcpStream, lease: &Lease) -> io::Result<()> {
    let (mut app_read, mut app_write) = app.split();
    let (mut remote_read, mut remote_write) = remote.split();

    let upload = async {
        let mut buffer = vec![0u8; BUFFER];
        loop {
            let n = app_read.read(&mut buffer).await?;
            if n == 0 {
                let _ = remote_write.shutdown().await;
                return io::Result::Ok(());
            }
            lease.throttle(n).await;
            remote_write.write_all(&buffer[..n]).await?;
            lease.uploaded(n);
        }
    };
    let download = async {
        let mut buffer = vec![0u8; BUFFER];
        loop {
            let n = remote_read.read(&mut buffer).await?;
            if n == 0 {
                let _ = app_write.shutdown().await;
                return io::Result::Ok(());
            }
            lease.throttle(n).await;
            app_write.write_all(&buffer[..n]).await?;
            lease.downloaded(n);
        }
    };

    tokio::pin!(upload, download);
    tokio::select! {
        result = &mut download => result,
        result = &mut upload => {
            result?;
            download.await
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{LinkInfo, LinkKind, Mode};
    use crate::router::{Route, Router, RouterOptions};
    use std::net::{Ipv4Addr, SocketAddr};
    use tokio::net::TcpListener;

    #[tokio::test]
    async fn a_half_closed_request_still_gets_its_whole_reply() {
        // The far server reads the request to its end, then answers.
        let server = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let SocketAddr::V4(server_address) = server.local_addr().unwrap() else { unreachable!() };
        tokio::spawn(async move {
            let (mut stream, _) = server.accept().await.unwrap();
            let mut request = Vec::new();
            stream.read_to_end(&mut request).await.unwrap();
            stream.write_all(&vec![7u8; request.len() * 3]).await.unwrap();
        });

        let router = std::sync::Arc::new(Router::new(RouterOptions::default(), None));
        let info = LinkInfo {
            id: "a".into(),
            name: "a".into(),
            description: String::new(),
            kind: LinkKind::Ethernet,
            if_index: 0,
            address: Ipv4Addr::LOCALHOST,
            gateway: Ipv4Addr::LOCALHOST,
        };
        router.set_routes(vec![Route { info, weight: 1, limit_mbps: 0 }], Mode::Balanced);

        // DualLink's side: accept the program's connection and carry it to the server.
        let front = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let front_address = front.local_addr().unwrap();
        let carrying = router.clone();
        let carried = tokio::spawn(async move {
            let (app, _) = front.accept().await.unwrap();
            let (remote, lease) = carrying.connect(server_address).await.unwrap();
            relay(app, remote, &lease).await.unwrap();
        });

        let mut program = TcpStream::connect(front_address).await.unwrap();
        program.write_all(&[1u8; 100_000]).await.unwrap();
        program.shutdown().await.unwrap();
        let mut reply = Vec::new();
        program.read_to_end(&mut reply).await.unwrap();
        assert_eq!(reply.len(), 300_000);
        carried.await.unwrap();

        let status = &router.statuses()[0];
        assert_eq!((status.uploaded, status.downloaded), (100_000, 300_000));
        assert_eq!(status.active_connections, 0);
    }
}
