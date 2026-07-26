using System.Buffers;
using System.Net.WebSockets;
using ExtraChat.Protocol;
using MessagePack;

namespace ExtraChat;

public static class Ext {
    public static string ToHexString(this IEnumerable<byte> bytes) {
        return string.Join("", bytes.Select(b => b.ToString("x2")));
    }

    public static bool IsWhiteSpace(this ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
            if (!char.IsWhiteSpace((char)b))
                return false;

        return true;
    }

    public static async Task SendMessage(this ClientWebSocket client, RequestContainer request) {
        var bytes = MessagePackSerializer.Serialize(request);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.SendAsync(bytes, WebSocketMessageType.Binary, true, cts.Token);
    }

    public static async Task<ResponseContainer> ReceiveMessage(this ClientWebSocket client)
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

internal class WebSocketClosure(WebSocketCloseStatus closeStatus, string closeStatusDescription)
    : Exception($"WebSocket closed with code {closeStatus}: {closeStatusDescription}")
{
    public WebSocketCloseStatus CloseStatus => closeStatus;
    public string CloseStatusDescription => closeStatusDescription;
}
