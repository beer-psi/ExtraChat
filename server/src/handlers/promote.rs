use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;

use crate::types::protocol::channel::Rank;
use crate::types::protocol::MemberChangeKind;
use crate::types::protocol::{MemberChangeResponse, PromoteRequest, PromoteResponse};
use crate::util::send;
use crate::{ClientState, ErrorResponse, State, WsStream};

pub async fn promote(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: PromoteRequest,
) -> Result<()> {
    let user = match &client_state.read().await.user {
        Some(user) => user.clone(),
        None => return Ok(()),
    };
    let lodestone_id = user.lodestone_id;
    let lodestone_id_i = lodestone_id as i64;

    let rank = match client_state
        .read()
        .await
        .get_rank(req.channel, &state)
        .await?
    {
        Some(rank) if rank == Rank::Admin => rank,
        _ => {
            return send(
                conn,
                number,
                ErrorResponse::new(req.channel, "not in channel/not enough permissions"),
            )
            .await
        }
    };

    if req.rank == Rank::Invited {
        return send(
            conn,
            number,
            ErrorResponse::new(req.channel, "cannot change rank to invited"),
        )
        .await;
    }

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

    if target_id == lodestone_id {
        return send(
            conn,
            number,
            ErrorResponse::new(req.channel, "cannot change own rank"),
        )
        .await;
    }

    let channel_id_str = req.channel.as_simple().to_string();
    let target_rank = sqlx::query!(
        // language=sqlite
        "select rank from user_channels where channel_id = ? and lodestone_id = ?",
        channel_id_str,
        target_id_i,
    )
    .fetch_optional(&state.read().await.db)
    .await
    .context("could not query database for rank")?;

    match target_rank {
        Some(target) if target.rank >= rank.as_u8() as i64 => {
            return send(
                conn,
                number,
                ErrorResponse::new(
                    req.channel,
                    "cannot change rank of someone of equal or higher rank",
                ),
            )
            .await;
        }
        None => {
            return send(
                conn,
                number,
                ErrorResponse::new(req.channel, "user not in channel"),
            )
            .await
        }
        _ => {}
    }

    let swap = req.rank == Rank::Admin;

    // change the rank
    let new_rank = req.rank.as_u8() as i64;
    sqlx::query!(
        // language=sqlite
        "update user_channels set rank = ? where channel_id = ? and lodestone_id = ?",
        new_rank,
        channel_id_str,
        target_id_i,
    )
    .execute(&state.read().await.db)
    .await
    .context("could not update user rank")?;

    if swap {
        // lower own rank
        let new_rank = Rank::Moderator.as_u8() as i64;
        sqlx::query!(
            // language=sqlite
            "update user_channels set rank = ? where channel_id = ? and lodestone_id = ?",
            new_rank,
            channel_id_str,
            lodestone_id_i,
        )
        .execute(&state.read().await.db)
        .await
        .context("could not update user rank")?;

        crate::util::send_to_all(
            &state,
            req.channel,
            0,
            MemberChangeResponse {
                channel: req.channel,
                name: user.name,
                world: crate::util::id_from_world(user.world),
                kind: MemberChangeKind::Promote {
                    rank: Rank::Moderator,
                },
            },
        )
        .await?;
    }

    crate::util::send_to_all(
        &state,
        req.channel,
        0,
        MemberChangeResponse {
            channel: req.channel,
            name: req.name.clone(),
            world: req.world,
            kind: MemberChangeKind::Promote { rank: req.rank },
        },
    )
    .await?;

    send(
        conn,
        number,
        PromoteResponse {
            channel: req.channel,
            name: req.name,
            world: req.world,
            rank: req.rank,
        },
    )
    .await
}
