//! The messages the app and the service exchange over a local named pipe.
//!
//! Each message is one line of JSON. The app sends a [`Request`] and reads one [`Response`].

use crate::model::{Config, Status};
use serde::{Deserialize, Serialize};

pub const PIPE_NAME: &str = r"\\.\pipe\DualLink.Control.v5";
pub const PROTOCOL: u32 = 5;
/// No message is longer than this; a longer line ends the connection.
pub const MAX_MESSAGE_BYTES: usize = 256 * 1024;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "do", rename_all = "kebab-case")]
pub enum Request {
    /// Report what is happening. Changes nothing.
    Status { protocol: u32 },
    /// Replace the configuration. The service saves it and applies it at once.
    Apply { protocol: u32, config: Config },
}

impl Request {
    pub fn protocol(&self) -> u32 {
        match self {
            Request::Status { protocol } | Request::Apply { protocol, .. } => *protocol,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "result", rename_all = "kebab-case")]
pub enum Response {
    Ok { status: Box<Status> },
    Error { message: String },
}

pub fn encode<T: Serialize>(message: &T) -> Vec<u8> {
    let mut line = serde_json::to_vec(message).expect("messages always serialise");
    line.push(b'\n');
    line
}

pub fn decode<'a, T: Deserialize<'a>>(line: &'a [u8]) -> Result<T, String> {
    if line.len() > MAX_MESSAGE_BYTES {
        return Err("The message is too long.".into());
    }
    serde_json::from_slice(line).map_err(|e| format!("The message could not be read: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn requests_survive_the_pipe() {
        let request = Request::Apply { protocol: PROTOCOL, config: Config { enabled: true, ..Config::default() } };
        let line = encode(&request);
        assert_eq!(line.last(), Some(&b'\n'));
        assert_eq!(decode::<Request>(&line[..line.len() - 1]).unwrap(), request);
    }

    #[test]
    fn a_config_from_an_older_build_fills_in_new_fields() {
        let config: Config = serde_json::from_str(r#"{"enabled":true}"#).unwrap();
        assert!(config.enabled && config.steer_quic && config.links.is_empty());
    }

    #[test]
    fn hostile_configs_are_rejected() {
        let mut config = Config::default();
        config.apps.push("cmd.bat".into());
        assert!(config.validate().is_err());
        config.apps = vec!["steam.exe".into()];
        config.links.push(crate::model::LinkChoice { weight: 0, ..crate::model::LinkChoice::new("{x}") });
        assert!(config.validate().is_err());
        config.links[0].weight = 3;
        assert!(config.validate().is_ok());
    }
}
