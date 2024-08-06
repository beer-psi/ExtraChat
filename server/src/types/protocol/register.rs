use serde::{Deserialize, Serialize};
use crate::util::redacted::Redacted;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RegisterRequest {
    pub name: String,
    pub world: u16,
    pub challenge_completed: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RegisterResponse {
    Challenge {
        challenge: String,
    },
    Failure {
        reason: FailureReason,
    },
    Success {
        key: Redacted<String>,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FailureReason {
    MissingCharacter,
    PrivateProfile,
    ChallengeNotFound,
}
