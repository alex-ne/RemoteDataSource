using System.Net;
using System.Net.Sockets;
using SocketCommon;

namespace SocketServer
{
    public class ServerCore : IDisposable
    {
        public const int DiscoveryPort = SocketConstants.DiscoveryPort;

        private readonly string _serverId = Guid.NewGuid().ToString("N")[..8];
        private readonly int _tcpPort;

        private TcpListener? _tcpListener;
        private UdpClient? _udpListener;
        private CancellationTokenSource _cts = new();

        private readonly Dictionary<string, ClientSession> _sessions = new();
        private readonly object _sessionsLock = new();

        // Clients discovered via UDP (before TCP connect)
        private readonly List<string> _knownClients = new();
        private readonly object _clientsLock = new();

        public bool AutoReply { get; set; }

        public event Action<string>? LogMessage;
        public event Action<ClientSession>? ClientConnected;
        public event Action<ClientSession>? ClientDisconnected;
        public event Action<ClientSession, string>? MessageReceived;

        public ServerCore(int tcpPort = 0)
        {
            _tcpListener = new TcpListener(IPAddress.Any, tcpPort);
            _tcpListener.Start();
            _tcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
        }

        public int TcpPort => _tcpPort;

        public IReadOnlyDictionary<string, ClientSession> Sessions
        {
            get { lock (_sessionsLock) return new Dictionary<string, ClientSession>(_sessions); }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _ = RunUdpDiscoveryAsync(_cts.Token);
            _ = RunTcpAcceptAsync(_cts.Token);
            Log($"Сервер запущен. TCP:{_tcpPort}  UDP:{DiscoveryPort}");
        }

        public void Stop()
        {
            _cts.Cancel();
            lock (_sessionsLock)
            {
                foreach (var s in _sessions.Values) s.Dispose();
                _sessions.Clear();
            }
            Log("Сервер остановлен.");
        }

        // --- UDP discovery ---

        private async Task RunUdpDiscoveryAsync(CancellationToken ct)
        {
            try
            {
                _udpListener?.Dispose();
                _udpListener = new UdpClient();
                _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _udpListener.EnableBroadcast = true;

                Log($"Ожидание UDP-запросов на порту {DiscoveryPort}...");
                while (!ct.IsCancellationRequested)
                {
                    var result = await _udpListener.ReceiveAsync(ct);
                    _ = HandleDiscoveryRequestAsync(result, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"UDP ошибка: {ex.Message}"); }
            finally { _udpListener?.Dispose(); }
        }

        private async Task HandleDiscoveryRequestAsync(UdpReceiveResult result, CancellationToken ct)
        {
            try
            {
                var req = UdpHelper.DecodeRequest(result.Buffer);
                if (req == null) return;

                // Prefer explicit IP from request, fall back to UDP packet source
                var clientAddr = !string.IsNullOrEmpty(req.ClientIpAddress)
                    ? req.ClientIpAddress
                    : result.RemoteEndPoint.Address.ToString();

                lock (_clientsLock)
                {
                    if (!_knownClients.Contains(clientAddr))
                        _knownClients.Add(clientAddr);
                }

                Log($"Получен запрос на соединение от {clientAddr}");

                var resp = new DiscoveryResponse
                {
                    ServerId        = _serverId,
                    ServerHostName  = Dns.GetHostName(),
                    ServerIpAddress = GetLocalIpV4(),
                    TcpPort         = _tcpPort
                };

                var data = UdpHelper.EncodeResponse(resp);
                await _udpListener!.SendAsync(data, data.Length, result.RemoteEndPoint);
                Log($"Отправлен отклик клиенту {clientAddr}");
            }
            catch (Exception ex) { Log($"Ошибка обработки запроса: {ex.Message}"); }
        }

        // --- TCP accept ---

        private async Task RunTcpAcceptAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var tcp = await _tcpListener!.AcceptTcpClientAsync(ct);
                    _ = HandleClientAsync(tcp, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"TCP ошибка приёма: {ex.Message}"); }
        }

        private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
        {
            var ep = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
            var session = new ClientSession(tcp, ep);
            lock (_sessionsLock) _sessions[ep] = session;

            ClientConnected?.Invoke(session);

            try
            {
                var stream = tcp.GetStream();
                while (!ct.IsCancellationRequested && tcp.Connected)
                {
                    var msg = await MessageHelper.ReadMessageAsync(stream, ct);
                    if (msg == null) break;
                    await ProcessMessageAsync(session, stream, msg, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"Клиент {ep} ошибка: {ex.Message}"); }
            finally
            {
                lock (_sessionsLock) _sessions.Remove(ep);
                session.Dispose();
                ClientDisconnected?.Invoke(session);
                Log($"Клиент отключился: {ep}");
            }
        }

        private async Task ProcessMessageAsync(ClientSession session, Stream stream, SocketMessage msg, CancellationToken ct)
        {
            switch (msg.Type)
            {
                case MessageType.Hello:
                    Log($"Hello от {session.RemoteEndPoint}");
                    break;

                case MessageType.Text:
                    var tp = MessageHelper.DeserializePayload<TextPayload>(msg.Payload);
                    var text = tp?.Text ?? msg.Payload ?? string.Empty;
                    Log($"Сообщение от {session.RemoteEndPoint}: {text}");
                    MessageReceived?.Invoke(session, text);
                    if (AutoReply)
                    {
                        var replyPayload = MessageHelper.SerializePayload(new ReplyPayload
                        {
                            Text       = $"Ответ: {text}",
                            ReceivedAt = DateTime.Now
                        });
                        await SendToClientAsync(session,
                            new SocketMessage { Type = MessageType.Reply, Payload = replyPayload });
                    }
                    break;

                case MessageType.Reply:
                    var rp = MessageHelper.DeserializePayload<ReplyPayload>(msg.Payload);
                    if (rp != null)
                    {
                        var display = $"{rp.Text}  [получено клиентом в {rp.ReceivedAt:HH:mm:ss.fff}]";
                        Log($"Ответ от {session.RemoteEndPoint}: {display}");
                        MessageReceived?.Invoke(session, display);
                    }
                    break;

                case MessageType.Bye:
                    Log($"Bye от {session.RemoteEndPoint}");
                    break;
            }
        }

        public async Task SendToAllAsync(SocketMessage msg)
        {
            IEnumerable<ClientSession> sessions;
            lock (_sessionsLock) sessions = _sessions.Values.ToList();
            foreach (var s in sessions)
                await SendToClientAsync(s, msg);
        }

        public async Task SendToClientAsync(ClientSession session, SocketMessage msg)
        {
            try
            {
                var stream = session.TcpClient.GetStream();
                await MessageHelper.WriteMessageAsync(stream, msg);
            }
            catch (Exception ex) { Log($"Ошибка отправки: {ex.Message}"); }
        }

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

        private void Log(string text) =>
            LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {text}");

        public void Dispose()
        {
            Stop();
            _tcpListener?.Stop();
            _udpListener?.Dispose();
        }
    }

    public class ClientSession : IDisposable
    {
        public TcpClient TcpClient { get; }
        public string RemoteEndPoint { get; }
        public DateTime ConnectedAt { get; } = DateTime.Now;

        public ClientSession(TcpClient tcp, string ep)
        {
            TcpClient = tcp;
            RemoteEndPoint = ep;
        }

        public void Dispose() => TcpClient.Dispose();

        public override string ToString() => RemoteEndPoint;
    }
}
