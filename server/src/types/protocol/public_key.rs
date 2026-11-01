use crate::util::redacted::Redacted;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PublicKeyRequest {
    pub name: String,
    pub world: u16,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PublicKeyResponse {
    pub name: String,
    pub world: u16,
    #[serde(with = "serde_bytes")]
    pub pk: Option<Redacted<Vec<u8>>>,
}
