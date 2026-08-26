using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using Dalamud.Game.Config;
using Dalamud.Game.Text;
using ExtraChat.Protocol;
using FFXIVClientStructs.FFXIV.Client.System.String;
using MessagePack;

namespace ExtraChat;

public static class Ext {
    public static string ToHexString(this IEnumerable<byte> bytes) {
        return string.Join("", bytes.Select(b => b.ToString("x2")));
    }
    
    extension(ReadOnlySpan<byte> span)
    {
        public bool IsWhiteSpace()
        {
            return Encoding.UTF8.GetString(span).Replace("\u3000", " ").IsWhiteSpace();
        }
    }

    extension(ClientWebSocket client)
    {
        public async Task SendMessage(RequestContainer request) {
            var bytes = MessagePackSerializer.Serialize(request);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.SendAsync(bytes, WebSocketMessageType.Binary, true, cts.Token);
        }

        public async Task<ResponseContainer> ReceiveMessage()
        {
            var bytes = ArrayPool<byte>.Shared.Rent(64 * 1024);
            var bytesSegment = new ArraySegment<byte>(bytes);

            WebSocketReceiveResult result;
            var i = 0;
            do {
                result = await client.ReceiveAsync(bytesSegment[i..], CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketClosure(result.CloseStatus!.Value, result.CloseStatusDescription!);
            
                i += result.Count;

                if (i >= bytesSegment.Count) {
                    throw new Exception();
                }
            } while (!result.EndOfMessage);

            var message = MessagePackSerializer.Deserialize<ResponseContainer>(bytesSegment[..i]);
        
            ArrayPool<byte>.Shared.Return(bytes);
            return message;
        }
    }

    public static unsafe Utf8String* ToUtf8String(this ReadOnlySpan<byte> input)
    {
        Utf8String* str;
        if (input[^1] != 0)
        {
            var replacement = ArrayPool<byte>.Shared.Rent(input.Length + 1);
            
            input.CopyTo(replacement);
            replacement[input.Length] = 0;
            str = Utf8String.FromSequence(replacement);
            
            ArrayPool<byte>.Shared.Return(replacement);
        }
        else
            str = Utf8String.FromSequence(input);

        return str;
    }

    extension(XivChatType chatType)
    {
        public bool IsLinkshell()
        {
            return chatType is XivChatType.CrossLinkShell1
                or >= XivChatType.CrossLinkShell2 and <= XivChatType.CrossLinkShell8
                or >= XivChatType.Ls1 and <= XivChatType.Ls8;
        }

        public UiConfigOption ToColorConfigOption() => chatType switch
        {
            XivChatType.Say => UiConfigOption.ColorSay,
            XivChatType.Shout => UiConfigOption.ColorShout,
            XivChatType.TellOutgoing => UiConfigOption.ColorTell,
            XivChatType.Party => UiConfigOption.ColorParty,
            XivChatType.Alliance => UiConfigOption.ColorAlliance,
            XivChatType.Yell => UiConfigOption.ColorYell,
            XivChatType.Ls1 => UiConfigOption.ColorLS1,
            XivChatType.Ls2 => UiConfigOption.ColorLS2,
            XivChatType.Ls3 => UiConfigOption.ColorLS3,
            XivChatType.Ls4 => UiConfigOption.ColorLS4,
            XivChatType.Ls5 => UiConfigOption.ColorLS5,
            XivChatType.Ls6 => UiConfigOption.ColorLS6,
            XivChatType.Ls7 => UiConfigOption.ColorLS7,
            XivChatType.Ls8 => UiConfigOption.ColorLS8,
            XivChatType.CrossLinkShell1 => UiConfigOption.ColorCWLS,
            XivChatType.CrossLinkShell2 => UiConfigOption.ColorCWLS2,
            XivChatType.CrossLinkShell3 => UiConfigOption.ColorCWLS3,
            XivChatType.CrossLinkShell4 => UiConfigOption.ColorCWLS4,
            XivChatType.CrossLinkShell5 => UiConfigOption.ColorCWLS5,
            XivChatType.CrossLinkShell6 => UiConfigOption.ColorCWLS6,
            XivChatType.CrossLinkShell7 => UiConfigOption.ColorCWLS7,
            XivChatType.CrossLinkShell8 => UiConfigOption.ColorCWLS8,
            XivChatType.FreeCompany => UiConfigOption.ColorFCompany,
            XivChatType.NoviceNetwork => UiConfigOption.ColorBeginner,
            XivChatType.NoviceNetworkSystem => UiConfigOption.ColorBeginnerAnnounce,
            XivChatType.CustomEmote => UiConfigOption.ColorEmoteUser,
            XivChatType.StandardEmote => UiConfigOption.ColorEmote,
            XivChatType.GainBuff => UiConfigOption.ColorBuffGive,
            XivChatType.Healing => UiConfigOption.ColorCureGive,
            XivChatType.GainDebuff => UiConfigOption.ColorDebuffGive,
            XivChatType.LootRoll => UiConfigOption.ColorLoot,
            XivChatType.FreeCompanyAnnouncement => UiConfigOption.ColorFCAnnounce,
            XivChatType.PvpTeamAnnouncement => UiConfigOption.ColorPvPGroupAnnounce,
            XivChatType.Damage => UiConfigOption.ColorAttackSuccess,
            XivChatType.Action => UiConfigOption.ColorAction,
            XivChatType.Item => UiConfigOption.ColorItem,
            XivChatType.Miss => UiConfigOption.ColorAttackFailure,
            XivChatType.Echo => UiConfigOption.ColorEcho,
            XivChatType.Crafting => UiConfigOption.ColorCraft,
            XivChatType.Gathering => UiConfigOption.ColorGathering,
            _ => UiConfigOption.ColorSay
        };
    }
}

internal class WebSocketClosure(WebSocketCloseStatus closeStatus, string closeStatusDescription)
    : Exception($"WebSocket closed with code {closeStatus}: {closeStatusDescription}")
{
    public WebSocketCloseStatus CloseStatus => closeStatus;
    public string CloseStatusDescription => closeStatusDescription;
}
