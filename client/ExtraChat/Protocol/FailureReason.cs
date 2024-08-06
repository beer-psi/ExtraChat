namespace ExtraChat.Protocol;

[Serializable]
public enum FailureReason {
    MissingCharacter,
    PrivateProfile,
    ChallengeNotFound,
}
