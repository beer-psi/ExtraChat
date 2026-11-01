use std::{sync::Arc, time::Duration};

use anyhow::{Context, Result};
use lodestone_scraper::LodestoneScraper;
use log::{debug, error, trace};
use tokio::{
    sync::{mpsc::UnboundedReceiver, RwLock},
    task::JoinHandle,
    time::Instant,
};

use crate::State;

pub fn spawn(state: Arc<RwLock<State>>, mut rx: UnboundedReceiver<i64>) -> JoinHandle<()> {
    const WAIT_TIME: u64 = 5;

    tokio::task::spawn(async move {
        let lodestone = LodestoneScraper::default();

        let mut last_update = Instant::now();
        while let Some(id) = rx.recv().await {
            // make sure to wait five seconds between each request
            let elapsed = last_update.elapsed();
            if elapsed < Duration::from_secs(WAIT_TIME) {
                let left = Duration::from_secs(WAIT_TIME) - elapsed;
                tokio::time::sleep(left).await;
            }

            match update(&state, &lodestone, id).await {
                Ok(()) => debug!("updated user {}", id),
                Err(e) => error!("error updating user {}: {:?}", id, e),
            }

            last_update = Instant::now();
        }
    })
}

async fn update(
    state: &RwLock<State>,
    lodestone: &LodestoneScraper,
    lodestone_id: i64,
) -> Result<()> {
    let info = lodestone
        .character(lodestone_id as u64)
        .await
        .context("could not get character info")?;
    let world_name = info.world.as_str();

    sqlx::query!(
            // language=sqlite
            "update users set name = ?, world = ?, last_updated = current_timestamp where lodestone_id = ?",
            info.name,
            world_name,
            lodestone_id,
        )
        .execute(&state.read().await.db)
        .await
        .context("could not update user")?;

    trace!("  [updater] before state read");
    let client_state = state
        .read()
        .await
        .clients
        .get(&(lodestone_id as u64))
        .cloned();
    trace!("  [updater] after state read");
    if let Some(user) = client_state {
        trace!("  [updater] before user write");
        if let Some(user) = user.write().await.user.as_mut() {
            user.name = info.name;
            user.world = info.world;
        }
        trace!("  [updater] after user write");
    }

    Ok(())
}
