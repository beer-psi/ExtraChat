use std::sync::Arc;

use anyhow::{Context, Result};
use chrono::{Duration, TimeZone, Utc};
use lodestone_scraper::LodestoneScraper;
use log::warn;
use rand::RngCore;
use tokio::sync::RwLock;

use crate::{types::protocol::FailureReason, util::{hash_key, send, world_from_id}, ClientState, RegisterRequest, RegisterResponse, State, WsStream};

pub async fn register(state: Arc<RwLock<State>>, _client_state: Arc<RwLock<ClientState>>, conn: &mut WsStream, number: u32, req: RegisterRequest) -> Result<()> {
    let scraper = LodestoneScraper::default();

    let world = world_from_id(req.world)
        .context("invalid world id")?;

    // look up character
    let mut page = 1;
    let character = loop {
        let search = scraper.character_search()
            .name(&req.name)
            .world(world)
            .page(page)
            .send()
            .await?;
        let chara = search
            .results
            .into_iter()
            .find(|c| c.name == req.name && Some(c.world) == world_from_id(req.world));
        if chara.is_some() {
            break chara;
        }

        page += 1;
        if page > search.pagination.total_pages {
            break None;
        }
    };
    let character = character.context("could not find character")?;
    let lodestone_id = character.id as i64;

    // get challenge
    let challenge: Option<_> = sqlx::query!(
            // language=sqlite
            "select challenge, created_at from verifications where lodestone_id = ?",
            lodestone_id,
        )
        .fetch_optional(&state.read().await.db)
        .await
        .context("could not query database for verification")?;

    if !req.challenge_completed || challenge.is_none() {
        let generate = match &challenge {
            Some(r) if Utc::now().signed_duration_since(Utc.from_utc_datetime(&r.created_at)) > Duration::minutes(5) => {
                // set up a challenge if one hasn't been set up in the last five minutes
                true
            }
            Some(_) => {
                // challenge already exists, send back existing one
                false
            }
            None => true,
        };

        let challenge = match &challenge {
            None | Some(_) if generate => {
                let mut rand_bytes = [0; 32];
                rand::thread_rng().fill_bytes(&mut rand_bytes);
                let challenge = hex::encode(rand_bytes);

                sqlx::query!(
                    // language=sqlite
                    "
                        insert into verifications (lodestone_id, challenge)
                        values (?1, ?2)
                        on conflict (lodestone_id)
                            do update set challenge  = ?2,
                                          created_at = current_timestamp
                    ",
                    lodestone_id,
                    challenge,
                )
                    .execute(&state.read().await.db)
                    .await?;

                challenge
            }
            Some(r) => r.challenge.clone(),
            None => unreachable!(),
        };

        send(conn, number, RegisterResponse::Challenge {
            challenge,
        }).await?;
        return Ok(());
    }

    // verify challenge
    let challenge = match challenge {
        Some(c) => c,
        // should not be possible
        None => return Ok(()),
    };

    let chara_info = match scraper.character(character.id).await {
        Ok(c) => c,
        Err(e) => {
            warn!("missing character {}: {e:#}", character.id);
            send(conn, number, RegisterResponse::Failure {
                reason: FailureReason::MissingCharacter,
            }).await?;
            return Ok(());
        }
    };

    let profile_text = match chara_info.profile {
        Some(p) => p.profile_text,
        None => {
            send(conn, number, RegisterResponse::Failure {
                reason: FailureReason::PrivateProfile,
            }).await?;
            return Ok(());
        }
    };

    let verified = profile_text.contains(&challenge.challenge);
    if !verified {
        send(conn, number, RegisterResponse::Failure {
             reason: FailureReason::ChallengeNotFound,
        }).await?;
        return Ok(());
    }

    sqlx::query!(
        // language=sqlite
        "delete from verifications where lodestone_id = ?",
        lodestone_id,
    )
        .execute(&state.read().await.db)
        .await
        .context("could not remove verification")?;

    let key = prefixed_api_key::generate("extrachat", None);
    let hash = hash_key(&key);

    let world_name = character.world.as_str();
    sqlx::query!(
        // language=sqlite
        "
            insert into users (lodestone_id, name, world, key_short, key_hash, last_updated)
            values (?1, ?2, ?3, ?4, ?5, current_timestamp)
            on conflict (lodestone_id)
                do update set name         = ?2,
                              world        = ?3,
                              key_short    = ?4,
                              key_hash     = ?5,
                              last_updated = current_timestamp
        ",
        lodestone_id,
        character.name,
        world_name,
        key.short_token,
        hash,
    )
        .execute(&state.read().await.db)
        .await
        .context("could not insert user")?;

    send(conn, number, RegisterResponse::Success {
        key: key.to_string().into(),
    }).await?;

    Ok(())
}
