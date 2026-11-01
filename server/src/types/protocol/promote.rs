use crate::types::protocol::channel::Rank;
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PromoteRequest {
    pub channel: Uuid,
    pub name: String,
    pub world: u16,
    pub rank: Rank,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PromoteResponse {
    pub channel: Uuid,
    pub name: String,
    pub world: u16,
    pub rank: Rank,
}
