use anyhow::{Context, Result};
use futures_util::SinkExt;
use prefixed_api_key::ApiKey;
use sha3::Sha3_256;
use tokio::sync::RwLock;
use tokio_tungstenite::tungstenite::Message as WsMessage;
use uuid::Uuid;

use crate::{Digest, ResponseContainer, State, types::protocol::ResponseKind, World, WsStream};

pub mod redacted;

pub async fn send(conn: &mut WsStream, number: u32, msg: impl Into<ResponseKind>) -> Result<()> {
    let container = ResponseContainer {
        number,
        kind: msg.into(),
    };

    conn.send(WsMessage::Binary(rmp_serde::to_vec(&container)?)).await?;
    Ok(())
}

pub async fn send_to_all(state: &RwLock<State>, channel_id: Uuid, number: u32, msg: impl Into<ResponseKind>) -> Result<()> {
    let members = get_raw_members(state, channel_id).await?
        .into_iter()
        .chain(get_raw_invited_members(state, channel_id).await?.into_iter());

    let resp = ResponseContainer {
        number,
        kind: msg.into(),
    };
    for member in members {
        if let Some(client) = state.read().await.clients.get(&(member.lodestone_id as u64)) {
            client.read().await.tx.send(resp.clone()).await.ok();
        }
    }

    Ok(())
}

#[derive(Debug)]
pub struct RawMember {
    pub lodestone_id: i64,
    pub name: String,
    pub world: String,
    pub rank: i64,
}

pub async fn get_raw_members(state: &RwLock<State>, channel: Uuid) -> Result<Vec<RawMember>> {
    let id = channel.as_simple().to_string();
    sqlx::query_as!(
        RawMember,
        // language=sqlite
        "select users.lodestone_id, users.name, users.world, user_channels.rank from user_channels inner join users on users.lodestone_id = user_channels.lodestone_id where user_channels.channel_id = ?",
        id,
    )
        .fetch_all(&state.read().await.db)
        .await
        .context("could not get channel members")
}

pub async fn get_raw_invited_members(state: &RwLock<State>, channel: Uuid) -> Result<Vec<RawMember>> {
    let id = channel.as_simple().to_string();
    sqlx::query_as!(
        RawMember,
        // language=sqlite
        "select users.lodestone_id, users.name, users.world, cast(0 as int) as rank from channel_invites inner join users on users.lodestone_id = channel_invites.invited where channel_invites.channel_id = ?",
        id,
    )
        .fetch_all(&state.read().await.db)
        .await
        .context("could not get channel members")
}

pub async fn is_invited(state: &RwLock<State>, channel: Uuid, id: u64) -> Result<bool> {
    let channel_id = channel.as_simple().to_string();
    let id = id as i64;
    sqlx::query!(
        // language=sqlite
        "select count(*) as count from channel_invites where channel_id = ? and invited = ?",
        channel_id,
        id,
    )
        .fetch_one(&state.read().await.db)
        .await
        .context("could not get channel members")
        .map(|x| x.count > 0)
}

pub fn hash_key(key: &ApiKey) -> String {
    let mut hasher = Sha3_256::new();
    hasher.update(&key.long_bytes);
    hex::encode(&hasher.finalize()[..])
}

pub fn id_from_world(world: World) -> u16 {
    match world {
        World::Ravana => 21,
        World::Bismarck => 22,
        World::Asura => 23,
        World::Belias => 24,
        World::Pandaemonium => 28,
        World::Shinryu => 29,
        World::Unicorn => 30,
        World::Yojimbo => 31,
        World::Zeromus => 32,
        World::Twintania => 33,
        World::Brynhildr => 34,
        World::Famfrit => 35,
        World::Lich => 36,
        World::Mateus => 37,
        World::Omega => 39,
        World::Jenova => 40,
        World::Zalera => 41,
        World::Zodiark => 42,
        World::Alexander => 43,
        World::Anima => 44,
        World::Carbuncle => 45,
        World::Fenrir => 46,
        World::Hades => 47,
        World::Ixion => 48,
        World::Kujata => 49,
        World::Typhon => 50,
        World::Ultima => 51,
        World::Valefor => 52,
        World::Exodus => 53,
        World::Faerie => 54,
        World::Lamia => 55,
        World::Phoenix => 56,
        World::Siren => 57,
        World::Garuda => 58,
        World::Ifrit => 59,
        World::Ramuh => 60,
        World::Titan => 61,
        World::Diabolos => 62,
        World::Gilgamesh => 63,
        World::Leviathan => 64,
        World::Midgardsormr => 65,
        World::Odin => 66,
        World::Shiva => 67,
        World::Atomos => 68,
        World::Bahamut => 69,
        World::Chocobo => 70,
        World::Moogle => 71,
        World::Tonberry => 72,
        World::Adamantoise => 73,
        World::Coeurl => 74,
        World::Malboro => 75,
        World::Tiamat => 76,
        World::Ultros => 77,
        World::Behemoth => 78,
        World::Cactuar => 79,
        World::Cerberus => 80,
        World::Goblin => 81,
        World::Mandragora => 82,
        World::Louisoix => 83,
        World::Spriggan => 85,
        World::Sephirot => 86,
        World::Sophia => 87,
        World::Zurvan => 88,
        World::Aegis => 90,
        World::Balmung => 91,
        World::Durandal => 92,
        World::Excalibur => 93,
        World::Gungnir => 94,
        World::Hyperion => 95,
        World::Masamune => 96,
        World::Ragnarok => 97,
        World::Ridill => 98,
        World::Sargatanas => 99,
        World::Sagittarius => 400,
        World::Phantom => 401,
        World::Alpha => 402,
        World::Raiden => 403,
        World::Marilith => 404,
        World::Seraph => 405,
        World::Halicarnassus => 406,
        World::Maduin => 407,
        World::Cuchulainn => 408,
        World::Kraken => 409,
        World::Rafflesia => 410,
        World::Golem => 411,
        World::Titania => 412,
        World::Innocence => 413,
        World::Pixie => 414,
        World::Tycoon => 415,
    }
}

pub fn world_from_id(id: u16) -> Option<World> {
    let world = match id {
        21 => World::Ravana,
        22 => World::Bismarck,
        23 => World::Asura,
        24 => World::Belias,
        28 => World::Pandaemonium,
        29 => World::Shinryu,
        30 => World::Unicorn,
        31 => World::Yojimbo,
        32 => World::Zeromus,
        33 => World::Twintania,
        34 => World::Brynhildr,
        35 => World::Famfrit,
        36 => World::Lich,
        37 => World::Mateus,
        39 => World::Omega,
        40 => World::Jenova,
        41 => World::Zalera,
        42 => World::Zodiark,
        43 => World::Alexander,
        44 => World::Anima,
        45 => World::Carbuncle,
        46 => World::Fenrir,
        47 => World::Hades,
        48 => World::Ixion,
        49 => World::Kujata,
        50 => World::Typhon,
        51 => World::Ultima,
        52 => World::Valefor,
        53 => World::Exodus,
        54 => World::Faerie,
        55 => World::Lamia,
        56 => World::Phoenix,
        57 => World::Siren,
        58 => World::Garuda,
        59 => World::Ifrit,
        60 => World::Ramuh,
        61 => World::Titan,
        62 => World::Diabolos,
        63 => World::Gilgamesh,
        64 => World::Leviathan,
        65 => World::Midgardsormr,
        66 => World::Odin,
        67 => World::Shiva,
        68 => World::Atomos,
        69 => World::Bahamut,
        70 => World::Chocobo,
        71 => World::Moogle,
        72 => World::Tonberry,
        73 => World::Adamantoise,
        74 => World::Coeurl,
        75 => World::Malboro,
        76 => World::Tiamat,
        77 => World::Ultros,
        78 => World::Behemoth,
        79 => World::Cactuar,
        80 => World::Cerberus,
        81 => World::Goblin,
        82 => World::Mandragora,
        83 => World::Louisoix,
        85 => World::Spriggan,
        86 => World::Sephirot,
        87 => World::Sophia,
        88 => World::Zurvan,
        90 => World::Aegis,
        91 => World::Balmung,
        92 => World::Durandal,
        93 => World::Excalibur,
        94 => World::Gungnir,
        95 => World::Hyperion,
        96 => World::Masamune,
        97 => World::Ragnarok,
        98 => World::Ridill,
        99 => World::Sargatanas,
        400 => World::Sagittarius,
        401 => World::Phantom,
        402 => World::Alpha,
        403 => World::Raiden,
        404 => World::Marilith,
        405 => World::Seraph,
        406 => World::Halicarnassus,
        407 => World::Maduin,
        408 => World::Cuchulainn,
        409 => World::Kraken,
        410 => World::Rafflesia,
        411 => World::Golem,
        412 => World::Titania,
        413 => World::Innocence,
        414 => World::Pixie,
        415 => World::Tycoon,
        _ => return None,
    };

    Some(world)
}
