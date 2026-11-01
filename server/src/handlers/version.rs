use anyhow::Result;

use crate::{
    types::protocol::{VersionRequest, VersionResponse},
    util::send,
    ErrorResponse, WsStream,
};

const VERSION: u32 = 1;

pub async fn version(conn: &mut WsStream, number: u32, req: VersionRequest) -> Result<bool> {
    if req.version != VERSION {
        send(
            conn,
            number,
            ErrorResponse::new(None, "unsupported version"),
        )
        .await?;
        return Ok(false);
    }

    send(conn, number, VersionResponse { version: VERSION }).await?;

    Ok(true)
}
