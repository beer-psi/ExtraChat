use anyhow::Result;

use crate::types::protocol::PingResponse;
use crate::WsStream;

pub async fn ping(conn: &mut WsStream, number: u32) -> Result<()> {
    crate::util::send(conn, number, PingResponse {}).await
}
