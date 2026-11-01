use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;
use uuid::Uuid;

use crate::types::protocol::channel::{Channel, Rank};
use crate::types::protocol::{CreateRequest, CreateResponse};
use crate::{ClientState, ErrorResponse, State, WsStream};

pub async fn create(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: CreateRequest,
) -> Result<()> {
    let id = Uuid::new_v4();
    let id_str = id.as_simple().to_string();

    sqlx::query!(
        // language=sqlite
        "insert into channels (id, name) values (?, ?)",
        id_str,
        req.name,
    )
    .execute(&state.read().await.db)
    .await
    .context("could not create channel")?;

    let lodestone_id = client_state
        .read()
        .await
        .user
        .as_ref()
        .map(|u| u.lodestone_id as i64)
        .unwrap_or(0);
    if lodestone_id == 0 {
        // should not be possible
        return Ok(());
    }

    let rank = Rank::Admin.as_u8();
    sqlx::query!(
        // language=sqlite
        "insert into user_channels (lodestone_id, channel_id, rank) values (?, ?, ?)",
        lodestone_id,
        id_str,
        rank,
    )
    .execute(&state.read().await.db)
    .await
    .context("could not add user to channel")?;

    let channel = match Channel::get(&state, id).await? {
        Some(c) => c,
        None => {
            return crate::util::send(
                conn,
                number,
                ErrorResponse::new(None, "could not get newly-created channel"),
            )
            .await;
        }
    };

    crate::util::send(conn, number, CreateResponse { channel }).await?;

    Ok(())
}
