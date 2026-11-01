use crate::util::redacted::Redacted;
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UpdateRequest {
    pub channel: Uuid,
    pub kind: UpdateKind,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum UpdateKind {
    Name(#[serde(with = "serde_bytes")] Redacted<Vec<u8>>),
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UpdateResponse {
    pub channel: Uuid,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UpdatedResponse {
    pub channel: Uuid,
    pub kind: UpdateKind,
}
