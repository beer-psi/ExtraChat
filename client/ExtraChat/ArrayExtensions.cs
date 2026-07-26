namespace ExtraChat;

internal static class ArrayExtensions {
    internal static byte[] Concat(this ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) {
        var result = new byte[a.Length + b.Length];
        
        a.CopyTo(result);
        b.CopyTo(result.AsSpan()[a.Length..]);

        return result;
    }
}
