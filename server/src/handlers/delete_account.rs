use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;

use crate::types::protocol::{DeleteAccountRequest, DeleteAccountResponse};
use crate::{ClientState, ErrorResponse, State, WsStream};

pub async fn delete_account(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    _req: DeleteAccountRequest,
) -> Result<()> {
    let id = match client_state.read().await.lodestone_id() {
        Some(id) => id,
        None => {
            return crate::util::send(
                conn,
                number,
                ErrorResponse::new(None, "no Lodestone ID? this is a bug"),
            )
            .await
        }
    };
    let lodestone_id = id as i64;

    let channels = sqlx::query!(
        // language=sqlite
        "select count(*) as count from user_channels where lodestone_id = ?",
        lodestone_id,
    )
    .fetch_one(&state.read().await.db)
    .await
    .context("could not get channel count")?;

    if channels.count > 0 {
        return crate::util::send(
            conn,
            number,
            ErrorResponse::new(None, "leave all linkshells first"),
        )
        .await;
    }

    sqlx::query!(
        // language=sqlite
        "delete from users where lodestone_id = ?",
        lodestone_id,
    )
    .execute(&state.read().await.db)
    .await
    .context("could not delete user")?;

    crate::util::send(conn, number, DeleteAccountResponse {}).await
}
