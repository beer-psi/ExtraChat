using System.Text;
using ExtraChat.Protocol;
using MessagePack;
using MessagePack.Formatters;

namespace ExtraChat.Formatters;

public class RegisterResponseFormatter : IMessagePackFormatter<RegisterResponse> {
    public void Serialize(ref MessagePackWriter writer, RegisterResponse value, MessagePackSerializerOptions options) {
        if (value is RegisterResponse.Failure) {
            writer.WriteString(Encoding.UTF8.GetBytes("failure"));
            return;
        }

        writer.WriteMapHeader(1);

        var key = value switch {
            RegisterResponse.Challenge => "challenge",
            RegisterResponse.Failure => "failure",
            RegisterResponse.Success => "success",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

        writer.WriteString(Encoding.UTF8.GetBytes(key));

        switch (value) {
            case RegisterResponse.Challenge challenge: {
                writer.WriteArrayHeader(1);
                writer.WriteString(Encoding.UTF8.GetBytes(challenge.Text));
                break;
            }
            case RegisterResponse.Failure failure: {
                var text = failure.Reason switch {
                    FailureReason.MissingCharacter => "missing_character",
                    FailureReason.PrivateProfile => "private_profile",
                    FailureReason.ChallengeNotFound => "challenge_not_found",
                    _ => throw new ArgumentOutOfRangeException(nameof(failure.Reason)),
                };

                writer.WriteArrayHeader(1);
                writer.WriteString(Encoding.UTF8.GetBytes(text));
                break;
            }
            case RegisterResponse.Success success: {
                writer.WriteArrayHeader(1);
                writer.WriteString(Encoding.UTF8.GetBytes(success.Key));
                break;
            }
        }
    }

    public RegisterResponse Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) {
        if (reader.NextMessagePackType == MessagePackType.Map) {
            if (reader.ReadMapHeader() != 1) {
                throw new MessagePackSerializationException("Invalid map length");
            }
        } else {
            throw new MessagePackSerializationException("Invalid RegisterResponse");
        }

        var key = reader.ReadString();
        switch (key) {
            case "challenge": {
                if (reader.ReadArrayHeader() != 1) {
                    throw new MessagePackSerializationException("Invalid RegisterResponse");
                }

                var text = reader.ReadString();
                return new RegisterResponse.Challenge(text);
            }
            case "failure": {
                if (reader.ReadArrayHeader() != 1) {
                    throw new MessagePackSerializationException("Invalid RegisterResponse");
                }

                var reason = reader.ReadString() switch {
                    "missing_character" => FailureReason.MissingCharacter,
                    "private_profile" => FailureReason.PrivateProfile,
                    "challenge_not_found" => FailureReason.ChallengeNotFound,
                    _ => throw new MessagePackSerializationException("Invalid RegisterResponse"),
                };

                return new RegisterResponse.Failure(reason);
            }
            case "success": {
                if (reader.ReadArrayHeader() != 1) {
                    throw new MessagePackSerializationException("Invalid RegisterResponse");
                }

                var text = reader.ReadString();
                return new RegisterResponse.Success(text);
            }
            default:
                throw new MessagePackSerializationException("Invalid RegisterResponse type");
        }
    }
}
