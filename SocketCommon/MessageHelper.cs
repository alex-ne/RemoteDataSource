using System.Text;
using System.Text.Json;

namespace SocketCommon
{
    public static class MessageHelper
    {
        private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

        // Framing: 4-byte length prefix (little-endian) + UTF-8 JSON body
        public static byte[] Encode(SocketMessage msg)
        {
            var json = JsonSerializer.Serialize(msg, _json);
            var body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[4 + body.Length];
            BitConverter.TryWriteBytes(frame, body.Length);
            body.CopyTo(frame, 4);
            return frame;
        }

        public static SocketMessage? Decode(byte[] frame)
        {
            if (frame.Length < 4) return null;
            var json = Encoding.UTF8.GetString(frame, 4, frame.Length - 4);
            return JsonSerializer.Deserialize<SocketMessage>(json, _json);
        }

        // Read exactly one framed message from a NetworkStream
        public static async Task<SocketMessage?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
        {
            var lenBuf = new byte[4];
            if (!await ReadExactAsync(stream, lenBuf, 0, 4, ct)) return null;
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0 || len > 4 * 1024 * 1024) return null;
            var body = new byte[len];
            if (!await ReadExactAsync(stream, body, 0, len, ct)) return null;
            var json = Encoding.UTF8.GetString(body);
            return JsonSerializer.Deserialize<SocketMessage>(json, _json);
        }

        public static async Task WriteMessageAsync(Stream stream, SocketMessage msg, CancellationToken ct = default)
        {
            var frame = Encode(msg);
            await stream.WriteAsync(frame, ct);
        }

        public static string SerializePayload<T>(T obj) =>
            JsonSerializer.Serialize(obj, _json);

        public static T? DeserializePayload<T>(string? json) =>
            json == null ? default : JsonSerializer.Deserialize<T>(json, _json);

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buf, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buf.AsMemory(offset + read, count - read), ct);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }
    }

    // UDP packet helpers (simple JSON, no framing needed for small UDP datagrams)
    public static class UdpHelper
    {
        private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

        public static byte[] EncodeRequest(DiscoveryRequest req) =>
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req, _json));

        public static DiscoveryRequest? DecodeRequest(byte[] data) =>
            JsonSerializer.Deserialize<DiscoveryRequest>(Encoding.UTF8.GetString(data), _json);

        public static byte[] EncodeResponse(DiscoveryResponse resp) =>
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resp, _json));

        public static DiscoveryResponse? DecodeResponse(byte[] data) =>
            JsonSerializer.Deserialize<DiscoveryResponse>(Encoding.UTF8.GetString(data), _json);
    }
}
