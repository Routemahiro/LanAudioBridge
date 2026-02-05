using System.Net.NetworkInformation;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LanMicBridge;

partial class Form1
{
    private void BuildUi()
    {
        Text = "LanMicBridge";
        MinimumSize = new Size(720, 420);
        Size = new Size(780, 460);
        StartPosition = FormStartPosition.CenterScreen;

        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        Controls.Add(mainLayout);

        var modePanel = new Panel { Dock = DockStyle.Fill };
        _radioReceiver = new RadioButton
        {
            Appearance = Appearance.Button,
            Text = "受信（B）",
            AutoSize = false,
            Width = 160,
            Height = 40,
            Location = new Point(20, 10),
            Checked = true
        };
        _radioReceiver.CheckedChanged += ModeChanged;
        _radioSender = new RadioButton
        {
            Appearance = Appearance.Button,
            Text = "送信（A）",
            AutoSize = false,
            Width = 160,
            Height = 40,
            Location = new Point(190, 10)
        };
        _radioSender.CheckedChanged += ModeChanged;
        modePanel.Controls.Add(_radioReceiver);
        modePanel.Controls.Add(_radioSender);
        mainLayout.Controls.Add(modePanel, 0, 0);

        var contentPanel = new Panel { Dock = DockStyle.Fill };
        mainLayout.Controls.Add(contentPanel, 0, 1);

        _receiverPanel = BuildReceiverPanel();
        _senderPanel = BuildSenderPanel();
        contentPanel.Controls.Add(_receiverPanel);
        contentPanel.Controls.Add(_senderPanel);
        _senderPanel.Visible = false;

        _statusStrip = new StatusStrip { Dock = DockStyle.Fill };
        _statusLabel = new ToolStripStatusLabel { Text = "待受中", Spring = true };
        _alertLabel = new ToolStripStatusLabel { Text = "", ForeColor = Color.DarkRed, Visible = false };
        _restartButton = new ToolStripButton
        {
            Text = "再起動",
            Visible = false
        };
        _restartButton.Click += (_, _) => RestartApplication();
        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Items.Add(_alertLabel);
        _statusStrip.Items.Add(_restartButton);
        mainLayout.Controls.Add(_statusStrip, 0, 2);

        _statsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statsTimer.Tick += (_, _) => UpdateStatsLabel();
        _receiverStatusTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _receiverStatusTimer.Tick += (_, _) => UpdateReceiverStatus();
        _silenceTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _silenceTimer.Tick += (_, _) => EnsureSilence();

        Load += Form1_Load;
        FormClosing += Form1_FormClosing;
    }
    private Panel BuildReceiverPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(layout);

        layout.Controls.Add(new Label { Text = "このPCのIP (IPv4)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        var ipPanel = new Panel { Dock = DockStyle.Fill };
        _lblIpList = new Label
        {
            AutoSize = false,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _btnCopyIp = new Button
        {
            Text = "コピー",
            Width = 80,
            Height = 26,
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        _btnCopyIp.Click += (_, _) => CopyIpToClipboard();
        ipPanel.Controls.Add(_lblIpList);
        ipPanel.Controls.Add(_btnCopyIp);
        _btnCopyIp.Location = new Point(ipPanel.Width - _btnCopyIp.Width - 4, 4);
        ipPanel.Resize += (_, _) => _btnCopyIp.Location = new Point(ipPanel.Width - _btnCopyIp.Width - 4, 4);
        layout.Controls.Add(ipPanel, 1, 0);

        layout.Controls.Add(new Label { Text = "待受ポート", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _lblPort = new Label { Text = DefaultPort.ToString(), AutoSize = true, Anchor = AnchorStyles.Left };
        layout.Controls.Add(_lblPort, 1, 1);

        layout.Controls.Add(new Label { Text = "VB-CABLE 状態", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _lblCableStatus = new Label { Text = "確認中...", AutoSize = true, Anchor = AnchorStyles.Left };
        layout.Controls.Add(_lblCableStatus, 1, 2);

        _lblCableGuide = new Label
        {
            Text = "",
            AutoSize = false,
            Dock = DockStyle.Fill
        };
        layout.SetColumnSpan(_lblCableGuide, 2);
        layout.Controls.Add(_lblCableGuide, 0, 3);

        _linkReceiverDetail = new LinkLabel { Text = "詳細設定を開く", AutoSize = true, Anchor = AnchorStyles.Left };
        _linkReceiverDetail.Click += (_, _) => OpenSettingsTab(0);
        layout.Controls.Add(_linkReceiverDetail, 0, 4);
        layout.SetColumnSpan(_linkReceiverDetail, 2);

        _groupReceiverDetail = new GroupBox
        {
            Text = "詳細設定",
            Dock = DockStyle.Top,
            Visible = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        var detailLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(8)
        };
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 出力デバイス
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // ジッタバッファ
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 音声処理
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));  // 出力ゲイン
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));  // 強制再生待ち
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));  // チェック音
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 情報
        _groupReceiverDetail.Controls.Add(detailLayout);
        detailLayout.Controls.Add(new Label { Text = "出力デバイス", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _comboOutputDevice = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _comboOutputDevice.SelectedIndexChanged += (_, _) => ApplyOutputDeviceSelection();
        detailLayout.Controls.Add(_comboOutputDevice, 1, 0);
        detailLayout.Controls.Add(new Label { Text = "ジッタバッファ", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _comboJitter = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _comboJitter.Items.AddRange(new object[] { "Low latency", "Stable", "Ultra stable" });
        _comboJitter.SelectedIndex = 0;
        _comboJitter.SelectedIndexChanged += (_, _) => RestartOutputForJitter();
        detailLayout.Controls.Add(_comboJitter, 1, 1);
        detailLayout.Controls.Add(new Label { Text = "音声処理", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _chkRecvProcessing = new CheckBox { Text = "AGC/ゲート/クリップ有効", Dock = DockStyle.Fill, Checked = true };
        _chkRecvProcessing.CheckedChanged += (_, _) => _enableRecvProcessing = _chkRecvProcessing.Checked;
        detailLayout.Controls.Add(_chkRecvProcessing, 1, 2);
        detailLayout.Controls.Add(new Label
        {
            Text = "出力ゲイン",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 4, 0, 0)
        }, 0, 3);
        var gainPanel = new Panel { Dock = DockStyle.Fill };
        _trackOutputGain = new TrackBar { Minimum = 25, Maximum = 1000, Value = 100, TickFrequency = 25, Dock = DockStyle.Fill };
        _trackOutputGain.Scroll += (_, _) => UpdateOutputGain();
        _lblOutputGainValue = new Label { Text = "100%", AutoSize = true, Dock = DockStyle.Right };
        gainPanel.Controls.Add(_trackOutputGain);
        gainPanel.Controls.Add(_lblOutputGainValue);
        detailLayout.Controls.Add(gainPanel, 1, 3);
        UpdateOutputGain();
        detailLayout.Controls.Add(new Label
        {
            Text = "強制再生待ち",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 4, 0, 0)
        }, 0, 4);
        var forcePanel = new Panel { Dock = DockStyle.Fill };
        _trackOutputForceStart = new TrackBar { Minimum = 5, Maximum = 10, Value = 10, TickFrequency = 1, Dock = DockStyle.Fill };
        _trackOutputForceStart.Scroll += (_, _) => UpdateOutputForceStart();
        _lblOutputForceStartValue = new Label { Text = "1.0s", AutoSize = true, Dock = DockStyle.Right };
        forcePanel.Controls.Add(_trackOutputForceStart);
        forcePanel.Controls.Add(_lblOutputForceStartValue);
        detailLayout.Controls.Add(forcePanel, 1, 4);
        UpdateOutputForceStart();

        _btnCheckTone = new Button { Text = "チェック音を鳴らす", Width = 160, Height = 30, Anchor = AnchorStyles.Left };
        _btnCheckTone.Click += BtnCheckTone_Click;
        _lblCheckResult = new Label { Text = "結果: -", AutoSize = true, Anchor = AnchorStyles.Left };
        detailLayout.Controls.Add(_btnCheckTone, 0, 5);
        detailLayout.Controls.Add(_lblCheckResult, 1, 5);

        _receiverInfoGroup = new GroupBox
        {
            Text = "情報",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        var infoLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1
        };
        infoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _receiverInfoGroup.Controls.Add(infoLayout);

        _lblMeterGuide = new Label
        {
            Text = "音量が0付近: 送信停止 / IP / Firewallを確認\n音量が動くのに拾えない: 入力アプリのマイク入力を CABLE Output に設定",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        infoLayout.Controls.Add(_lblMeterGuide, 0, 0);

        _lblMeterWarning = new Label { Text = "", AutoSize = true, Anchor = AnchorStyles.Left };
        infoLayout.Controls.Add(_lblMeterWarning, 0, 1);

        _lblMeterA = new Label { Text = "音量: Peak -∞ dBFS / RMS -∞ dBFS", AutoSize = true, Anchor = AnchorStyles.Left };
        infoLayout.Controls.Add(_lblMeterA, 0, 2);

        _lblOutputLevel = new Label { Text = "出力後レベル: Peak -∞ dBFS / RMS -∞ dBFS", AutoSize = true, Anchor = AnchorStyles.Left };
        infoLayout.Controls.Add(_lblOutputLevel, 0, 3);

        _lblStats = new Label { Text = "Packets: 0  Loss: 0%  Jitter: 0ms  Delay: 0ms", AutoSize = true, Anchor = AnchorStyles.Left };
        infoLayout.Controls.Add(_lblStats, 0, 4);

        // 詳細設定と情報は別ウィンドウに移動するため、ここでは追加しない

        return panel;
    }

    private Panel BuildSenderPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(layout);

        layout.Controls.Add(new Label { Text = "受信側IPアドレス", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _txtIp = new TextBox { Dock = DockStyle.Fill };
        layout.Controls.Add(_txtIp, 1, 0);

        _btnSenderToggle = new Button { Text = "開始", Width = 120, Height = 30, Anchor = AnchorStyles.Left };
        _btnSenderToggle.Click += BtnSenderToggle_Click;
        _lblSenderStatus = new Label { Text = "待機中", AutoSize = true, Anchor = AnchorStyles.Left };
        layout.Controls.Add(_btnSenderToggle, 0, 1);
        layout.Controls.Add(_lblSenderStatus, 1, 1);

        _linkSenderDetail = new LinkLabel { Text = "詳細設定を開く", AutoSize = true, Anchor = AnchorStyles.Left };
        _linkSenderDetail.Click += (_, _) => OpenSettingsTab(1);
        layout.Controls.Add(_linkSenderDetail, 0, 2);
        layout.SetColumnSpan(_linkSenderDetail, 2);

        _groupSenderDetail = new GroupBox
        {
            Text = "詳細設定",
            Dock = DockStyle.Top,
            Visible = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        var detailLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(8)
        };
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _groupSenderDetail.Controls.Add(detailLayout);
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        detailLayout.Controls.Add(new Label { Text = "入力方式", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _comboCaptureApi = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _comboCaptureApi.Items.AddRange(new object[] { "WASAPI", "MME (互換)" });
        _comboCaptureApi.SelectedIndex = 0;
        _comboCaptureApi.SelectedIndexChanged += (_, _) =>
        {
            StopSender();
            RefreshCaptureDeviceList();
        };
        detailLayout.Controls.Add(_comboCaptureApi, 1, 0);
        detailLayout.Controls.Add(new Label { Text = "マイクデバイス", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _comboMicDevice = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        detailLayout.Controls.Add(_comboMicDevice, 1, 1);
        detailLayout.Controls.Add(new Label { Text = "送信品質", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _comboQuality = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _comboQuality.Items.AddRange(new object[] { "低", "標準", "高", "超高" });
        _comboQuality.SelectedIndex = 3;
        _comboQuality.SelectedIndexChanged += (_, _) => ApplyQualitySelection();
        _comboQuality.Enabled = true;
        detailLayout.Controls.Add(_comboQuality, 1, 2);
        ApplyQualitySelection();
        detailLayout.Controls.Add(new Label { Text = "送信方式", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _comboSendMode = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _comboSendMode.Items.AddRange(new object[] { "Opus (推奨)", "PCM直送(テスト)" });
        _comboSendMode.SelectedIndex = 0;
        _comboSendMode.SelectedIndexChanged += (_, _) =>
        {
            _sendPcmDirect = _comboSendMode.SelectedIndex == 1;
            _comboQuality.Enabled = !_sendPcmDirect;
        };
        _sendPcmDirect = _comboSendMode.SelectedIndex == 1;
        detailLayout.Controls.Add(_comboSendMode, 1, 3);
        detailLayout.Controls.Add(new Label { Text = "送信ゲイン", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        var gainPanel = new Panel { Dock = DockStyle.Fill };
        _trackGain = new TrackBar { Minimum = 25, Maximum = 400, Value = 100, TickFrequency = 25, Dock = DockStyle.Fill };
        _trackGain.Scroll += (_, _) => UpdateGain();
        _lblGainValue = new Label { Text = "100%", AutoSize = true, Dock = DockStyle.Right };
        gainPanel.Controls.Add(_trackGain);
        gainPanel.Controls.Add(_lblGainValue);
        detailLayout.Controls.Add(gainPanel, 1, 4);
        UpdateGain();
        detailLayout.Controls.Add(new Label { Text = "送信開始閾値", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        var vadPanel = new Panel { Dock = DockStyle.Fill };
        _trackVadThreshold = new TrackBar { Minimum = 30, Maximum = 90, Value = 45, TickFrequency = 5, Dock = DockStyle.Fill };
        _trackVadThreshold.Scroll += (_, _) => UpdateVadThreshold();
        _lblVadThresholdValue = new Label { Text = "-45 dBFS", AutoSize = true, Dock = DockStyle.Right };
        vadPanel.Controls.Add(_trackVadThreshold);
        vadPanel.Controls.Add(_lblVadThresholdValue);
        detailLayout.Controls.Add(vadPanel, 1, 5);
        UpdateVadThreshold();
        detailLayout.Controls.Add(new Label { Text = "音声処理", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        _chkSendProcessing = new CheckBox { Text = "AGC/ゲート/クリップ有効", Dock = DockStyle.Fill, Checked = true };
        _chkSendProcessing.CheckedChanged += (_, _) => _enableSendProcessing = _chkSendProcessing.Checked;
        detailLayout.Controls.Add(_chkSendProcessing, 1, 6);
        detailLayout.Controls.Add(new Label { Text = "送信テスト音", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 7);
        _chkSendTestTone = new CheckBox { Text = "1kHzサイン送出", Dock = DockStyle.Fill, Checked = false };
        _chkSendTestTone.CheckedChanged += (_, _) => _sendTestTone = _chkSendTestTone.Checked;
        detailLayout.Controls.Add(_chkSendTestTone, 1, 7);
        detailLayout.Controls.Add(new Label { Text = "送信入力レベル", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 8);
        _lblSenderMeterDetail = new Label { Text = "Peak -∞ dBFS / RMS -∞ dBFS", AutoSize = true, Anchor = AnchorStyles.Left };
        detailLayout.Controls.Add(_lblSenderMeterDetail, 1, 8);

        // 詳細設定は別ウィンドウに移動するため、ここでは追加しない

        return panel;
    }

    private void ModeChanged(object? sender, EventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        if (_radioReceiver.Checked)
        {
            UpdateModeUi(true);
        }
        else if (_radioSender.Checked)
        {
            UpdateModeUi(false);
        }
    }

    private void UpdateModeUi(bool receiverMode)
    {
        _receiverPanel.Visible = receiverMode;
        _senderPanel.Visible = !receiverMode;

        if (receiverMode)
        {
            StopSender();
            if (_receiverUdp == null)
            {
                AppLogger.Init("Receiver");
                StartReceiver();
            }
        }
        else
        {
            StopReceiver();
            AppLogger.Init("Sender");
        }
    }
    private void UpdateIpList()
    {
        var list = GetLocalIpv4Addresses();
        _lblIpList.Text = list.Count == 0 ? "-" : string.Join(Environment.NewLine, list);
    }

    private void CopyIpToClipboard()
    {
        var list = GetLocalIpv4Addresses();
        if (list.Count == 0)
        {
            return;
        }

        Clipboard.SetText(list[0]);
    }

    private static List<string> GetLocalIpv4Addresses()
    {
        var results = new List<(int Score, string Ip)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var props = ni.GetIPProperties();
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var ip = addr.Address.ToString();
                if (ip.StartsWith("169.254.", StringComparison.Ordinal))
                {
                    continue;
                }

                var score = 0;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                {
                    score += 50;
                }
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    score += 40;
                }
                if (!ip.StartsWith("127.", StringComparison.Ordinal))
                {
                    score += 10;
                }
                results.Add((score, ip));
            }
        }

        return results
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Ip, StringComparer.Ordinal)
            .Select(item => item.Ip)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
    }

    private void RefreshDeviceLists()
    {
        _deviceEnumerator?.Dispose();
        _deviceEnumerator = new MMDeviceEnumerator();
        _renderDevices.Clear();
        _captureDevices.Clear();
        _comboOutputDevice.Items.Clear();

        foreach (var device in _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            _renderDevices.Add(device);
            _comboOutputDevice.Items.Add(device.FriendlyName);
        }

        foreach (var device in _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            _captureDevices.Add(device);
        }

        var recommendedOutput = FindCableInputIndex();
        if (_renderDevices.Count > 0)
        {
            _comboOutputDevice.SelectedIndex = recommendedOutput >= 0 ? recommendedOutput : 0;
        }

        RefreshCaptureDeviceList();

        UpdateCableStatus();
    }

    private void RefreshCaptureDeviceList()
    {
        if (_comboMicDevice == null)
        {
            return;
        }

        _comboMicDevice.Items.Clear();
        _mmeDevices.Clear();

        if (_comboCaptureApi.SelectedIndex == 1)
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                _mmeDevices.Add(caps);
                _comboMicDevice.Items.Add(caps.ProductName);
            }
        }
        else
        {
            foreach (var device in _captureDevices)
            {
                _comboMicDevice.Items.Add(device.FriendlyName);
            }
        }

        if (_comboMicDevice.Items.Count > 0)
        {
            _comboMicDevice.SelectedIndex = 0;
        }
    }

    private int FindCableInputIndex()
    {
        for (var i = 0; i < _renderDevices.Count; i++)
        {
            var name = _renderDevices[i].FriendlyName ?? string.Empty;
            if (name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindCableOutputIndex()
    {
        for (var i = 0; i < _captureDevices.Count; i++)
        {
            var name = _captureDevices[i].FriendlyName ?? string.Empty;
            if (name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdateCableStatus()
    {
        var outputIndex = FindCableInputIndex();
        if (outputIndex >= 0)
        {
            _lblCableStatus.Text = "OK: CABLE Input 検出";
            _lblCableGuide.Text = "";
            AppLogger.Log($"VB-CABLE検出 OK Device={_renderDevices[outputIndex].FriendlyName}");
        }
        else
        {
            _lblCableStatus.Text = "NG: CABLE Input が見つかりません";
            _lblCableGuide.Text = "管理者でセットアップを実行し、インストール後に再起動してください。";
            AppLogger.Log("VB-CABLE検出 NG");
        }
    }

    private void ApplyOutputDeviceSelection()
    {
        if (_comboOutputDevice.SelectedIndex < 0 || _comboOutputDevice.SelectedIndex >= _renderDevices.Count)
        {
            return;
        }

        RestartReceiverOutput();
    }

    private void RestartOutputForJitter()
    {
        if (_output != null)
        {
            RestartReceiverOutput();
        }
    }
}
