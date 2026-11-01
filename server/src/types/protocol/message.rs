use crate::util::redacted::Redacted;
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MessageRequest {
    pub channel: Uuid,
    #[serde(with = "serde_bytes")]
    pub message: Redacted<Vec<u8>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MessageResponse {
    pub channel: Uuid,
    pub sender: String,
    pub world: u16,
    #[serde(with = "serde_bytes")]
    pub message: Redacted<Vec<u8>>,
}
