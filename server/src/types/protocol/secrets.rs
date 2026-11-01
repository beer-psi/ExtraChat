use crate::util::redacted::Redacted;
use serde::{Deserialize, Serialize};
use uuid::Uuid;

/// A user sends this request if they have lost their
/// shared secret for a channel.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SecretsRequest {
    pub channel: Uuid,
}

/// When the server has received the shared secret from
/// another member, this response is sent to the initial
/// user requesting the secret.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SecretsResponse {
    pub channel: Uuid,
    #[serde(with = "serde_bytes")]
    pub pk: Redacted<Vec<u8>>,
    #[serde(with = "serde_bytes")]
    pub encrypted_shared_secret: Redacted<Vec<u8>>,
}

/// This response is sent to a random, online member of
/// the channel that the user has requested the secret
/// for. The server will wait a predetermined amount of
/// time for the user to respond with a `SendSecretsResponse`
/// before trying a different member.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SendSecretsResponse {
    pub channel: Uuid,
    pub request_id: Uuid,
    #[serde(with = "serde_bytes")]
    pub pk: Redacted<Vec<u8>>,
}

/// Clients send this request to the server after having
/// been asked to send a secret. The client may or may not
/// have the secret, so the `encrypted_shared_secret` field
/// is optional.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SendSecretsRequest {
    pub request_id: Uuid,
    #[serde(with = "serde_bytes")]
    pub encrypted_shared_secret: Option<Redacted<Vec<u8>>>,
}
