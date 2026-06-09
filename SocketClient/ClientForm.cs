namespace SocketClient
{
    public class ClientForm : Form
    {
        private readonly List<ClientCore> _connections = new();

        // Row 1
        private readonly Button _btnRequest   = new() { Text = "Запрос соединения", Width = 150 };
        private readonly Label  _lblStatus    = new() { Text = "Ожидание", AutoSize = true };

        // Row 2
        private readonly ComboBox  _cmbServers   = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox   _txtMessage   = new() { Width = 220 };
        private readonly Button    _btnSend      = new() { Text = "Отправить сообщение", Width = 160, Enabled = false };
        private readonly CheckBox  _chkAutoReply = new() { Text = "Отвечать на сообщение", AutoSize = true };

        // Row 3
        private readonly Button _btnClearLog = new() { Text = "Очистить лог", Width = 110 };

        private readonly ListBox _lstEvents = new() { Dock = DockStyle.Fill };

        public ClientForm()
        {
            Text = "Socket Client";
            Size = new Size(740, 520);
            MinimumSize = new Size(500, 350);

            BuildLayout();

            _btnRequest.Click += OnRequestConnection;
            _btnSend.Click    += OnSendMessage;
            _btnClearLog.Click += (_, _) => _lstEvents.Items.Clear();
            _chkAutoReply.CheckedChanged += (_, _) =>
            {
                foreach (var c in _connections)
                    c.AutoReply = _chkAutoReply.Checked;
            };

            FormClosing += (_, _) =>
            {
                foreach (var c in _connections.ToList()) c.Dispose();
                _connections.Clear();
            };
        }

        private void BuildLayout()
        {
            // Three toolbar rows, each DockStyle.Top — processed top-down in Controls order
            var row1 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 4, 4, 2)
            };
            row1.Controls.AddRange(new Control[]
            {
                _btnRequest,
                new Label { Text = "  Статус: ", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
                _lblStatus
            });

            var row2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 2, 4, 2)
            };
            row2.Controls.AddRange(new Control[]
            {
                _cmbServers,
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
            // WinForms docks Controls[0] first — so visual top row must be Controls[0]
            Controls.Add(grpEvents);
            Controls.Add(row3);
            Controls.Add(row2);
            Controls.Add(row1);
        }

        private async void OnRequestConnection(object? s, EventArgs e)
        {
            _btnRequest.Enabled = false;
            _lblStatus.Text = "Поиск серверов...";

            // Dispose and remove any lingering disconnected clients before re-discovering
            var dead = _connections.Where(c => !c.IsConnected).ToList();
            foreach (var d in dead) { _connections.Remove(d); d.Dispose(); }

            AppendLog("Отправка широковещательного запроса...");

            try
            {
                var servers = await ClientCore.DiscoverAsync();
                AppendLog($"Получено откликов: {servers.Count}");

                if (servers.Count == 0)
                {
                    AppendLog("Серверы не найдены.");
                    _lblStatus.Text = "Серверы не найдены";
                    return;
                }

                foreach (var srv in servers)
                {
                    var client = new ClientCore();
                    client.AutoReply        = _chkAutoReply.Checked;
                    client.LogMessage       += AppendLog;
                    client.Connected        += ep => AppendLog($"Создано соединение с {ep}");
                    client.Disconnected     += () => SafeUI(() =>
                    {
                        _connections.Remove(client);
                        client.Dispose();
                        UpdateStatus();
                        RefreshServerList();
                    });
                    client.MessageReceived  += (ep, txt) => { /* already logged by ClientCore */ };
                    _connections.Add(client);

                    try
                    {
                        await client.ConnectAsync(srv.IpAddress, srv.Response.TcpPort);
                        RefreshServerList();
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Ошибка соединения с {srv.IpAddress}:{srv.Response.TcpPort} — {ex.Message}");
                    }
                }

                UpdateStatus();
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка: {ex.Message}");
                _lblStatus.Text = "Ошибка";
            }
            finally
            {
                _btnRequest.Enabled = true;
            }
        }

        private async void OnSendMessage(object? s, EventArgs e)
        {
            var text = _txtMessage.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var selected = _cmbServers.SelectedItem as ClientCore;
            if (selected == null || !selected.IsConnected)
            {
                AppendLog("Не выбран активный сервер.");
                return;
            }

            await selected.SendMessageAsync(text);
            _txtMessage.Clear();
        }

        private void RefreshServerList()
        {
            var prev = _cmbServers.SelectedItem as ClientCore;
            var active = _connections.Where(c => c.IsConnected).ToList();
            _cmbServers.Items.Clear();
            foreach (var c in active)
                _cmbServers.Items.Add(c);

            if (prev != null && _cmbServers.Items.Contains(prev))
                _cmbServers.SelectedItem = prev;
            else if (_cmbServers.Items.Count > 0)
                _cmbServers.SelectedIndex = 0;

            _btnSend.Enabled = _cmbServers.Items.Count > 0;
        }

        private void UpdateStatus()
        {
            var active = _connections.Count(c => c.IsConnected);
            _lblStatus.Text = active > 0 ? $"Подключено: {active}" : "Нет активных соединений";
        }

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
