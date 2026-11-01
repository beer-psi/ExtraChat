use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;

use crate::types::protocol::{UpdateKind, UpdateRequest, UpdateResponse, UpdatedResponse};
use crate::util::send;
use crate::{ClientState, ErrorResponse, Rank, State, WsStream};

pub async fn update(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: UpdateRequest,
) -> Result<()> {
    match client_state
        .read()
        .await
        .get_rank(req.channel, &state)
        .await?
    {
        Some(Rank::Admin) => {}
        _ => {
            return send(
                conn,
                number,
                ErrorResponse::new(req.channel, "not in that channel"),
            )
            .await
        }
    }

    let channel_id_str = req.channel.as_simple().to_string();
    match &req.kind {
        UpdateKind::Name(name) => {
            sqlx::query!(
                // language=sqlite
                "update channels set name = ? where id = ?",
                name,
                channel_id_str,
            )
            .execute(&state.read().await.db)
            .await
            .context("could not update name")?;
        }
    }

    crate::util::send_to_all(
        &state,
        req.channel,
        0,
        UpdatedResponse {
            channel: req.channel,
            kind: req.kind,
        },
    )
    .await?;

    send(
        conn,
        number,
        UpdateResponse {
            channel: req.channel,
        },
    )
    .await
}
