using System.Text;
using ExtraChat.Protocol;
using MessagePack;
using MessagePack.Formatters;

namespace ExtraChat.Formatters;

public class RequestKindFormatter : IMessagePackFormatter<RequestKind> {
    public void Serialize(ref MessagePackWriter writer, RequestKind value, MessagePackSerializerOptions options) {
        writer.WriteMapHeader(1);

        var key = value switch {
            RequestKind.Ping => "ping",
            RequestKind.Authenticate => "authenticate",
            RequestKind.Create => "create",
            RequestKind.Invite => "invite",
            RequestKind.Join => "join",
            RequestKind.Message => "message",
            RequestKind.PublicKey => "public_key",
            RequestKind.Register => "register",
            RequestKind.List => "list",
            RequestKind.Leave => "leave",
            RequestKind.Kick => "kick",
            RequestKind.Disband => "disband",
            RequestKind.Promote => "promote",
            RequestKind.Update => "update",
            RequestKind.Version => "version",
            RequestKind.DeleteAccount => "delete_account",
            RequestKind.AllowInvites => "allow_invites",
            RequestKind.Secrets => "secrets",
            RequestKind.SendSecrets => "send_secrets",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

        writer.WriteString(Encoding.UTF8.GetBytes(key));

        switch (value) {
            case RequestKind.Ping ping:
                options.Resolver.GetFormatterWithVerify<PingRequest>().Serialize(ref writer, ping.Request, options);
                break;
            case RequestKind.Authenticate authenticate:
                options.Resolver.GetFormatterWithVerify<AuthenticateRequest>().Serialize(ref writer, authenticate.Request, options);
                break;
            case RequestKind.Create create:
                options.Resolver.GetFormatterWithVerify<CreateRequest>().Serialize(ref writer, create.Request, options);
                break;
            case RequestKind.Invite invite:
                options.Resolver.GetFormatterWithVerify<InviteRequest>().Serialize(ref writer, invite.Request, options);
                break;
            case RequestKind.Join join:
                options.Resolver.GetFormatterWithVerify<JoinRequest>().Serialize(ref writer, join.Request, options);
                break;
            case RequestKind.Message message:
                options.Resolver.GetFormatterWithVerify<MessageRequest>().Serialize(ref writer, message.Request, options);
                break;
            case RequestKind.PublicKey publicKey:
                options.Resolver.GetFormatterWithVerify<PublicKeyRequest>().Serialize(ref writer, publicKey.Request, options);
                break;
            case RequestKind.Register register:
                options.Resolver.GetFormatterWithVerify<RegisterRequest>().Serialize(ref writer, register.Request, options);
                break;
            case RequestKind.List list:
                options.Resolver.GetFormatterWithVerify<ListRequest>().Serialize(ref writer, list.Request, options);
                break;
            case RequestKind.Leave leave:
                options.Resolver.GetFormatterWithVerify<LeaveRequest>().Serialize(ref writer, leave.Request, options);
                break;
            case RequestKind.Kick kick:
                options.Resolver.GetFormatterWithVerify<KickRequest>().Serialize(ref writer, kick.Request, options);
                break;
            case RequestKind.Disband disband:
                options.Resolver.GetFormatterWithVerify<DisbandRequest>().Serialize(ref writer, disband.Request, options);
                break;
            case RequestKind.Promote promote:
                options.Resolver.GetFormatterWithVerify<PromoteRequest>().Serialize(ref writer, promote.Request, options);
                break;
            case RequestKind.Update update:
                options.Resolver.GetFormatterWithVerify<UpdateRequest>().Serialize(ref writer, update.Request, options);
                break;
            case RequestKind.Secrets secrets:
                options.Resolver.GetFormatterWithVerify<SecretsRequest>().Serialize(ref writer, secrets.Request, options);
                break;
            case RequestKind.SendSecrets sendSecrets:
                options.Resolver.GetFormatterWithVerify<SendSecretsRequest>().Serialize(ref writer, sendSecrets.Request, options);
                break;
            case RequestKind.Version version:
                options.Resolver.GetFormatterWithVerify<VersionRequest>().Serialize(ref writer, version.Request, options);
                break;
            case RequestKind.DeleteAccount deleteAccount:
                options.Resolver.GetFormatterWithVerify<DeleteAccountRequest>().Serialize(ref writer, deleteAccount.Request, options);
                break;
            case RequestKind.AllowInvites allowInvites:
                options.Resolver.GetFormatterWithVerify<AllowInvitesRequest>().Serialize(ref writer, allowInvites.Request, options);
                break;
        }
    }

    public RequestKind Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) {
        if (reader.ReadMapHeader() != 1) {
            throw new MessagePackSerializationException("Invalid RequestKind");
        }

        var key = reader.ReadString();

        switch (key) {
            case "ping": {
                var request = options.Resolver.GetFormatterWithVerify<PingRequest>().Deserialize(ref reader, options);
                return new RequestKind.Ping(request);
            }
            case "authenticate": {
                var request = options.Resolver.GetFormatterWithVerify<AuthenticateRequest>().Deserialize(ref reader, options);
                return new RequestKind.Authenticate(request);
            }
            case "create": {
                var request = options.Resolver.GetFormatterWithVerify<CreateRequest>().Deserialize(ref reader, options);
                return new RequestKind.Create(request);
            }
            case "invite": {
                var request = options.Resolver.GetFormatterWithVerify<InviteRequest>().Deserialize(ref reader, options);
                return new RequestKind.Invite(request);
            }
            case "join": {
                var request = options.Resolver.GetFormatterWithVerify<JoinRequest>().Deserialize(ref reader, options);
                return new RequestKind.Join(request);
            }
            case "message": {
                var request = options.Resolver.GetFormatterWithVerify<MessageRequest>().Deserialize(ref reader, options);
                return new RequestKind.Message(request);
            }
            case "public_key": {
                var request = options.Resolver.GetFormatterWithVerify<PublicKeyRequest>().Deserialize(ref reader, options);
                return new RequestKind.PublicKey(request);
            }
            case "register": {
                var request = options.Resolver.GetFormatterWithVerify<RegisterRequest>().Deserialize(ref reader, options);
                return new RequestKind.Register(request);
            }
            case "list": {
                var request = options.Resolver.GetFormatterWithVerify<ListRequest>().Deserialize(ref reader, options);
                return new RequestKind.List(request);
            }
            case "leave": {
                var request = options.Resolver.GetFormatterWithVerify<LeaveRequest>().Deserialize(ref reader, options);
                return new RequestKind.Leave(request);
            }
            case "kick": {
                var request = options.Resolver.GetFormatterWithVerify<KickRequest>().Deserialize(ref reader, options);
                return new RequestKind.Kick(request);
            }
            case "disband": {
                var request = options.Resolver.GetFormatterWithVerify<DisbandRequest>().Deserialize(ref reader, options);
                return new RequestKind.Disband(request);
            }
            case "promote": {
                var request = options.Resolver.GetFormatterWithVerify<PromoteRequest>().Deserialize(ref reader, options);
                return new RequestKind.Promote(request);
            }
            case "update": {
                var request = options.Resolver.GetFormatterWithVerify<UpdateRequest>().Deserialize(ref reader, options);
                return new RequestKind.Update(request);
            }
            case "secrets": {
                var request = options.Resolver.GetFormatterWithVerify<SecretsRequest>().Deserialize(ref reader, options);
                return new RequestKind.Secrets(request);
            }
            case "send_secrets": {
                var request = options.Resolver.GetFormatterWithVerify<SendSecretsRequest>().Deserialize(ref reader, options);
                return new RequestKind.SendSecrets(request);
            }
            case "version": {
                var request = options.Resolver.GetFormatterWithVerify<VersionRequest>().Deserialize(ref reader, options);
                return new RequestKind.Version(request);
            }
            case "delete_account": {
                var request = options.Resolver.GetFormatterWithVerify<DeleteAccountRequest>().Deserialize(ref reader, options);
                return new RequestKind.DeleteAccount(request);
            }
            case "allow_invites": {
                var request = options.Resolver.GetFormatterWithVerify<AllowInvitesRequest>().Deserialize(ref reader, options);
                return new RequestKind.AllowInvites(request);
            }
            default:
                throw new MessagePackSerializationException("Invalid RequestKind");
        }
    }
}
