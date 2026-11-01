use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;

use crate::types::protocol::channel::{Channel, Rank};
use crate::types::protocol::{
    InviteRequest, InviteResponse, InvitedResponse, MemberChangeKind, MemberChangeResponse,
    ResponseKind,
};
use crate::{ClientState, ErrorResponse, ResponseContainer, State, WsStream};

pub async fn invite(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: InviteRequest,
) -> Result<()> {
    let user = match &client_state.read().await.user {
        Some(u) => u.clone(),
        None => return Ok(()),
    };
    let lodestone_id = user.lodestone_id as i64;

    let rank = match client_state
        .read()
        .await
        .get_rank(req.channel, &state)
        .await?
    {
        Some(r) => r,
        None => {
            return crate::util::send(
                conn,
                number,
                ErrorResponse::new(req.channel, "not in channel"),
            )
            .await
        }
    };

    if rank < Rank::Moderator {
        return crate::util::send(
            conn,
            number,
            ErrorResponse::new(req.channel, "not enough permissions to invite"),
        )
        .await;
    }

    const NOT_ONLINE: &str = "user not online";
    let target_id = match state.read().await.ids.get(&(req.name.clone(), req.world)) {
        Some(id) => *id,
        None => {
            return crate::util::send(conn, number, ErrorResponse::new(req.channel, NOT_ONLINE))
                .await
        }
    };
    let target_id_i = target_id as i64;

    if let Some(client) = state.read().await.clients.get(&target_id) {
        if !client.read().await.allow_invites {
            return crate::util::send(conn, number, ErrorResponse::new(req.channel, NOT_ONLINE))
                .await;
        }
    }

    if target_id_i == lodestone_id {
        return crate::util::send(
            conn,
            number,
            ErrorResponse::new(req.channel, "cannot invite self"),
        )
        .await;
    }

    let channel_id = req.channel.as_simple().to_string();
    // check for existing membership
    let membership = sqlx::query!(
        // language=sqlite
        "select count(*) as count from user_channels where channel_id = ? and lodestone_id = ?",
        channel_id,
        target_id_i,
    )
    .fetch_one(&state.read().await.db)
    .await
    .context("could not query database for membership")?;

    if membership.count > 0 {
        return crate::util::send(
            conn,
            number,
            ErrorResponse::new(req.channel, "already in channel"),
        )
        .await;
    }

    // check for existing invite
    let invite = sqlx::query!(
        // language=sqlite
        "select count(*) as count from channel_invites where channel_id = ? and invited = ?",
        channel_id,
        target_id_i,
    )
    .fetch_one(&state.read().await.db)
    .await
    .context("could not query database for invite")?;

    if invite.count > 0 {
        return crate::util::send(
            conn,
            number,
            ErrorResponse::new(req.channel, "already invited"),
        )
        .await;
    }

    crate::util::send_to_all(
        &state,
        req.channel,
        0,
        MemberChangeResponse {
            channel: req.channel,
            name: req.name.clone(),
            world: req.world,
            kind: MemberChangeKind::Invite {
                inviter: user.name,
                inviter_world: crate::util::id_from_world(user.world),
            },
        },
    )
    .await?;

    sqlx::query!(
        // language=sqlite
        "insert into channel_invites (channel_id, invited, inviter) values (?, ?, ?)",
        channel_id,
        target_id_i,
        lodestone_id,
    )
    .execute(&state.read().await.db)
    .await
    .context("could not add invite")?;

    // inviter's info
    let pk = client_state.read().await.pk.clone();
    let (name, world) = match &client_state.read().await.user {
        Some(c) => (c.name.clone(), c.world),
        None => return Ok(()),
    };

    // send invite to invitee
    match state.read().await.clients.get(&target_id) {
        Some(c) => {
            let channel = Channel::get(&state, req.channel)
                .await
                .context("could not get channel")?
                .context("no such channel")?;
            c.read()
                .await
                .tx
                .send(ResponseContainer {
                    number: 0,
                    kind: ResponseKind::Invited(InvitedResponse {
                        channel,
                        name,
                        world: crate::util::id_from_world(world),
                        pk: pk.into(),
                        encrypted_secret: req.encrypted_secret,
                    }),
                })
                .await?;
        }
        None => {
            return crate::util::send(conn, number, ErrorResponse::new(req.channel, NOT_ONLINE))
                .await
        }
    }

    crate::util::send(
        conn,
        number,
        InviteResponse {
            channel: req.channel,
            name: req.name,
            world: req.world,
        },
    )
    .await
}
