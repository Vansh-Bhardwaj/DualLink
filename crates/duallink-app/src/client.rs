//! Talks to the DualLink service over its local pipe.

use duallink_core::ipc::{self, PIPE_NAME, PROTOCOL, Request, Response};
use duallink_core::{Config, Status};
use std::fs::{File, OpenOptions};
use std::io::{BufRead, BufReader, Write};

pub const NOT_RUNNING: &str = "DualLink's background service isn't running.";

/// One connection to the service, reopened by itself if it drops.
#[derive(Default)]
pub struct Client {
    pipe: Option<(File, BufReader<File>)>,
}

impl Client {
    pub fn status(&mut self) -> Result<Status, String> {
        self.call(&Request::Status { protocol: PROTOCOL })
    }

    pub fn apply(&mut self, config: Config) -> Result<Status, String> {
        self.call(&Request::Apply { protocol: PROTOCOL, config })
    }

    fn call(&mut self, request: &Request) -> Result<Status, String> {
        let result = self.exchange(request);
        if result.is_err() {
            self.pipe = None;
        }
        match result? {
            Response::Ok { status } => Ok(*status),
            Response::Error { message } => Err(message),
        }
    }

    fn exchange(&mut self, request: &Request) -> Result<Response, String> {
        if self.pipe.is_none() {
            let pipe = OpenOptions::new().read(true).write(true).open(PIPE_NAME).map_err(|_| NOT_RUNNING)?;
            let reader = BufReader::new(pipe.try_clone().map_err(|_| NOT_RUNNING)?);
            self.pipe = Some((pipe, reader));
        }
        let (writer, reader) = self.pipe.as_mut().expect("just opened");
        writer.write_all(&ipc::encode(request)).map_err(|_| NOT_RUNNING)?;
        let mut line = Vec::new();
        let read = reader.read_until(b'\n', &mut line).map_err(|_| NOT_RUNNING)?;
        if read == 0 || line.pop() != Some(b'\n') {
            return Err(NOT_RUNNING.into());
        }
        ipc::decode(&line)
    }
}
