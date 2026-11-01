use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;

use crate::types::protocol::{MemberChangeKind, MemberChangeResponse};
use crate::{
    types::protocol::{LeaveRequest, LeaveResponse},
    util::send,
    ClientState, ErrorResponse, Rank, State, WsStream,
};

pub async fn leave(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: LeaveRequest,
) -> Result<()> {
    let user = match &client_state.read().await.user {
        Some(user) => user.clone(),
        None => return Ok(()),
    };
    let lodestone_id = user.lodestone_id as i64;

    let channel_id = req.channel.as_simple().to_string();
    let rank = match client_state
        .read()
        .await
        .get_rank(req.channel, &state)
        .await?
    {
        Some(rank) => rank,
        None => {
            let is_invited = sqlx::query!(
                // language=sqlite
                "select count(*) as count from channel_invites where channel_id = ? and invited = ?",
                channel_id,
                lodestone_id,
            )
                .fetch_one(&state.read().await.db)
                .await
                .context("could not get channel members")?
                .count > 0;

            if is_invited {
                Rank::Invited
            } else {
                return send(
                    conn,
                    number,
                    ErrorResponse::new(req.channel, "not in that channel"),
                )
                .await;
            }
        }
    };

    let is_decline = rank == Rank::Invited;

    let users: i32 = sqlx::query!(
        // language=sqlite
        "select count(*) as count from user_channels where channel_id = ?",
        channel_id,
    )
    .fetch_one(&state.read().await.db)
    .await
    .context("failed to get user count")?
    .count;

    // if the leaving user is an admin and there's more than one user,
    // the admin must promote someone before they can leave
    if users > 1 && rank == Rank::Admin {
        return send(
            conn,
            number,
            LeaveResponse::error(
                req.channel,
                "you must promote someone to admin before leaving",
            ),
        )
        .await;
    }

    // if there's only one user and this isn't an invite decline, we can
    // handle all the logic just with cascade deletes
    if users == 1 && !is_decline {
        sqlx::query!(
            // language=sqlite
            "delete from channels where id = ?",
            channel_id,
        )
        .execute(&state.read().await.db)
        .await
        .context("failed to delete channel")?;

        return send(conn, number, LeaveResponse::success(req.channel)).await;
    }

    let kind = if is_decline {
        sqlx::query!(
            // language=sqlite
            "delete from channel_invites where channel_id = ? and invited = ?",
            channel_id,
            lodestone_id,
        )
        .execute(&state.read().await.db)
        .await
        .context("failed to remove invite")?;

        MemberChangeKind::InviteDecline
    } else {
        sqlx::query!(
            // language=sqlite
            "delete from user_channels where lodestone_id = ? and channel_id = ?",
            lodestone_id,
            channel_id,
        )
        .execute(&state.read().await.db)
        .await
        .context("failed to remove user from channel")?;

        MemberChangeKind::Leave
    };

    crate::util::send_to_all(
        &state,
        req.channel,
        0,
        MemberChangeResponse {
            channel: req.channel,
            name: user.name,
            world: crate::util::id_from_world(user.world),
            kind,
        },
    )
    .await?;

    send(conn, number, LeaveResponse::success(req.channel)).await
}
