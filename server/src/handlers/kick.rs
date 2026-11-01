use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;

use crate::types::protocol::channel::Rank;
use crate::types::protocol::{KickRequest, KickResponse, MemberChangeKind, MemberChangeResponse};
use crate::util::send;
use crate::{ClientState, ErrorResponse, State, WsStream};

pub async fn kick(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: KickRequest,
) -> Result<()> {
    let user = match &client_state.read().await.user {
        Some(user) => user.clone(),
        None => return Ok(()),
    };

    let rank = match client_state
        .read()
        .await
        .get_rank(req.channel, &state)
        .await?
    {
        Some(rank) if rank >= Rank::Moderator => rank,
        _ => {
            return send(
                conn,
                number,
                ErrorResponse::new(req.channel, "not in channel/not enough permissions"),
            )
            .await
        }
    };

    let target_id = match state
        .read()
        .await
        .get_id(&state, &req.name, req.world)
        .await
    {
        Some(id) => id,
        None => {
            return send(
                conn,
                number,
                ErrorResponse::new(req.channel, "user not found"),
            )
            .await
        }
    };
    let target_id_i = target_id as i64;

    let channel_id_str = req.channel.as_simple().to_string();
    let target_rank: Option<Rank> = sqlx::query!(
        // language=sqlite
        "select rank from user_channels where channel_id = ? and lodestone_id = ?",
        channel_id_str,
        target_id_i,
    )
    .fetch_optional(&state.read().await.db)
    .await
    .context("could not query database for rank")?
    .map(|row| (row.rank as u8).into());

    match target_rank {
        Some(target) if target >= rank => {
            return send(
                conn,
                number,
                ErrorResponse::new(req.channel, "cannot kick someone of equal or higher rank"),
            )
            .await;
        }
        None if !crate::util::is_invited(&state, req.channel, target_id).await? => {
            return send(
                conn,
                number,
                ErrorResponse::new(req.channel, "user not in channel"),
            )
            .await;
        }
        _ => {}
    }

    let is_invited = target_rank.is_none();

    let kind = if is_invited {
        MemberChangeKind::InviteCancel {
            canceler: user.name,
            canceler_world: crate::util::id_from_world(user.world),
        }
    } else {
        MemberChangeKind::Kick {
            kicker: user.name,
            kicker_world: crate::util::id_from_world(user.world),
        }
    };

    crate::util::send_to_all(
        &state,
        req.channel,
        0,
        MemberChangeResponse {
            channel: req.channel,
            name: req.name.clone(),
            world: req.world,
            kind,
        },
    )
    .await?;

    if is_invited {
        sqlx::query!(
            // language=sqlite
            "delete from channel_invites where channel_id = ? and invited = ?",
            channel_id_str,
            target_id_i,
        )
        .execute(&state.read().await.db)
        .await
        .context("could not delete invite")?;
    } else {
        sqlx::query!(
            // language=sqlite
            "delete from user_channels where channel_id = ? and lodestone_id = ?",
            channel_id_str,
            target_id_i,
        )
        .execute(&state.read().await.db)
        .await
        .context("could not kick user")?;
    }

    send(
        conn,
        number,
        KickResponse {
            channel: req.channel,
            name: req.name.clone(),
            world: req.world,
        },
    )
    .await
}
