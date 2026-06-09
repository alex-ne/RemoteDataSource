namespace SocketCommon
{
    public static class SocketConstants
    {
        public const int DiscoveryPort = 45678;
    }

    // UDP discovery broadcast sent by client
    public class DiscoveryRequest
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientHostName { get; set; } = string.Empty;
        public string ClientIpAddress { get; set; } = string.Empty;
    }

    // UDP response sent by server back to client (unicast)
    public class DiscoveryResponse
    {
        public string ServerId { get; set; } = string.Empty;
        public string ServerHostName { get; set; } = string.Empty;
        public string ServerIpAddress { get; set; } = string.Empty;  // IPv4 of the server
        public int TcpPort { get; set; }
    }

    // Discovered server (client-side only, not sent over network)
    public class DiscoveredServer
    {
        public DiscoveryResponse Response { get; set; } = new();
        public string IpAddress { get; set; } = string.Empty;
    }

    // Wrapper for all TCP messages
    public class SocketMessage
    {
        public MessageType Type { get; set; }
        public string? Payload { get; set; }  // JSON-serialized body
    }

    public enum MessageType
    {
        Hello,
        RemoteDataSourceList,
        RemoteDataSourceRequest,
        Text,
        Reply,
        Bye
    }

    // Payload for MessageType.Text
    public class TextPayload
    {
        public string Text { get; set; } = string.Empty;
    }

    // Payload for MessageType.Reply — includes timestamp of when the original message was received
    public class ReplyPayload
    {
        public string Text        { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; }
    }

    // Serializable DTO for RemoteDataSource (Bitmap → base64 PNG)
    public class RemoteDataSourceDto
    {
        public Guid ID { get; set; }
        public int Location { get; set; }   // SourceLocation enum value
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? IconBase64 { get; set; }  // PNG bytes in base64
        public string IpAddress { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
    }
}
