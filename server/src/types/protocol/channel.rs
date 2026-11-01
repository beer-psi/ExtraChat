use std::str::FromStr;

use anyhow::{Context, Result};
use futures_util::StreamExt;
use lodestone_scraper::lodestone_parser::ffxiv_types::World;
use serde::{Deserialize, Serialize};
use serde_repr::{Deserialize_repr, Serialize_repr};
use tokio::sync::RwLock;
use uuid::Uuid;

use crate::State;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Channel {
    pub id: Uuid,
    #[serde(with = "serde_bytes")]
    pub name: Vec<u8>,
    pub members: Vec<ChannelMember>,
}

impl Channel {
    pub async fn get(state: &RwLock<State>, id: Uuid) -> Result<Option<Self>> {
        let id_str = id.as_simple().to_string();
        let raw_channel = sqlx::query!(
            // language=sqlite
            "select * from channels where id = ?",
            id_str,
        )
        .fetch_optional(&state.read().await.db)
        .await
        .context("could not get channel info")?;

        let raw_channel = match raw_channel {
            Some(channel) => channel,
            None => return Ok(None),
        };

        let members: Vec<_> = futures_util::stream::iter(
            crate::util::get_raw_members(state, id)
                .await?
                .into_iter()
                .chain(
                    crate::util::get_raw_invited_members(state, id)
                        .await?
                        .into_iter(),
                ),
        )
        .then(|member| async move {
            ChannelMember {
                name: member.name,
                world: World::from_str(&member.world)
                    .map(crate::util::id_from_world)
                    .unwrap_or(0),
                rank: Rank::from_u8(member.rank as u8),
                online: state
                    .read()
                    .await
                    .clients
                    .contains_key(&(member.lodestone_id as u64)),
            }
        })
        .collect()
        .await;

        let id = Uuid::from_str(&raw_channel.id).context("invalid channel id")?;

        Ok(Some(Self {
            id,
            name: raw_channel.name,
            members,
        }))
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChannelMember {
    pub name: String,
    pub world: u16,
    pub rank: Rank,
    pub online: bool,
}

#[derive(Debug, Clone, Copy, Serialize_repr, Deserialize_repr, PartialEq, Eq, PartialOrd, Ord)]
#[serde(rename_all = "snake_case")]
#[repr(u8)]
pub enum Rank {
    Invited = 0,
    Member = 1,
    Moderator = 2,
    Admin = 3,
}

impl Rank {
    pub fn from_u8(u: u8) -> Self {
        match u {
            0 => Self::Invited,
            1 => Self::Member,
            2 => Self::Moderator,
            3 => Self::Admin,
            _ => Rank::Member,
        }
    }

    pub fn as_u8(self) -> u8 {
        match self {
            Self::Invited => 0,
            Self::Member => 1,
            Self::Moderator => 2,
            Self::Admin => 3,
        }
    }
}

impl From<u8> for Rank {
    fn from(u: u8) -> Self {
        Rank::from_u8(u)
    }
}

impl From<Rank> for u8 {
    fn from(r: Rank) -> Self {
        r.as_u8()
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SimpleChannel {
    pub id: Uuid,
    #[serde(with = "serde_bytes")]
    pub name: Vec<u8>,
    pub rank: Rank,
}

impl SimpleChannel {
    pub async fn get_all_for_user(state: &RwLock<State>, lodestone_id: u64) -> Result<Vec<Self>> {
        let lodestone_id_i = lodestone_id as i64;

        let all_channels = sqlx::query!(
            // language=sqlite
            "select channels.*, user_channels.rank from user_channels inner join channels on user_channels.channel_id = channels.id where user_channels.lodestone_id = ?",
            lodestone_id_i,
        )
            .fetch_all(&state.read().await.db)
            .await
            .context("could not get channels")?;

        let mut channels = Vec::with_capacity(all_channels.len());
        for channel in all_channels {
            let id = match Uuid::from_str(&channel.id) {
                Ok(u) => u,
                Err(_) => continue,
            };

            channels.push(Self {
                id,
                name: channel.name,
                rank: Rank::from_u8(channel.rank as u8),
            });
        }

        Ok(channels)
    }

    pub async fn get_invites_for_user(
        state: &RwLock<State>,
        lodestone_id: u64,
    ) -> Result<Vec<Self>> {
        let lodestone_id_i = lodestone_id as i64;

        let all_channels = sqlx::query!(
            // language=sqlite
            "select channels.* from channel_invites inner join channels on channel_invites.channel_id = channels.id where channel_invites.invited = ?",
            lodestone_id_i,
        )
            .fetch_all(&state.read().await.db)
            .await
            .context("could not get channels")?;

        let mut channels = Vec::with_capacity(all_channels.len());
        for channel in all_channels {
            let id = match Uuid::from_str(&channel.id) {
                Ok(u) => u,
                Err(_) => continue,
            };

            channels.push(Self {
                id,
                name: channel.name,
                rank: Rank::Member,
            });
        }

        Ok(channels)
    }
}
