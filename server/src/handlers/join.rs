use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;

use crate::types::protocol::channel::{Channel, Rank};
use crate::types::protocol::{JoinRequest, JoinResponse, MemberChangeKind, MemberChangeResponse};
use crate::util::send;
use crate::{ClientState, ErrorResponse, State, WsStream};

pub async fn join(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: JoinRequest,
) -> Result<()> {
    let user = match &client_state.read().await.user {
        Some(user) => user.clone(),
        None => return Ok(()),
    };
    let lodestone_id = user.lodestone_id as i64;

    let channel_id = req.channel.as_simple().to_string();
    let invite = sqlx::query!(
        // language=sqlite
        "delete from channel_invites where channel_id = ? and invited = ? returning *",
        channel_id,
        lodestone_id,
    )
    .fetch_optional(&state.read().await.db)
    .await
    .context("failed to fetch invite")?;

    if invite.is_none() {
        return send(
            conn,
            number,
            ErrorResponse::new(req.channel, "you were not invited to that channel"),
        )
        .await;
    }

    crate::util::send_to_all(
        &state,
        req.channel,
        0,
        MemberChangeResponse {
            channel: req.channel,
            name: user.name,
            world: crate::util::id_from_world(user.world),
            kind: MemberChangeKind::Join,
        },
    )
    .await?;

    let rank = Rank::Member.as_u8();
    sqlx::query!(
        // language=sqlite
        "insert into user_channels (lodestone_id, channel_id, rank) values (?, ?, ?)",
        lodestone_id,
        channel_id,
        rank,
    )
    .execute(&state.read().await.db)
    .await
    .context("failed to add user to channel")?;

    let channel = Channel::get(&state, req.channel)
        .await
        .context("failed to get channel")?
        .context("no such channel")?;

    send(conn, number, JoinResponse { channel }).await
}
