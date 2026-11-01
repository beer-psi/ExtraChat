use std::sync::Arc;

use anyhow::Result;
use tokio::sync::RwLock;

use crate::types::protocol::{PublicKeyRequest, PublicKeyResponse};
use crate::util::redacted::Redacted;
use crate::{State, WsStream};

pub async fn public_key(
    state: Arc<RwLock<State>>,
    conn: &mut WsStream,
    number: u32,
    req: PublicKeyRequest,
) -> Result<()> {
    let id = match state.read().await.ids.get(&(req.name.clone(), req.world)) {
        Some(id) => *id,
        None => {
            return crate::util::send(
                conn,
                number,
                PublicKeyResponse {
                    name: req.name,
                    world: req.world,
                    pk: None,
                },
            )
            .await
        }
    };

    let pk = match state.read().await.clients.get(&id) {
        Some(client) if client.read().await.allow_invites => Some(client.read().await.pk.clone()),
        _ => None,
    };
    crate::util::send(
        conn,
        number,
        PublicKeyResponse {
            name: req.name,
            world: req.world,
            pk: pk.map(Redacted::new),
        },
    )
    .await
}
