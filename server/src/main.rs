#![feature(try_blocks)]

use std::collections::HashMap;
use std::str::FromStr;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use anyhow::{Context, Result};
use futures_util::{SinkExt, StreamExt};
use lodestone_scraper::lodestone_parser::ffxiv_types::World;
use log::{debug, error, info, Level, LevelFilter, warn};
use rustyline::history::DefaultHistory;
use sha3::Digest;
use sqlx::{ConnectOptions, Executor, Pool, Sqlite};
use sqlx::migrate::Migrator;
use sqlx::sqlite::{SqliteConnectOptions, SqlitePoolOptions};
use tokio::net::{UnixListener, UnixStream};
use tokio::sync::mpsc::{Sender, UnboundedSender};
use tokio::sync::RwLock;
use tokio_tungstenite::{
    tungstenite::Message as WsMessage,
    WebSocketStream,
};
use uuid::Uuid;

use crate::types::{
    protocol::{
        MessageRequest,
        MessageResponse,
        RegisterRequest,
        RegisterResponse,
        RequestContainer,
        RequestKind,
        ResponseContainer,
    },
    user::User,
};
use crate::handlers::SecretsRequestInfo;
use crate::types::config::Config;
use crate::types::protocol::{AnnounceResponse, AuthenticateRequest, AuthenticateResponse, ErrorResponse, ResponseKind};
use crate::types::protocol::channel::Rank;

pub mod types;
pub mod handlers;
pub mod util;
pub mod updater;
pub mod logging;
pub mod influx;

#[global_allocator]
static ALLOC: mimalloc::MiMalloc = mimalloc::MiMalloc;

pub type WsStream = WebSocketStream<UnixStream>;

pub struct State {
    pub db: Pool<Sqlite>,
    pub clients: HashMap<u64, Arc<RwLock<ClientState>>>,
    pub ids: HashMap<(String, u16), u64>,
    pub secrets_requests: HashMap<Uuid, SecretsRequestInfo>,
    pub messages_sent: AtomicU64,
    pub updater_tx: UnboundedSender<i64>,
}

impl State {
    pub async fn announce(&self, msg: impl Into<String>) {
        let msg = msg.into();

        for client in self.clients.values() {
            client.read().await.tx.send(ResponseContainer {
                number: 0,
                kind: ResponseKind::Announce(AnnounceResponse::new(&msg)),
            }).await.ok();
        }
    }

    pub async fn get_id(&self, state: &RwLock<State>, name: &str, world: u16) -> Option<u64> {
        // if they're logged in, grab the id the easy way
        if let Some(id) = self.ids.get(&(name.to_string(), world)).copied() {
            return Some(id);
        }

        let world_name = util::world_from_id(world)?.as_str();
        let id = sqlx::query!(
            // language=sqlite
            "select lodestone_id from users where name = ? and world = ?",
            name,
            world_name,
        )
            .fetch_optional(&state.read().await.db)
            .await
            .ok()?;

        id.map(|id| id.lodestone_id as u64)
    }
}

static MIGRATOR: Migrator = sqlx::migrate!();

#[tokio::main]
async fn main() -> Result<()> {
    logging::setup()?;

    // get config
    let config_path = std::env::args().nth(1).unwrap_or_else(|| "config.toml".to_string());
    let config_toml = std::fs::read_to_string(config_path)
        .context("couldn't read config file")?;
    let config: Config = toml::from_str(&config_toml)
        .context("couldn't parse config file")?;

    // set up database pool
    let options = SqliteConnectOptions::new()
        .log_statements(LevelFilter::Debug)
        .filename(&config.database.path);

    let pool = SqlitePoolOptions::new()
        .after_connect(|conn, _| Box::pin(async move {
            conn.execute(
                // language=sqlite
                "PRAGMA foreign_keys = ON;"
            ).await?;
            Ok(())
        }))
        .connect_with(options)
        .await
        .context("could not connect to database")?;
    MIGRATOR.run(&pool)
        .await
        .context("could not run database migrations")?;

    // set up updater channel
    let (updater_tx, updater_rx) = tokio::sync::mpsc::unbounded_channel();

    // set up server
    let server = UnixListener::bind(&config.server.path)?;
    let state = Arc::new(RwLock::new(State {
        db: pool,
        clients: Default::default(),
        ids: Default::default(),
        secrets_requests: Default::default(),
        messages_sent: AtomicU64::default(),
        updater_tx,
    }));

    let listening_on = server.local_addr()
        .ok()
        .and_then(|addr| addr.as_pathname().map(ToOwned::to_owned))
        .and_then(|addr| addr.to_str().map(ToOwned::to_owned))
        .unwrap_or_else(|| config.server.path.to_string_lossy().to_string());
    info!("Listening on ws://unix:{listening_on}/");

    let (quit_tx, mut quit_rx) = tokio::sync::mpsc::channel(1);
    let (announce_tx, mut announce_rx) = tokio::sync::mpsc::channel(1);

    std::thread::spawn(move || {
        let mut editor = match rustyline::Editor::<(), DefaultHistory>::new() {
            Ok(e) => e,
            Err(e) => {
                error!("error creating line editor: {:#?}", e);
                return;
            }
        };

        for line in editor.iter("> ") {
            let line = match line {
                Ok(l) => l,
                Err(rustyline::error::ReadlineError::Interrupted) => {
                    quit_tx.blocking_send(()).ok();
                    return;
                }
                Err(e) => {
                    error!("error reading input: {:#?}", e);
                    continue;
                }
            };

            let command: Vec<_> = line.splitn(2, ' ').collect();
            match command[0] {
                "exit" | "quit" => {
                    quit_tx.blocking_send(()).ok();
                    return;
                }
                "announce" | "say" => {
                    if command.len() == 2 {
                        let msg = command[1].to_string();
                        announce_tx.blocking_send(msg).ok();
                    } else {
                        info!("usage: announce <message>");
                    }
                }
                "log" | "level" => {
                    if command.len() == 2 {
                        match Level::from_str(command[1]) {
                            Ok(level) => *logging::LOG_LEVEL.write() = level,
                            Err(_) => warn!("invalid log level"),
                        }
                    } else {
                        info!("usage: log <trace|debug|info|warn|error>");
                    }
                }
                "" => {}
                x => warn!("unknown command: {}", x),
            }
        }
    });

    {
        let state = Arc::clone(&state);
        tokio::task::spawn(async move {
            let mut last_messages = 0;

            loop {
                let messages = state.read().await.messages_sent.load(Ordering::SeqCst);
                let diff = messages - last_messages;
                last_messages = messages;

                let clients = state.read().await.clients.len();

                info!(
                    "Clients: {}, messages sent: {} (+{})",
                    clients,
                    messages,
                    diff,
                );
                tokio::time::sleep(Duration::from_secs(60)).await;
            }
        });
    }

    influx::spawn(&config, Arc::clone(&state));

    updater::spawn(Arc::clone(&state), updater_rx);

    loop {
        let res: Result<()> = try {
            tokio::select! {
                accept = server.accept() => {
                    let (sock, _addr) = accept.context("could not accept socket connection")?;
                    let state = Arc::clone(&state);
                    tokio::task::spawn(async move {
                        let conn = match tokio_tungstenite::accept_async(sock).await {
                            Ok(c) => c,
                            Err(e) => {
                                error!("client error: {:?}", e);
                                return;
                            }
                        };

                        if let Err(e) = client_loop(state, conn).await {
                            error!("client error: {}", e);
                        }
                    });
                }
                _ = quit_rx.recv() => {
                    break;
                }
                msg = announce_rx.recv() => {
                    if let Some(msg) = msg {
                        state.read().await.announce(msg).await;
                    }
                }
            }
        };

        if let Err(e) = res {
            error!("server error: {}", e);
        }
    }

    info!("quitting");
    Ok(())
}

pub struct ClientState {
    user: Option<User>,
    tx: Sender<ResponseContainer>,
    shutdown_tx: Sender<()>,
    pk: Vec<u8>,
    allow_invites: bool,
}

impl ClientState {
    pub fn lodestone_id(&self) -> Option<u64> {
        self.user.as_ref().map(|u| u.lodestone_id)
    }

    pub async fn in_channel(&self, channel_id: Uuid, state: &RwLock<State>) -> Result<bool> {
        let user = match &self.user {
            Some(user) => user,
            None => return Ok(false),
        };

        let channel_id_str = channel_id.as_simple().to_string();
        let id = user.lodestone_id as i64;
        let members = sqlx::query!(
            // language=sqlite
            "select count(*) as count from user_channels where channel_id = ? and lodestone_id = ?",
            channel_id_str,
            id,
        )
            .fetch_one(&state.read().await.db)
            .await
            .context("could not get count")?;

        Ok(members.count > 0)
    }

    pub async fn get_rank(&self, channel_id: Uuid, state: &RwLock<State>) -> Result<Option<Rank>> {
        let user = match &self.user {
            Some(user) => user,
            None => return Ok(None),
        };

        let channel_id_str = channel_id.as_simple().to_string();
        let id = user.lodestone_id as i64;
        let rank = sqlx::query!(
            // language=sqlite
            "select rank from user_channels where channel_id = ? and lodestone_id = ?",
            channel_id_str,
            id,
        )
            .fetch_optional(&state.read().await.db)
            .await
            .context("could not get rank")?;

        Ok(rank.map(|rank| Rank::from_u8(rank.rank as u8)))
    }

    pub async fn get_rank_invite(&self, channel_id: Uuid, state: &RwLock<State>) -> Result<Option<Rank>> {
        if let Some(rank) = self.get_rank(channel_id, state).await? {
            return Ok(Some(rank));
        }

        let user = match &self.user {
            Some(user) => user,
            None => return Ok(None),
        };

        let channel_id_str = channel_id.as_simple().to_string();
        let id = user.lodestone_id as i64;
        let count = sqlx::query!(
            // language=sqlite
            "select count(*) as count from channel_invites where channel_id = ? and invited = ?",
            channel_id_str,
            id,
        )
            .fetch_one(&state.read().await.db)
            .await
            .context("could not get count")?
            .count;

        if count > 0 {
            Ok(Some(Rank::Invited))
        } else {
            Ok(None)
        }
    }
}

async fn client_loop(state: Arc<RwLock<State>>, mut conn: WsStream) -> Result<()> {
    let (tx, mut rx) = tokio::sync::mpsc::channel(10);
    let (shutdown_tx, mut shutdown_rx) = tokio::sync::mpsc::channel(1);

    let client_state = Arc::new(RwLock::new(ClientState {
        user: None,
        tx,
        shutdown_tx,
        pk: Default::default(),
        allow_invites: false,
    }));

    loop {
        let res: Result<()> = try {
            tokio::select! {
                _ = shutdown_rx.recv() => {
                    debug!("break due to new login");
                    break;
                }
                msg = rx.recv() => {
                    if let Some(msg) = msg {
                        let encoded = rmp_serde::to_vec(&msg).context("could not encode messagepack")?;
                        conn.send(WsMessage::Binary(encoded)).await.context("could not send message")?;
                    }
                }
                msg = conn.next() => {
                    // match &msg {
                    //     Some(Ok(WsMessage::Pong(_))) => {},
                    //     _ => debug!("{:?}", msg),
                    // }

                    match msg {
                        Some(Ok(WsMessage::Binary(msg))) => {
                            let msg: RequestContainer = rmp_serde::from_slice(&msg).context("could not decode messagepack")?;
                            debug!("{:#?}", msg);

                            let logged_in = client_state.read().await.user.is_some();

                            match msg.kind {
                                RequestKind::Ping(_) => {
                                    crate::handlers::ping(&mut conn, msg.number).await?;
                                }
                                RequestKind::Version(req) => {
                                    if !crate::handlers::version(&mut conn, msg.number, req).await? {
                                        break;
                                    }
                                }
                                RequestKind::Register(req) => {
                                    crate::handlers::register(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Authenticate(req) => {
                                    crate::handlers::authenticate(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Create(req) if logged_in => {
                                    crate::handlers::create(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::PublicKey(req) if logged_in => {
                                    crate::handlers::public_key(Arc::clone(&state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Invite(req) if logged_in => {
                                    crate::handlers::invite(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Join(req) if logged_in => {
                                    crate::handlers::join(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Message(req) if logged_in => {
                                    crate::handlers::message(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::List(req) if logged_in => {
                                    crate::handlers::list(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Leave(req) if logged_in => {
                                    crate::handlers::leave(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Promote(req) if logged_in => {
                                    crate::handlers::promote(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Kick(req) if logged_in => {
                                    crate::handlers::kick(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Disband(req) if logged_in => {
                                    crate::handlers::disband(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Update(req) if logged_in => {
                                    crate::handlers::update(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::Secrets(req) if logged_in => {
                                    crate::handlers::secrets(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::SendSecrets(req) if logged_in => {
                                    crate::handlers::send_secrets(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::AllowInvites(req) if logged_in => {
                                    crate::handlers::allow_invites(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                RequestKind::DeleteAccount(req) if logged_in => {
                                    crate::handlers::delete_account(Arc::clone(&state), Arc::clone(&client_state), &mut conn, msg.number, req).await?;
                                }
                                _ if !logged_in => {
                                    util::send(&mut conn, msg.number, ErrorResponse::new(None, "not logged in")).await?;
                                }
                                _ => {
                                    util::send(&mut conn, msg.number, ErrorResponse::new(None, "not yet implemented")).await?;
                                }
                            }
                        }
                        None | Some(Ok(WsMessage::Close(_))) | Some(Err(_)) => {
                            debug!("break");
                            break;
                        }
                        _ => {}
                    }
                }
            }
        };

        if let Err(e) = res {
            error!("error in client loop: {:#?}", e);
            break;
        }
    }

    debug!("ending client thread");

    if let Some(user) = &client_state.read().await.user {
        state.write().await.clients.remove(&user.lodestone_id);
        state.write().await.ids.remove(&(user.name.clone(), util::id_from_world(user.world)));
    }

    debug!("client thread ended");

    Ok(())
}
