use std::sync::Arc;

use anyhow::Result;
use rand::seq::SliceRandom;
use tokio::sync::RwLock;
use uuid::Uuid;

use crate::types::protocol::{ResponseKind, SecretsRequest, SendSecretsResponse};
use crate::util::send;
use crate::{ClientState, ErrorResponse, ResponseContainer, State, WsStream};

#[derive(Clone)]
pub struct SecretsRequestInfo {
    pub lodestone_id: u64,
    pub channel_id: Uuid,
    pub number: u32,
}

pub async fn secrets(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: SecretsRequest,
) -> Result<()> {
    if client_state
        .read()
        .await
        .get_rank_invite(req.channel, &state)
        .await?
        .is_none()
    {
        return send(
            conn,
            number,
            ErrorResponse::new(req.channel, "not in that channel"),
        )
        .await;
    }

    let lodestone_id = match client_state.read().await.lodestone_id() {
        Some(lodestone_id) => lodestone_id,
        None => return Ok(()),
    };

    let all_members = crate::util::get_raw_members(&state, req.channel)
        .await?
        .into_iter()
        .chain(
            crate::util::get_raw_invited_members(&state, req.channel)
                .await?
                .into_iter(),
        );
    let mut members = Vec::new();
    for member in all_members {
        let id = member.lodestone_id as u64;
        if id != lodestone_id && state.read().await.clients.contains_key(&id) {
            members.push(member);
        }
    }

    if members.is_empty() {
        return send(
            conn,
            number,
            ErrorResponse::new(req.channel, "no other online members"),
        )
        .await;
    }

    // because I am lazy
    // ask 10% of the online members for their secrets
    // take the first one

    let mut amount = (members.len() as f32 / 10.0).round() as usize;
    if amount == 0 {
        amount = 1;
    }

    let members: Vec<_> = members
        .choose_multiple(&mut rand::thread_rng(), amount)
        .collect();
    if members.is_empty() {
        return send(
            conn,
            number,
            ErrorResponse::new(req.channel, "no online members found"),
        )
        .await;
    }

    let request_id = Uuid::new_v4();
    state.write().await.secrets_requests.insert(
        request_id,
        SecretsRequestInfo {
            lodestone_id,
            channel_id: req.channel,
            number,
        },
    );

    let pk = client_state.read().await.pk.clone();

    for member in members {
        let target_client = match state
            .read()
            .await
            .clients
            .get(&(member.lodestone_id as u64))
            .cloned()
        {
            Some(client) => client,
            None => continue,
        };

        target_client
            .read()
            .await
            .tx
            .send(ResponseContainer {
                number: 0,
                kind: ResponseKind::SendSecrets(SendSecretsResponse {
                    channel: req.channel,
                    request_id,
                    pk: pk.clone().into(),
                }),
            })
            .await?;
    }

    Ok(())
}
