using System.Buffers;
using System.Net.WebSockets;
using System.Text;
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

        public int IndexOfAny(ReadOnlySpan<byte> span1, ReadOnlySpan<byte> span2)
        {
            var idx = span.IndexOf(span1);

            return idx != -1 ? idx : span.IndexOf(span2);
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

    public static bool IsLinkshell(this XivChatType chatType)
    {
        return chatType is XivChatType.CrossLinkShell1
            or >= XivChatType.CrossLinkShell2 and <= XivChatType.CrossLinkShell8
            or >= XivChatType.Ls1 and <= XivChatType.Ls8;
    }
}

internal class WebSocketClosure(WebSocketCloseStatus closeStatus, string closeStatusDescription)
    : Exception($"WebSocket closed with code {closeStatus}: {closeStatusDescription}")
{
    public WebSocketCloseStatus CloseStatus => closeStatus;
    public string CloseStatusDescription => closeStatusDescription;
}
