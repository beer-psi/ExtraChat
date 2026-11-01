use std::str::FromStr;
use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;
use uuid::Uuid;

use crate::util::RawMember;
use crate::{
    types::protocol::{
        channel::{Channel, ChannelMember, Rank, SimpleChannel},
        ListRequest, ListResponse,
    },
    util::send,
    ClientState, State, World, WsStream,
};

pub async fn list(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: ListRequest,
) -> Result<()> {
    let lodestone_id = match &client_state.read().await.user {
        Some(u) => u.lodestone_id,
        None => return Ok(()),
    };

    let resp = match req {
        ListRequest::All => ListResponse::All {
            channels: get_full_channels(lodestone_id, &state).await?,
            invites: get_full_invites(lodestone_id, &state).await?,
        },
        ListRequest::Channels => ListResponse::Channels(get_channels(lodestone_id, &state).await?),
        ListRequest::Members(id) => ListResponse::Members {
            id,
            members: get_members(lodestone_id, &state, id).await?,
        },
        ListRequest::Invites => ListResponse::Invites(get_invites(lodestone_id, &state).await?),
    };

    send(conn, number, resp).await
}

async fn ids_to_channels(ids: &[&str], state: &RwLock<State>) -> Vec<Channel> {
    let mut channels = Vec::with_capacity(ids.len());
    for id in ids {
        let id = match Uuid::from_str(id) {
            Ok(id) => id,
            Err(_) => continue,
        };

        let channel = match Channel::get(state, id).await {
            Ok(Some(channel)) => channel,
            _ => continue,
        };

        channels.push(channel);
    }

    channels
}

async fn get_full_channels(lodestone_id: u64, state: &RwLock<State>) -> Result<Vec<Channel>> {
    let lodestone_id_i = lodestone_id as i64;
    let channel_ids = sqlx::query!(
        // language=sqlite
        "select channel_id from user_channels where lodestone_id = ?",
        lodestone_id_i,
    )
    .fetch_all(&state.read().await.db)
    .await
    .context("failed to fetch channel ids")?;

    let ids: Vec<&str> = channel_ids
        .iter()
        .map(|id| id.channel_id.as_str())
        .collect();
    Ok(ids_to_channels(&ids, state).await)
}

async fn get_full_invites(lodestone_id: u64, state: &RwLock<State>) -> Result<Vec<Channel>> {
    let lodestone_id_i = lodestone_id as i64;
    let channel_ids = sqlx::query!(
        // language=sqlite
        "select channel_id from channel_invites where invited = ?",
        lodestone_id_i,
    )
    .fetch_all(&state.read().await.db)
    .await
    .context("failed to fetch channel ids")?;

    let ids: Vec<&str> = channel_ids
        .iter()
        .map(|id| id.channel_id.as_str())
        .collect();
    Ok(ids_to_channels(&ids, state).await)
}

async fn get_channels(lodestone_id: u64, state: &RwLock<State>) -> Result<Vec<SimpleChannel>> {
    SimpleChannel::get_all_for_user(state, lodestone_id)
        .await
        .context("could not get channels for user")
}

async fn get_members(
    lodestone_id: u64,
    state: &RwLock<State>,
    channel_id: Uuid,
) -> Result<Vec<ChannelMember>> {
    let lodestone_id_i = lodestone_id as i64;

    let channel_id_str = channel_id.as_simple().to_string();
    let users: Vec<RawMember> = sqlx::query_as!(
        RawMember,
        // language=sqlite
        "select users.lodestone_id, users.name, users.world, user_channels.rank from user_channels inner join users on users.lodestone_id = user_channels.lodestone_id where user_channels.channel_id = ?",
        channel_id_str,
    )
        .fetch_all(&state.read().await.db)
        .await
        .context("failed to get members")?;

    let invited: Vec<RawMember> = sqlx::query_as!(
        RawMember,
        // language=sqlite
        "select users.lodestone_id, users.name, users.world, cast(0 as int) as rank from channel_invites inner join users on users.lodestone_id = channel_invites.invited where channel_invites.channel_id = ?",
        channel_id_str,
    )
        .fetch_all(&state.read().await.db)
        .await
        .context("failed to get invited members")?;

    let mut found = false;
    let mut members = Vec::with_capacity(users.len());
    for user in users.into_iter().chain(invited.into_iter()) {
        if user.lodestone_id == lodestone_id_i {
            found = true;
        }

        let world = match World::from_str(&user.world) {
            Ok(world) => world,
            Err(_) => continue,
        };

        let online = state
            .read()
            .await
            .clients
            .contains_key(&(user.lodestone_id as u64));
        members.push(ChannelMember {
            name: user.name,
            world: crate::util::id_from_world(world),
            rank: Rank::from_u8(user.rank as u8),
            online,
        });
    }

    if !found {
        anyhow::bail!("user not in channel");
    }

    Ok(members)
}

async fn get_invites(lodestone_id: u64, state: &RwLock<State>) -> Result<Vec<SimpleChannel>> {
    SimpleChannel::get_invites_for_user(state, lodestone_id)
        .await
        .context("could not get channels for user")
}
