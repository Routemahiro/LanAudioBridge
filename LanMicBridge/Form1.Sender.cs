using System.Net;
using System.Net.Sockets;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LanMicBridge;

partial class Form1
{
    private void BtnSenderToggle_Click(object? sender, EventArgs e)
    {
        if (_senderUdp == null)
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
        switch (_comboQuality.SelectedIndex)
        {
            case 0:
                _selectedBitrate = 16000;
                _selectedComplexity = 3;
                break;
            case 2:
                _selectedBitrate = 64000;
                _selectedComplexity = 8;
                break;
            case 3:
                _selectedBitrate = 128000;
                _selectedComplexity = 10;
                break;
            default:
                _selectedBitrate = 32000;
                _selectedComplexity = 5;
                break;
        }

        if (_encoder != null)
        {
            _encoder.Bitrate = _selectedBitrate;
            _encoder.Complexity = _selectedComplexity;
        }
    }

    private void UpdateGain()
    {
        _sendGain = (_trackGain.Value / 100f) * (SendGainBasePercent / 100f);
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
            if (useMme)
            {
                var mmeIndex = _comboMicDevice.SelectedIndex;
                if (mmeIndex < 0 || mmeIndex >= WaveInEvent.DeviceCount)
                {
                    MessageBox.Show("マイクデバイスを選択してください。", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var waveIn = new WaveInEvent
                {
                    DeviceNumber = mmeIndex,
                    BufferMilliseconds = 50,
                    NumberOfBuffers = 3
                };
                _capture = waveIn;
            }
            else
            {
                if (_comboMicDevice.SelectedIndex < 0 || _comboMicDevice.SelectedIndex >= _captureDevices.Count)
                {
                    MessageBox.Show("マイクデバイスを選択してください。", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var device = _captureDevices[_comboMicDevice.SelectedIndex];
                _capture = new WasapiCapture(device, true, 50);
            }

            _captureBuffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
                BufferDuration = TimeSpan.FromSeconds(1)
            };
            _capture.DataAvailable += CaptureOnDataAvailable;
            _capture.RecordingStopped += CaptureOnRecordingStopped;

            var sampleProvider = _captureBuffer.ToSampleProvider();
            var inputChannels = sampleProvider.WaveFormat.Channels;
            if (inputChannels == 2)
            {
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
            }
            else if (inputChannels != 1)
            {
                var mux = new MultiplexingSampleProvider(new[] { sampleProvider }, 1);
                mux.ConnectInputToOutput(0, 0);
                sampleProvider = mux;
                AppLogger.Log($"送信入力チャンネル数 {inputChannels} -> 1 に変換");
            }

            if (sampleProvider.WaveFormat.SampleRate != SampleRate)
            {
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, SampleRate);
            }

            _sendSampleProvider = sampleProvider;
            _sendSampleBuffer = new float[FrameSamples];

            _encoder = OpusEncoder.Create(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = _selectedBitrate;
            _encoder.Complexity = _selectedComplexity;
            _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            _encoder.UseVBR = true;
            _encoder.UseInbandFEC = true;
            _encoder.PacketLossPercent = 20;

            _senderUdp = new UdpClient(0);
            _senderUdp.Client.SendBufferSize = 1024 * 1024;
            _senderUdp.Connect(ipAddress, DefaultPort);
            _senderCts = new CancellationTokenSource();
            _senderTask = Task.Run(() => SendLoop(_senderCts.Token));
            _senderReceiveTask = Task.Run(() => SenderReceiveLoop(_senderCts.Token));

            _capture.StartRecording();
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

    private void StopSender()
    {
        _senderCts?.Cancel();
        WaitTaskSafely(_senderTask, 500);
        WaitTaskSafely(_senderReceiveTask, 500);
        _senderTask = null;
        _senderReceiveTask = null;
        _senderCts?.Dispose();
        _senderCts = null;

        if (_capture != null)
        {
            _capture.DataAvailable -= CaptureOnDataAvailable;
            _capture.RecordingStopped -= CaptureOnRecordingStopped;
            _capture.StopRecording();
            _capture.Dispose();
            _capture = null;
        }

        _sendSampleProvider = null;
        _sendSampleBuffer = null;
        _captureBuffer = null;
        _encoder = null;

        _senderUdp?.Close();
        _senderUdp?.Dispose();
        _senderUdp = null;

        _btnSenderToggle.Text = "開始";
        SetSenderStatus("待機中");
        _accepted = false;
    }

    private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            AppLogger.LogException("録音停止", e.Exception);
            BeginInvoke(() => MessageBox.Show($"録音停止: {e.Exception.Message}", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Error));
            ShowAlert("録音が停止しました。再起動を試してください。");
            StopSender();
        }
    }

    private void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _captureBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private async Task SendLoop(CancellationToken token)
    {
        var pcm = new short[FrameSamples];
        var payload = new byte[4000];
        var pcmBytes = new byte[FrameSamples * 2];

        while (!token.IsCancellationRequested)
        {
            try
            {
                var senderUdp = _senderUdp;
                if (_sendSampleProvider == null || _sendSampleBuffer == null || senderUdp == null)
                {
                    await Task.Delay(20, token);
                    continue;
                }
                var encoder = _encoder;
                if (!_sendPcmDirect && encoder == null)
                {
                    await Task.Delay(20, token);
                    continue;
                }

                if (_sendTestTone)
                {
                    FillTestToneFrame(pcm);
                }
                else
                {
                    var samplesRead = _sendSampleProvider.Read(_sendSampleBuffer, 0, FrameSamples);
                    if (samplesRead == 0)
                    {
                        Array.Clear(pcm, 0, pcm.Length);
                    }
                    else
                    {
                        if (samplesRead < FrameSamples)
                        {
                            Array.Clear(_sendSampleBuffer, samplesRead, FrameSamples - samplesRead);
                        }

                        for (var i = 0; i < FrameSamples; i++)
                        {
                            var sample = Math.Clamp(_sendSampleBuffer[i], -1f, 1f);
                            pcm[i] = (short)Math.Round(sample * short.MaxValue);
                        }
                    }
                }
                ApplyGain(pcm, _sendGain);
                AudioMeter.ComputePeakRms(pcm, out var peakPre, out var rmsPre);
                var rmsDbPre = LinearToDb(rmsPre);
                var now = DateTime.UtcNow;

                if (rmsDbPre >= _vadThresholdDb || _sendTestTone)
                {
                    _lastVoiceTime = now;
                }

                var withinHangover = (now - _lastVoiceTime).TotalMilliseconds <= VadHangoverMs;
                float peak;
                float rms;
                if (_enableSendProcessing)
                {
                    ApplyAgcAndGain(
                        pcm,
                        rmsDbPre,
                        ref _sendAgcGainDb,
                        _sendGain,
                        out peak,
                        out rms,
                        SendAgcTargetRmsDb,
                        SendAgcNoBoostBelowDb,
                        SendAgcMaxBoostDb,
                        SendAgcMaxCutDb,
                        SendAgcAttack,
                        SendAgcRelease,
                        SendNoiseGateFloorDb,
                        SendNoiseGateRangeDb);
                }
                else
                {
                    ApplyGain(pcm, _sendGain);
                    AudioMeter.ComputePeakRms(pcm, out peak, out rms);
                }
                var rmsDbPost = LinearToDb(rms);

                if ((now - _lastSenderMeterUpdate).TotalMilliseconds >= 200)
                {
                    var peakDb = LinearToDb(peak);
                    var senderText = $"Peak {peakDb:0.0} dBFS / RMS {rmsDbPost:0.0} dBFS";
                    UpdateSenderMeterText(senderText);

                    _lastSenderMeterUpdate = now;
                }

                var sentAudio = false;
                if (_sendPcmDirect)
                {
                    Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
                    var pcmPacket = NetworkProtocol.BuildPcm(_senderId, _sendSequence++, pcmBytes);
                    senderUdp.Send(pcmPacket, pcmPacket.Length);
                    sentAudio = true;
                    SetSenderStatus(_accepted ? "接続中" : "再接続中");
                }
                else if (rmsDbPre >= _vadThresholdDb || withinHangover || _sendTestTone)
                {
                    var encoded = encoder!.Encode(pcm, 0, FrameSamples, payload, 0, payload.Length);
                    var packet = NetworkProtocol.BuildAudio(_senderId, _sendSequence++, payload.AsSpan(0, encoded));
                    senderUdp.Send(packet, packet.Length);
                    sentAudio = true;
                    SetSenderStatus(_accepted ? "接続中" : "再接続中");
                }
                else if (!sentAudio)
                {
                    if ((now - _lastKeepAlive).TotalMilliseconds >= KeepAliveMs)
                    {
                        var keep = NetworkProtocol.BuildKeepAlive(_senderId);
                        senderUdp.Send(keep, keep.Length);
                        _lastKeepAlive = now;
                    }
                }

                if ((now - _lastHello).TotalMilliseconds >= 2000 && !_accepted)
                {
                    var hello = NetworkProtocol.BuildHello(_senderId);
                    senderUdp.Send(hello, hello.Length);
                    _lastHello = now;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.LogException("送信ループ", ex);
                SetSenderStatus("エラー");
                ShowAlert("送信中にエラーが発生しました。再起動を試してください。");
                await Task.Delay(200, token);
            }

            await Task.Delay(FrameMs, token);
        }
    }

    private async Task SenderReceiveLoop(CancellationToken token)
    {
        if (_senderUdp == null)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _senderUdp.ReceiveAsync(token);
                if (NetworkProtocol.TryParse(result.Buffer, out var type, out var senderId, out _, out _))
                {
                    if (type == PacketType.Accept && senderId == _senderId)
                    {
                        _accepted = true;
                        _lastAccept = DateTime.UtcNow;
                        SetSenderStatus("接続中");
                    }
                }

                if (_accepted && (DateTime.UtcNow - _lastAccept).TotalSeconds > 5)
                {
                    _accepted = false;
                    SetSenderStatus("再接続中");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.LogException("送信受信", ex);
                ShowAlert("送信中にエラーが発生しました。再起動を試してください。");
                await Task.Delay(200, token);
            }
        }
    }

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
