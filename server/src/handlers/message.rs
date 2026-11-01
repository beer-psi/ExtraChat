use std::sync::atomic::Ordering;
use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;

use crate::types::protocol::ResponseKind;
use crate::util::send;
use crate::{
    util, ClientState, ErrorResponse, MessageRequest, MessageResponse, ResponseContainer, State,
    WsStream,
};

pub async fn message(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: MessageRequest,
) -> Result<()> {
    let (lodestone_id, sender, world) = match &client_state.read().await.user {
        Some(u) => (u.lodestone_id, u.name.clone(), u.world),
        None => return Ok(()),
    };

    let id = req.channel.as_simple().to_string();
    let members = sqlx::query!(
        // language=sqlite
        "select lodestone_id from user_channels where channel_id = ?",
        id,
    )
    .fetch_all(&state.read().await.db)
    .await
    .context("could not query database for members")?;

    let in_channel = members
        .iter()
        .any(|m| m.lodestone_id as u64 == lodestone_id);
    if !in_channel {
        return send(
            conn,
            number,
            ErrorResponse::new(req.channel, "not in channel"),
        )
        .await;
    }

    state
        .read()
        .await
        .messages_sent
        .fetch_add(1, Ordering::SeqCst);

    let resp = ResponseContainer {
        number: 0,
        kind: ResponseKind::Message(MessageResponse {
            channel: req.channel,
            sender,
            world: util::id_from_world(world),
            message: req.message,
        }),
    };

    for member in members {
        let client = match state
            .read()
            .await
            .clients
            .get(&(member.lodestone_id as u64))
            .cloned()
        {
            Some(c) => c,
            None => continue,
        };

        client.read().await.tx.send(resp.clone()).await.ok();
    }

    Ok(())
}
