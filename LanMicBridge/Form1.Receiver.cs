using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Concentus.Structs;
using LanMicBridge.Engine;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LanMicBridge;

partial class Form1
{
    private void StartReceiver()
    {
        if (_receiverEngine != null && _receiverEngine.IsRunning)
        {
            return;
        }

        try
        {
            _receiverEngine?.Dispose();
            _receiverEngine = new ReceiverEngine();

            string? outputDeviceId = null;
            if (_comboOutputDevice.SelectedIndex >= 0 && _comboOutputDevice.SelectedIndex < _renderDevices.Count)
            {
                outputDeviceId = _renderDevices[_comboOutputDevice.SelectedIndex].ID;
            }

            var jitterIndex = Math.Clamp(_comboJitter.SelectedIndex, 0, 2);
            _receiverEngine.Configure(outputDeviceId, (JitterMode)jitterIndex, DefaultPort);
            _receiverEngine.OutputGain = _outputGain;
            _receiverEngine.OutputForceStartMs = _outputForceStartMs;
            _receiverEngine.EnableProcessing = _enableRecvProcessing;
            _receiverEngine.VadThresholdDb = _vadThresholdDb;

            _receiverEngine.StatusChanged += SetStatus;
            _receiverEngine.InputLevelChanged += UpdateReceiverInputLevel;
            _receiverEngine.OutputLevelChanged += UpdateReceiverOutputLevel;
            _receiverEngine.WarningChanged += UpdateReceiverWarning;
            _receiverEngine.StatsUpdated += UpdateReceiverStats;
            _receiverEngine.Error += ShowAlert;

            _receiverEngine.Start();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("受信開始失敗", ex);
            SetStatus("エラー: 受信開始失敗");
            MessageBox.Show($"受信開始に失敗しました: {ex.Message}", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowAlert("受信開始に失敗しました。再起動を試してください。");
        }
    }

    private void UpdateReceiverInputLevel(AudioLevel level)
    {
        var text = $"受信: Peak {level.PeakDb:0.0} dBFS / RMS {level.RmsDb:0.0} dBFS";
        if (_lblMeterA.InvokeRequired)
        {
            _lblMeterA.BeginInvoke(() => _lblMeterA.Text = text);
        }
        else
        {
            _lblMeterA.Text = text;
        }
    }

    private void UpdateReceiverOutputLevel(AudioLevel level)
    {
        var text = $"出力: Peak {level.PeakDb:0.0} dBFS / RMS {level.RmsDb:0.0} dBFS";
        if (_lblOutputLevel.InvokeRequired)
        {
            _lblOutputLevel.BeginInvoke(() => _lblOutputLevel.Text = text);
        }
        else
        {
            _lblOutputLevel.Text = text;
        }
    }

    private void UpdateReceiverWarning(string text)
    {
        if (_lblMeterWarning.InvokeRequired)
        {
            _lblMeterWarning.BeginInvoke(() => _lblMeterWarning.Text = text);
        }
        else
        {
            _lblMeterWarning.Text = text;
        }
    }

    private void UpdateReceiverStats(ReceiverStats stats)
    {
        var text = $"Packets: {stats.Packets}  Loss: {stats.LossPercent}%  Jitter: {stats.JitterMs}ms  Delay: {stats.DelayMs}ms";
        if (_lblStats.InvokeRequired)
        {
            _lblStats.BeginInvoke(() => _lblStats.Text = text);
        }
        else
        {
            _lblStats.Text = text;
        }
    }

    private void RestartReceiverOutput()
    {
        try
        {
            if (_receiverEngine == null)
            {
                return;
            }

            string? outputDeviceId = null;
            if (_comboOutputDevice.SelectedIndex >= 0 && _comboOutputDevice.SelectedIndex < _renderDevices.Count)
            {
                outputDeviceId = _renderDevices[_comboOutputDevice.SelectedIndex].ID;
            }

            var jitterIndex = Math.Clamp(_comboJitter.SelectedIndex, 0, 2);
            var jitterMode = (JitterMode)jitterIndex;

            if (!string.Equals(_receiverEngine.OutputDeviceId, outputDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                _receiverEngine.SetOutputDevice(outputDeviceId);
            }

            if (_receiverEngine.JitterMode != jitterMode)
            {
                _receiverEngine.SetJitterMode(jitterMode);
            }

            _receiverEngine.OutputGain = _outputGain;
            _receiverEngine.OutputForceStartMs = _outputForceStartMs;
            _receiverEngine.EnableProcessing = _enableRecvProcessing;
            _receiverEngine.VadThresholdDb = _vadThresholdDb;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("出力初期化失敗", ex);
            SetStatus("エラー: 出力初期化失敗");
            ShowAlert("出力初期化に失敗しました。再起動を試してください。");
        }
    }

    private void StopReceiver()
    {
        _receiverEngine?.Dispose();
        _receiverEngine = null;
        SetStatus("待受中");
    }

    // 受信/再生/ジッタ/統計/チェック音の実処理は `ReceiverEngine` に移動しました。
    private async void BtnCheckTone_Click(object? sender, EventArgs e)
    {
        if (_receiverEngine == null || !_receiverEngine.IsRunning)
        {
            MessageBox.Show("受信が開始されていません。", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnCheckTone.Enabled = false;
        try
        {
            var pass = await _receiverEngine.PlayCheckToneAsync();
            _lblCheckResult.Text = pass ? "結果: PASS" : "結果: FAIL";
        }
        finally
        {
            _btnCheckTone.Enabled = true;
        }
    }

    // loopbackチェック等の内部処理は `ReceiverEngine` 側に集約しています。

    private void UpdateSenderMeterText(string text)
    {
        if (_lblSenderMeterDetail.InvokeRequired)
        {
            _lblSenderMeterDetail.BeginInvoke(() => _lblSenderMeterDetail.Text = text);
        }
        else
        {
            _lblSenderMeterDetail.Text = text;
        }
    }
}
