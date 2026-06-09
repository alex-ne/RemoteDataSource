using System.Net;
using System.Net.Sockets;
using SocketCommon;

namespace SocketClient
{
    /// <summary>
    /// One instance per server connection.
    /// </summary>
    public class ClientCore : IDisposable
    {
        private readonly string _clientId = Guid.NewGuid().ToString("N")[..8];
        private TcpClient? _tcp;
        private CancellationTokenSource _cts = new();

        public string ServerEndPoint { get; private set; } = string.Empty;
        public bool IsConnected => _tcp?.Connected == true;
        public bool AutoReply { get; set; }

        public event Action<string>? LogMessage;
        public event Action? Disconnected;
        public event Action<string>? Connected;               // fires after TCP handshake
        public event Action<string, string>? MessageReceived; // (serverEndPoint, displayText)

        // --- Discovery ---

        public static async Task<List<DiscoveredServer>> DiscoverAsync(
            int port = SocketConstants.DiscoveryPort,
            int timeoutMs = 2000)
        {
            var results = new Dictionary<string, DiscoveredServer>();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            udp.EnableBroadcast = true;

            var req = new DiscoveryRequest
            {
                ClientId        = Guid.NewGuid().ToString("N")[..8],
                ClientHostName  = Dns.GetHostName(),
                ClientIpAddress = GetLocalIpV4()
            };
            var data = UdpHelper.EncodeRequest(req);

            foreach (var addr in GetBroadcastAddresses())
            {
                try { await udp.SendAsync(data, data.Length, new IPEndPoint(addr, port)); }
                catch { }
            }
            try { await udp.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, port)); }
            catch { }

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(cts.Token);
                    var resp = UdpHelper.DecodeResponse(result.Buffer);
                    if (resp == null) continue;
                    var ip = !string.IsNullOrEmpty(resp.ServerIpAddress)
                        ? resp.ServerIpAddress
                        : result.RemoteEndPoint.Address.ToString();
                    results[resp.ServerId] = new DiscoveredServer { Response = resp, IpAddress = ip };
                }
            }
            catch (OperationCanceledException) { }

            return results.Values.ToList();
        }

        // --- TCP connection ---

        public async Task ConnectAsync(string host, int tcpPort)
        {
            _cts = new CancellationTokenSource();
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(host, tcpPort, _cts.Token);
            ServerEndPoint = $"{host}:{tcpPort}";

            var stream = _tcp.GetStream();
            await MessageHelper.WriteMessageAsync(stream, new SocketMessage { Type = MessageType.Hello }, _cts.Token);

            // Fire Connected — caller (UI thread) logs this reliably
            Connected?.Invoke(ServerEndPoint);

            _ = ReceiveLoopAsync(stream, _cts.Token);
        }

        public void Disconnect()
        {
            _cts.Cancel();
            _tcp?.Close();
        }

        public async Task SendMessageAsync(string text)
        {
            if (_tcp == null || !_tcp.Connected) return;
            var stream = _tcp.GetStream();
            var payload = MessageHelper.SerializePayload(new TextPayload { Text = text });
            await MessageHelper.WriteMessageAsync(stream,
                new SocketMessage { Type = MessageType.Text, Payload = payload }, _cts.Token);
            Log($"Отправлено на {ServerEndPoint}: {text}");
        }

        private async Task SendReplyAsync(string originalText)
        {
            if (_tcp == null || !_tcp.Connected) return;
            var stream = _tcp.GetStream();
            var payload = MessageHelper.SerializePayload(new ReplyPayload
            {
                Text       = $"Ответ: {originalText}",
                ReceivedAt = DateTime.Now
            });
            await MessageHelper.WriteMessageAsync(stream,
                new SocketMessage { Type = MessageType.Reply, Payload = payload }, _cts.Token);
        }

        private async Task ReceiveLoopAsync(Stream stream, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await MessageHelper.ReadMessageAsync(stream, ct);
                    if (msg == null) break;
                    await ProcessMessageAsync(msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"Ошибка приёма от {ServerEndPoint}: {ex.Message}"); }
            finally
            {
                Disconnected?.Invoke();
                Log($"Соединение с {ServerEndPoint} закрыто.");
            }
        }

        private async Task ProcessMessageAsync(SocketMessage msg)
        {
            switch (msg.Type)
            {
                case MessageType.Hello:
                    Log($"Hello от сервера {ServerEndPoint}");
                    break;

                case MessageType.Text:
                    var tp = MessageHelper.DeserializePayload<TextPayload>(msg.Payload);
                    var text = tp?.Text ?? msg.Payload ?? string.Empty;
                    Log($"Сообщение от {ServerEndPoint}: {text}");
                    MessageReceived?.Invoke(ServerEndPoint, text);
                    if (AutoReply)
                        await SendReplyAsync(text);
                    break;

                case MessageType.Reply:
                    var rp = MessageHelper.DeserializePayload<ReplyPayload>(msg.Payload);
                    if (rp != null)
                    {
                        var display = $"{rp.Text}  [получено сервером в {rp.ReceivedAt:HH:mm:ss.fff}]";
                        Log($"Ответ от {ServerEndPoint}: {display}");
                        MessageReceived?.Invoke(ServerEndPoint, display);
                    }
                    break;

                case MessageType.Bye:
                    Log($"Сервер {ServerEndPoint} закрыл соединение.");
                    break;
            }
        }

        private void Log(string text) =>
            LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {text}");

        private static string GetLocalIpV4()
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                s.Connect("8.8.8.8", 65530);
                return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
            }
            catch { return "127.0.0.1"; }
        }

        private static List<IPAddress> GetBroadcastAddresses()
        {
            var result = new List<IPAddress>();
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var addr = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        bcast[i] = (byte)(addr[i] | ~mask[i]);
                    result.Add(new IPAddress(bcast));
                }
            }
            return result;
        }

        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _tcp?.Dispose();
        }

        public override string ToString() => ServerEndPoint;
    }
}
