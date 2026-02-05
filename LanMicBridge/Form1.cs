
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Concentus.Enums;
using Concentus.Structs;
using LanMicBridge.Engine;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LanMicBridge;

public partial class Form1 : Form
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameMs = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000;
    private const int DefaultPort = 48750;
    private const int KeepAliveMs = 1000;
    private const float VadThresholdDb = -45f;
    private const int VadHangoverMs = 300;
    private const float ClipThresholdLinear = 0.89f;
    private const float AgcTargetRmsDb = -20f;
    private const float AgcNoBoostBelowDb = -50f;
    private const float AgcMaxBoostDb = 24f;
    private const float AgcMaxCutDb = -18f;
    private const float AgcAttack = 0.25f;
    private const float AgcRelease = 0.08f;
    private const float NoiseGateFloorDb = -60f;
    private const float NoiseGateRangeDb = 10f;
    private const float SendAgcTargetRmsDb = -24f;
    private const float SendAgcNoBoostBelowDb = -55f;
    private const float SendAgcMaxBoostDb = 12f;
    private const float SendAgcMaxCutDb = -12f;
    private const float SendAgcAttack = 0.12f;
    private const float SendAgcRelease = 0.05f;
    private const float SendNoiseGateFloorDb = -65f;
    private const float SendNoiseGateRangeDb = 8f;
    private const int TestToneHz = 1000;
    private const float TestToneLevelDb = -12f;
    private const float OutputGainBasePercent = 42f;
    private const float SendGainBasePercent = 40f;
    private const int OutputForceStartMs = 1000;

    private MMDeviceEnumerator? _deviceEnumerator;
    private readonly List<MMDevice> _renderDevices = new();
    private readonly List<MMDevice> _captureDevices = new();
    private readonly List<WaveInCapabilities> _mmeDevices = new();
    private string _lastReceiverStatus = string.Empty;
    private string _lastSenderStatus = string.Empty;
    private int _senderReconnectCount;
    private int _senderDisconnectCount;
    private float _outputGain = 1.0f;
    private float _sendGain = 1.0f;
    private bool _enableRecvProcessing = true;
    private bool _enableSendProcessing = true;
    private bool _sendTestTone;
    private bool _sendPcmDirect;
    private AppSettings _appSettings = new();
    private bool _loadingSettings;
    private int _outputForceStartMs = OutputForceStartMs;
    private float _vadThresholdDb = VadThresholdDb;

    private RadioButton _radioReceiver = null!;
    private RadioButton _radioSender = null!;
    private Panel _receiverPanel = null!;
    private Panel _senderPanel = null!;
    private Label _lblIpList = null!;
    private Button _btnCopyIp = null!;
    private Label _lblPort = null!;
    private Label _lblCableStatus = null!;
    private Label _lblCableGuide = null!;
    private Label _lblMeterA = null!;
    private Label _lblMeterWarning = null!;
    private Label _lblMeterGuide = null!;
    private Label _lblStats = null!;
    private Button _btnCheckTone = null!;
    private Label _lblCheckResult = null!;
    private LinkLabel _linkReceiverDetail = null!;
    private GroupBox _groupReceiverDetail = null!;
    private ComboBox _comboOutputDevice = null!;
    private ComboBox _comboJitter = null!;
    private TrackBar _trackOutputGain = null!;
    private Label _lblOutputGainValue = null!;
    private TrackBar _trackOutputForceStart = null!;
    private Label _lblOutputForceStartValue = null!;
    private Label _lblOutputLevel = null!;
    private CheckBox _chkRecvProcessing = null!;
    private GroupBox _receiverInfoGroup = null!;

    private TextBox _txtIp = null!;
    private Button _btnSenderToggle = null!;
    private Label _lblSenderStatus = null!;
    private LinkLabel _linkSenderDetail = null!;
    private GroupBox _groupSenderDetail = null!;
    private ComboBox _comboCaptureApi = null!;
    private ComboBox _comboMicDevice = null!;
    private ComboBox _comboQuality = null!;
    private TrackBar _trackGain = null!;
    private Label _lblGainValue = null!;
    private TrackBar _trackVadThreshold = null!;
    private Label _lblVadThresholdValue = null!;
    private Label _lblSenderMeterDetail = null!;
    private CheckBox _chkSendProcessing = null!;
    private CheckBox _chkSendTestTone = null!;
    private ComboBox _comboSendMode = null!;

    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripStatusLabel _alertLabel = null!;
    private ToolStripButton _restartButton = null!;
    private string _lastAlert = string.Empty;
    private DateTime _lastAlertTime = DateTime.MinValue;
    private SettingsForm? _settingsForm;

    private ReceiverEngine? _receiverEngine;
    private SenderEngine? _senderEngine;

    public Form1()
    {
        InitializeComponent();
        BuildUi();
    }

    private void Form1_Load(object? sender, EventArgs e)
    {
        AppLogger.Init("Receiver");
        RefreshDeviceLists();
        UpdateIpList();
        UpdateCableStatus();
        LoadAppSettings();
        UpdateModeUi(_radioReceiver.Checked);
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveAppSettings();
        StopSender();
        StopReceiver();
        if (_settingsForm != null)
        {
            _settingsForm.Close();
            _settingsForm.Dispose();
            _settingsForm = null;
        }
        _deviceEnumerator?.Dispose();
        AppLogger.Log("終了");
    }
    private void SetStatus(string text)
    {
        if (text != _lastReceiverStatus)
        {
            _lastReceiverStatus = text;
            AppLogger.Log($"受信状態 {text}");
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => _statusLabel.Text = text);
        }
        else
        {
            _statusLabel.Text = text;
        }
    }

    private void ShowAlert(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (message == _lastAlert && (now - _lastAlertTime).TotalSeconds < 2)
        {
            return;
        }

        _lastAlert = message;
        _lastAlertTime = now;

        if (InvokeRequired)
        {
            BeginInvoke(() => SetAlertText(message));
        }
        else
        {
            SetAlertText(message);
        }
    }

    private void SetAlertText(string message)
    {
        _alertLabel.Text = message;
        _alertLabel.Visible = true;
        _restartButton.Visible = true;
    }

    private void RestartApplication()
    {
        try
        {
            SaveAppSettings();
            StopSender();
            StopReceiver();
        }
        catch
        {
        }

        AppLogger.Log("再起動要求");
        Application.Restart();
        Environment.Exit(0);
    }

    private static void WaitTaskSafely(Task? task, int millisecondsTimeout)
    {
        if (task == null)
        {
            return;
        }

        try
        {
            task.Wait(millisecondsTimeout);
        }
        catch (AggregateException ex)
        {
            ex.Handle(inner => inner is TaskCanceledException);
        }
        catch (TaskCanceledException)
        {
        }
    }
}
