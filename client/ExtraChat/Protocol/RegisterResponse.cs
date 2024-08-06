using ExtraChat.Formatters;
using MessagePack;

namespace ExtraChat.Protocol;

[Serializable]
[MessagePackObject]
[MessagePackFormatter(typeof(RegisterResponseFormatter))]
public abstract record RegisterResponse {
    [MessagePackObject]
    public record Challenge(string Text) : RegisterResponse;

    [MessagePackObject]
    public record Failure(FailureReason Reason) : RegisterResponse;

    [MessagePackObject]
    public record Success(string Key) : RegisterResponse;
}
