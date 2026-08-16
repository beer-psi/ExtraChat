namespace ExtraChat.Util;

internal static class PayloadUtil {
    internal static byte[] CreateTagPayload(Guid id)
    {
        var bytes = new byte[21];

        bytes[0] = 0x02; // start byte
        
        // https://github.com/Infiziert90/ChatTwo/blob/1a5c92880b8b10d41ff130b48398151993c6c205/ChatTwo/Message.cs#L128-L129
        bytes[1] = 0x27; // interactable
        bytes[2] = 18; // chunk length (3 bytes + data length - 1)
        bytes[3] = 0x20; // embedded info type (custom)
        
        Array.Copy(id.ToByteArray(), 0, bytes, 4, 16);

        bytes[20] = 0x03; // end byte

        return bytes;
    }
}
