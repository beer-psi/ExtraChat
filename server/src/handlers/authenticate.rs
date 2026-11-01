use std::str::FromStr;
use std::sync::Arc;

use anyhow::Context;
use chrono::{Duration, Utc};
use log::trace;
use tokio::sync::RwLock;

use crate::{
    util, AuthenticateRequest, AuthenticateResponse, ClientState, State, User, World, WsStream,
};

pub async fn authenticate(
    state: Arc<RwLock<State>>,
    client_state: Arc<RwLock<ClientState>>,
    conn: &mut WsStream,
    number: u32,
    req: AuthenticateRequest,
) -> anyhow::Result<()> {
    if client_state.read().await.user.is_some() {
        return util::send(
            conn,
            number,
            AuthenticateResponse::error("already logged in"),
        )
        .await;
    }

    let key = prefixed_api_key::parse(&*req.key).context("could not parse key")?;
    let hash = util::hash_key(&key);
    let user = sqlx::query!(
        // language=sqlite
        "select * from users where key_short = ? and key_hash = ?",
        key.short_token,
        hash,
    )
    .fetch_optional(&state.read().await.db)
    .await
    .context("could not query database for user")?;
    let user = match user {
        Some(u) => u,
        None => return util::send(conn, number, AuthenticateResponse::error("invalid key")).await,
    };

    let world = World::from_str(&user.world).map_err(|_| anyhow::anyhow!("invalid world in db"))?;

    if let Some(old_client_state) = state.read().await.clients.get(&(user.lodestone_id as u64)) {
        let mut lock = old_client_state.write().await;
        // this prevents the old client thread from removing info from the global state
        lock.user = None;
        lock.shutdown_tx.send(()).await.ok();
    }

    trace!("  [authenticate] before user write");
    let mut c_state = client_state.write().await;
    c_state.user = Some(User {
        lodestone_id: user.lodestone_id as u64,
        name: user.name.clone(),
        world,
        hash,
    });

    c_state.pk = req.pk.into_inner();
    c_state.allow_invites = req.allow_invites;

    // release lock asap
    drop(c_state);
    trace!("  [authenticate] after user write");

    trace!("  [authenticate] before state write 1");
    state
        .write()
        .await
        .clients
        .insert(user.lodestone_id as u64, Arc::clone(&client_state));
    trace!("  [authenticate] before state write 2");
    state.write().await.ids.insert(
        (user.name, util::id_from_world(world)),
        user.lodestone_id as u64,
    );
    trace!("  [authenticate] after state writes");

    if Utc::now()
        .naive_utc()
        .signed_duration_since(user.last_updated)
        >= Duration::hours(2)
    {
        state.read().await.updater_tx.send(user.lodestone_id).ok();
    }

    util::send(conn, number, AuthenticateResponse::success()).await
}
