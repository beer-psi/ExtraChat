use std::sync::Arc;

use anyhow::{Context, Result};
use tokio::sync::RwLock;

use crate::types::protocol::{ResponseKind, SecretsResponse, SendSecretsRequest};
use crate::util::send;
use crate::{ClientState, ErrorResponse, ResponseContainer, State, WsStream};

pub async fn send_secrets(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: SendSecretsRequest,
) -> Result<()> {
    let encrypted = match req.encrypted_shared_secret {
        Some(encrypted) if !encrypted.is_empty() => encrypted,
        _ => return Ok(()),
    };

    let info = match state
        .read()
        .await
        .secrets_requests
        .get(&req.request_id)
        .cloned()
    {
        Some(info) => info,
        None => return Ok(()),
    };

    if client_state
        .read()
        .await
        .get_rank_invite(info.channel_id, &state)
        .await?
        .is_none()
    {
        return send(
            conn,
            number,
            ErrorResponse::new(info.channel_id, "not in that channel"),
        )
        .await;
    }

    state.write().await.secrets_requests.remove(&req.request_id);

    let requester = match state.read().await.clients.get(&info.lodestone_id).cloned() {
        Some(requester) => requester,
        None => return Ok(()),
    };

    requester
        .read()
        .await
        .tx
        .send(ResponseContainer {
            number: info.number,
            kind: ResponseKind::Secrets(SecretsResponse {
                channel: info.channel_id,
                pk: client_state.read().await.pk.clone().into(),
                encrypted_shared_secret: encrypted,
            }),
        })
        .await
        .context("failed to send secrets response")?;

    Ok(())
}
