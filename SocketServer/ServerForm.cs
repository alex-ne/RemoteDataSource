using SocketCommon;

namespace SocketServer
{
    public class ServerForm : Form
    {
        private ServerCore? _server;

        // Row 1
        private readonly Button _btnListen  = new() { Text = "Слушать", Width = 90 };
        private readonly Button _btnStop    = new() { Text = "Стоп",    Width = 70, Enabled = false };
        private readonly Label  _lblStatus  = new() { Text = "Остановлен", AutoSize = true };

        // Row 2
        private readonly TextBox   _txtMessage   = new() { Width = 280 };
        private readonly Button    _btnSend      = new() { Text = "Отправить сообщение", Width = 160, Enabled = false };
        private readonly CheckBox  _chkAutoReply = new() { Text = "Отвечать на сообщение", AutoSize = true };

        // Row 3
        private readonly Button _btnClearLog = new() { Text = "Очистить лог", Width = 110 };

        private readonly ListBox _lstEvents = new() { Dock = DockStyle.Fill };

        public ServerForm()
        {
            Text = "Socket Server";
            Size = new Size(700, 520);
            MinimumSize = new Size(500, 350);

            BuildLayout();

            _btnListen.Click   += OnListen;
            _btnStop.Click     += OnStop;
            _btnSend.Click     += OnSendMessage;
            _btnClearLog.Click += (_, _) => _lstEvents.Items.Clear();
            _chkAutoReply.CheckedChanged += (_, _) =>
            {
                if (_server != null) _server.AutoReply = _chkAutoReply.Checked;
            };

            FormClosing += (_, _) => _server?.Stop();
        }

        private void BuildLayout()
        {
            var row1 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 4, 4, 2)
            };
            row1.Controls.AddRange(new Control[]
            {
                _btnListen, _btnStop,
                new Label { Text = "  Статус: ", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
                _lblStatus
            });

            var row2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 2, 4, 2)
            };
            row2.Controls.AddRange(new Control[]
            {
                _txtMessage,
                _btnSend,
                new Label { Width = 8 },
                _chkAutoReply
            });

            var row3 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 2, 4, 4)
            };
            row3.Controls.Add(_btnClearLog);

            var grpEvents = new GroupBox { Text = "События", Dock = DockStyle.Fill };
            grpEvents.Controls.Add(_lstEvents);

            // Add Fill first, then Top rows in reverse visual order
            Controls.Add(grpEvents);
            Controls.Add(row3);
            Controls.Add(row2);
            Controls.Add(row1);
        }

        private void OnListen(object? s, EventArgs e)
        {
            _server = new ServerCore();
            _server.AutoReply            = _chkAutoReply.Checked;
            _server.LogMessage           += AppendLog;
            _server.ClientConnected      += OnClientConnected;
            _server.ClientDisconnected   += OnClientDisconnected;
            _server.MessageReceived      += OnMessageReceived;
            _server.Start();

            _btnListen.Enabled = false;
            _btnStop.Enabled   = true;
            _btnSend.Enabled   = true;
            _lblStatus.Text    = $"Слушаю  TCP:{_server.TcpPort}";
        }

        private void OnStop(object? s, EventArgs e)
        {
            _server?.Stop();
            _server = null;

            _btnListen.Enabled = true;
            _btnStop.Enabled   = false;
            _btnSend.Enabled   = false;
            _lblStatus.Text    = "Остановлен";
        }

        private async void OnSendMessage(object? s, EventArgs e)
        {
            if (_server == null) return;
            var text = _txtMessage.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var payload = MessageHelper.SerializePayload(new TextPayload { Text = text });
            await _server.SendToAllAsync(new SocketMessage { Type = MessageType.Text, Payload = payload });
            AppendLog($"Отправлено всем клиентам: {text}");
            _txtMessage.Clear();
        }

        private void OnClientConnected(ClientSession session) =>
            AppendLog($"TCP-соединение установлено с {session.RemoteEndPoint}");

        private void OnClientDisconnected(ClientSession session) =>
            AppendLog($"TCP-соединение закрыто: {session.RemoteEndPoint}");

        private void OnMessageReceived(ClientSession session, string text) =>
            AppendLog($"[{session.RemoteEndPoint}] {text}");

        private void AppendLog(string msg) => SafeUI(() =>
        {
            _lstEvents.Items.Add(msg);
            _lstEvents.TopIndex = _lstEvents.Items.Count - 1;
        });

        private void SafeUI(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
    }
}
