using System.Net;
using System.Net.Sockets;
using Concentus.Enums;
using Concentus.Structs;
using LanMicBridge.Engine;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LanMicBridge;

partial class Form1
{
    private void BtnSenderToggle_Click(object? sender, EventArgs e)
    {
        if (_senderEngine == null || !_senderEngine.IsRunning)
        {
            StartSender();
        }
        else
        {
            StopSender();
        }
    }

    private void ApplyQualitySelection()
    {
        var qualityIndex = Math.Clamp(_comboQuality.SelectedIndex, 0, 3);
        _senderEngine?.SetQuality((SendQuality)qualityIndex);
    }

    private void UpdateGain()
    {
        _sendGain = (_trackGain.Value / 100f) * (SendGainBasePercent / 100f);
        if (_senderEngine != null)
        {
            _senderEngine.SendGain = _sendGain;
        }
        if (_lblGainValue.InvokeRequired)
        {
            _lblGainValue.BeginInvoke(() => _lblGainValue.Text = $"{_trackGain.Value}%");
        }
        else
        {
            _lblGainValue.Text = $"{_trackGain.Value}%";
        }
    }

    private void UpdateOutputGain()
    {
        _outputGain = (_trackOutputGain.Value / 100f) * (OutputGainBasePercent / 100f);
        if (_receiverEngine != null)
        {
            _receiverEngine.OutputGain = _outputGain;
        }
        if (_lblOutputGainValue.InvokeRequired)
        {
            _lblOutputGainValue.BeginInvoke(() => _lblOutputGainValue.Text = $"{_trackOutputGain.Value}%");
        }
        else
        {
            _lblOutputGainValue.Text = $"{_trackOutputGain.Value}%";
        }
    }

    private void UpdateOutputForceStart()
    {
        _outputForceStartMs = _trackOutputForceStart.Value * 100;
        if (_receiverEngine != null)
        {
            _receiverEngine.OutputForceStartMs = _outputForceStartMs;
        }
        var seconds = _outputForceStartMs / 1000.0;
        var text = $"{seconds:0.0}s";
        if (_lblOutputForceStartValue.InvokeRequired)
        {
            _lblOutputForceStartValue.BeginInvoke(() => _lblOutputForceStartValue.Text = text);
        }
        else
        {
            _lblOutputForceStartValue.Text = text;
        }
    }

    private void UpdateVadThreshold()
    {
        _vadThresholdDb = -_trackVadThreshold.Value;
        if (_senderEngine != null)
        {
            _senderEngine.VadThresholdDb = _vadThresholdDb;
        }
        if (_receiverEngine != null)
        {
            _receiverEngine.VadThresholdDb = _vadThresholdDb;
        }
        var text = $"{_vadThresholdDb:0} dBFS";
        if (_lblVadThresholdValue.InvokeRequired)
        {
            _lblVadThresholdValue.BeginInvoke(() => _lblVadThresholdValue.Text = text);
        }
        else
        {
            _lblVadThresholdValue.Text = text;
        }
    }

    private void StartSender()
    {
        if (!IPAddress.TryParse(_txtIp.Text.Trim(), out var ipAddress))
        {
            MessageBox.Show("IPアドレスが正しくありません。", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var useMme = _comboCaptureApi.SelectedIndex == 1;
            var captureApi = useMme ? CaptureApiMode.Mme : CaptureApiMode.Wasapi;
            var mmeIndex = -1;
            string? captureDeviceId = null;

            if (useMme)
            {
                mmeIndex = _comboMicDevice.SelectedIndex;
                if (mmeIndex < 0 || mmeIndex >= WaveInEvent.DeviceCount)
                {
                    MessageBox.Show("マイクデバイスを選択してください。", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else
            {
                if (_comboMicDevice.SelectedIndex < 0 || _comboMicDevice.SelectedIndex >= _captureDevices.Count)
                {
                    MessageBox.Show("マイクデバイスを選択してください。", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                captureDeviceId = _captureDevices[_comboMicDevice.SelectedIndex].ID;
            }

            var qualityIndex = Math.Clamp(_comboQuality.SelectedIndex, 0, 3);
            var sendMode = _comboSendMode.SelectedIndex == 1 ? SendMode.PcmDirect : SendMode.Opus;

            _senderEngine?.Dispose();
            _senderEngine = new SenderEngine();
            _senderEngine.Configure(captureApi, captureDeviceId, mmeIndex, (SendQuality)qualityIndex, sendMode);
            _senderEngine.SendGain = _sendGain;
            _senderEngine.VadThresholdDb = _vadThresholdDb;
            _senderEngine.EnableProcessing = _enableSendProcessing;
            _senderEngine.SendTestTone = _sendTestTone;

            _senderEngine.StatusChanged += SetSenderStatus;
            _senderEngine.MeterChanged += UpdateSenderMeterFromEngine;
            _senderEngine.Error += ShowAlert;

            _senderEngine.Start(ipAddress, DefaultPort);
            _btnSenderToggle.Text = "停止";
            SetSenderStatus("接続中");
            AppLogger.Log($"送信開始 IP={ipAddress}");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("送信開始失敗", ex);
            StopSender();
            MessageBox.Show($"送信開始に失敗しました: {ex.Message}", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowAlert("送信開始に失敗しました。再起動を試してください。");
        }
    }

    private void UpdateSenderMeterFromEngine(AudioLevel level)
    {
        var text = $"Peak {level.PeakDb:0.0} dBFS / RMS {level.RmsDb:0.0} dBFS";
        UpdateSenderMeterText(text);
    }

    private void StopSender()
    {
        _senderEngine?.Dispose();
        _senderEngine = null;
        _btnSenderToggle.Text = "開始";
        SetSenderStatus("待機中");
    }
    
    // キャプチャ/送信/再接続ロジックは `SenderEngine` に移動しました。

    private void SetSenderStatus(string text)
    {
        if (text != _lastSenderStatus)
        {
            if (text == "再接続中")
            {
                _senderReconnectCount++;
                AppLogger.Log($"送信再接続 count={_senderReconnectCount}");
            }
            else if (text == "待機中" && _lastSenderStatus != string.Empty)
            {
                _senderDisconnectCount++;
                AppLogger.Log($"送信切断 count={_senderDisconnectCount}");
            }
            else
            {
                AppLogger.Log($"送信状態 {text}");
            }

            _lastSenderStatus = text;
        }

        if (_lblSenderStatus.InvokeRequired)
        {
            _lblSenderStatus.BeginInvoke(() => _lblSenderStatus.Text = text);
        }
        else
        {
            _lblSenderStatus.Text = text;
        }
    }
}
