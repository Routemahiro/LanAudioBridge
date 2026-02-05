using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LanMicBridge;

partial class Form1
{
    private void StartReceiver()
    {
        if (_receiverUdp != null)
        {
            return;
        }

        try
        {
            _decoder = OpusDecoder.Create(SampleRate, Channels);
            _receiverUdp = new UdpClient(DefaultPort);
            _receiverUdp.Client.ReceiveBufferSize = 1024 * 1024;
            _receiverCts = new CancellationTokenSource();
            _receiverTask = Task.Run(() => ReceiveLoop(_receiverCts.Token));
            _playoutCts = new CancellationTokenSource();
            _playoutTask = Task.Run(() => PlayoutLoop(_playoutCts.Token));
            _statsWatch.Restart();
            _receiverClock.Restart();
            _statsTimer.Start();
            _receiverStatusTimer.Start();
            _silenceTimer.Start();
            ResetPlayoutState();
            RestartReceiverOutput();
            SetStatus("待受中");
            AppLogger.Log("受信待受開始");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("受信開始失敗", ex);
            SetStatus("エラー: 受信開始失敗");
            MessageBox.Show($"受信開始に失敗しました: {ex.Message}", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowAlert("受信開始に失敗しました。再起動を試してください。");
        }
    }

    private void RestartReceiverOutput()
    {
        try
        {
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _playBuffer = null;

            if (_comboOutputDevice.SelectedIndex < 0 || _comboOutputDevice.SelectedIndex >= _renderDevices.Count)
            {
                return;
            }

            var latency = _comboJitter.SelectedIndex switch
            {
                1 => 140,
                2 => 220,
                _ => 60
            };
            var bufferSeconds = _comboJitter.SelectedIndex switch
            {
                1 => 3.5,
                2 => 4.5,
                _ => 1.8
            };
            var device = _renderDevices[_comboOutputDevice.SelectedIndex];
            _output = new WasapiOut(device, AudioClientShareMode.Shared, true, latency);
            _playBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(bufferSeconds)
            };
            _output.Init(_playBuffer);
            _outputStarted = false;
            _prebufferMs = _comboJitter.SelectedIndex switch
            {
                1 => 220,
                2 => 320,
                _ => 120
            };
            _baseJitterFrames = Math.Max(3, _prebufferMs / FrameMs);
            _adaptiveJitterFrames = _baseJitterFrames;
            _minJitterFrames = Math.Max(3, _baseJitterFrames - 2);
            _maxJitterFrames = _baseJitterFrames + 8;
            _rebufferThresholdMs = Math.Max(40, _prebufferMs / 2);
            AppLogger.Log($"出力初期化 Device={device.FriendlyName} Latency={latency}ms Prebuffer={_prebufferMs}ms JitterFrames={_baseJitterFrames}");
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
        _receiverCts?.Cancel();
        WaitTaskSafely(_receiverTask, 500);
        _receiverTask = null;
        _receiverCts?.Dispose();
        _receiverCts = null;
        _playoutCts?.Cancel();
        WaitTaskSafely(_playoutTask, 500);
        _playoutTask = null;
        _playoutCts?.Dispose();
        _playoutCts = null;

        _receiverUdp?.Close();
        _receiverUdp?.Dispose();
        _receiverUdp = null;

        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _playBuffer = null;
        _outputStarted = false;

        _statsTimer?.Stop();
        _receiverStatusTimer?.Stop();
        _silenceTimer?.Stop();
        _packetsReceived = 0;
        _packetsLost = 0;
        _bytesReceived = 0;
        ResetPlayoutState();
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _receiverUdp != null)
        {
            try
            {
                var result = await _receiverUdp.ReceiveAsync(token);
                ProcessPacket(result.Buffer, result.RemoteEndPoint);
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
                AppLogger.LogException("受信エラー", ex);
                BeginInvoke(() => SetStatus("エラー: 受信エラー"));
                ShowAlert("受信中にエラーが発生しました。再起動を試してください。");
            }
        }
    }

    private void ProcessPacket(byte[] buffer, IPEndPoint remote)
    {
        if (!NetworkProtocol.TryParse(buffer, out var type, out var senderId, out var seq, out var payload))
        {
            return;
        }

        _lastPacketTime = DateTime.UtcNow;
        _lastSenderEndpoint = remote;

        switch (type)
        {
            case PacketType.Hello:
                SendAccept(remote, senderId);
                break;
            case PacketType.KeepAlive:
                break;
            case PacketType.Audio:
                HandleAudioPacket(seq, payload);
                break;
            case PacketType.Pcm:
                HandlePcmPacket(seq, payload);
                break;
        }
    }

    private void SendAccept(IPEndPoint remote, uint senderId)
    {
        if (_receiverUdp == null)
        {
            return;
        }

        var buffer = NetworkProtocol.BuildAccept(senderId);
        _receiverUdp.Send(buffer, buffer.Length, remote);
    }

    private void HandleAudioPacket(uint sequence, ReadOnlySpan<byte> payload)
    {
        EnqueuePacket(sequence, PacketType.Audio, payload);
    }

    private void HandlePcmPacket(uint sequence, ReadOnlySpan<byte> payload)
    {
        EnqueuePacket(sequence, PacketType.Pcm, payload);
    }

    private void EnqueuePacket(uint sequence, PacketType type, ReadOnlySpan<byte> payload)
    {
        _packetsReceived++;
        _bytesReceived += payload.Length;
        UpdateJitter(sequence);

        var data = payload.ToArray();
        lock (_jitterLock)
        {
            if (!_jitterBuffer.ContainsKey(sequence))
            {
                _jitterBuffer[sequence] = new PacketEntry(type, data);
            }

            _lastPacketType = type;
            if (!_playoutInitialized)
            {
                _playoutSequence = sequence;
                _playoutInitialized = true;
                _playoutBuffering = true;
                _playoutBufferingSince = DateTime.UtcNow;
            }
        }
    }

    private async Task PlayoutLoop(CancellationToken token)
    {
        var frameDuration = TimeSpan.FromMilliseconds(FrameMs);
        var stopwatch = Stopwatch.StartNew();
        var next = stopwatch.Elapsed;

        while (!token.IsCancellationRequested)
        {
            if (!_playoutInitialized)
            {
                await Task.Delay(5, token);
                continue;
            }

            if (_lastPacketTime != DateTime.MinValue && (DateTime.UtcNow - _lastPacketTime).TotalSeconds > 2)
            {
                ResetPlayoutState();
                await Task.Delay(50, token);
                continue;
            }

            if (_playoutBuffering)
            {
                var bufferedCount = GetBufferedCount();
                var bufferedEnough = bufferedCount >= _adaptiveJitterFrames;
                var bufferedTooLong = bufferedCount > 0 && _playoutBufferingSince != DateTime.MinValue &&
                    (DateTime.UtcNow - _playoutBufferingSince).TotalMilliseconds >= _outputForceStartMs;
                if (bufferedEnough || bufferedTooLong)
                {
                    _playoutBuffering = false;
                    next = stopwatch.Elapsed;
                    AppLogger.Log($"再生開始 JitterFrames={_adaptiveJitterFrames} Buffered={bufferedCount} Wait={_outputForceStartMs}ms");
                }
                else
                {
                    await Task.Delay(5, token);
                    continue;
                }
            }
            else
            {
                var bufferedCount = GetBufferedCount();
                if (bufferedCount > _adaptiveJitterFrames * 3 && TryGetMinSequence(out var minSeq))
                {
                    _playoutSequence = minSeq;
                    AppLogger.Log($"再同期 PlayoutSeq={_playoutSequence} Buffered={bufferedCount}");
                }
            }

            PlaySequence(_playoutSequence);
            _playoutSequence++;

            next += frameDuration;
            var delay = next - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token);
            }
        }
    }

    private void PlaySequence(uint sequence)
    {
        if (TryDequeuePacket(sequence, out var entry) && entry != null)
        {
            if (entry.Type == PacketType.Audio)
            {
                DecodeAndPlay(entry.Payload, entry.Payload.Length, false, false);
            }
            else
            {
                ProcessPcmPayload(entry.Payload);
            }

            AdjustJitterTarget(true);
            return;
        }

        _packetsLost++;

        if (TryPeekPacket(sequence + 1, out var next) && next != null && next.Type == PacketType.Audio)
        {
            DecodeAndPlay(next.Payload, next.Payload.Length, false, true);
            return;
        }

        if (_lastPacketType == PacketType.Pcm)
        {
            ProcessPcmAndPlay(new short[FrameSamples]);
        }
        else
        {
            DecodeAndPlay(Array.Empty<byte>(), 0, true, false);
        }

        AdjustJitterTarget(false);
    }

    private void ProcessPcmPayload(byte[] payload)
    {
        var pcm = new short[FrameSamples];
        var frameBytes = FrameSamples * 2;
        if (payload.Length >= frameBytes)
        {
            Buffer.BlockCopy(payload, 0, pcm, 0, frameBytes);
        }
        else if (payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, pcm, 0, payload.Length);
        }

        ProcessPcmAndPlay(pcm);
    }

    private bool TryDequeuePacket(uint sequence, out PacketEntry? entry)
    {
        lock (_jitterLock)
        {
            if (_jitterBuffer.TryGetValue(sequence, out entry))
            {
                _jitterBuffer.Remove(sequence);
                return true;
            }
        }

        entry = null;
        return false;
    }

    private bool TryPeekPacket(uint sequence, out PacketEntry? entry)
    {
        lock (_jitterLock)
        {
            if (_jitterBuffer.TryGetValue(sequence, out entry))
            {
                return true;
            }
        }

        entry = null;
        return false;
    }

    private int GetBufferedCount()
    {
        lock (_jitterLock)
        {
            return _jitterBuffer.Count;
        }
    }

    private bool TryGetMinSequence(out uint sequence)
    {
        lock (_jitterLock)
        {
            foreach (var key in _jitterBuffer.Keys)
            {
                sequence = key;
                return true;
            }
        }

        sequence = 0;
        return false;
    }

    private void ResetPlayoutState()
    {
        lock (_jitterLock)
        {
            _jitterBuffer.Clear();
        }

        _playoutInitialized = false;
        _playoutBuffering = true;
        _playoutSequence = 0;
        _lastPacketType = PacketType.Audio;
        _playoutBufferingSince = DateTime.MinValue;
        _adaptiveJitterFrames = _baseJitterFrames;
        _jitterWindowFrames = 0;
        _jitterMissesWindow = 0;
        _outputStartPendingSince = DateTime.MinValue;
    }

    private void AdjustJitterTarget(bool gotPacket)
    {
        _jitterWindowFrames++;
        if (!gotPacket)
        {
            _jitterMissesWindow++;
        }

        if (_jitterWindowFrames < 50)
        {
            return;
        }

        var missRate = _jitterMissesWindow / (double)_jitterWindowFrames;
        var bufferedCount = GetBufferedCount();

        if (missRate > 0.02 && _adaptiveJitterFrames < _maxJitterFrames)
        {
            var step = missRate > 0.1 ? 2 : 1;
            _adaptiveJitterFrames = Math.Min(_adaptiveJitterFrames + step, _maxJitterFrames);
            AppLogger.Log($"Jitter target ↑ {_adaptiveJitterFrames} missRate={missRate:0.0%}");
        }
        else if (missRate == 0 && bufferedCount > _adaptiveJitterFrames + 6 && _adaptiveJitterFrames > _minJitterFrames)
        {
            _adaptiveJitterFrames = Math.Max(_adaptiveJitterFrames - 1, _minJitterFrames);
            AppLogger.Log($"Jitter target ↓ {_adaptiveJitterFrames}");
        }

        _jitterWindowFrames = 0;
        _jitterMissesWindow = 0;
    }

    private void DecodeAndPlay(byte[] payload, int length, bool lost, bool useFec)
    {
        if (_decoder == null || _playBuffer == null)
        {
            return;
        }

        if (DateTime.UtcNow < _suppressNetworkAudioUntil)
        {
            return;
        }

        var pcm = new short[FrameSamples];
        try
        {
            if (lost)
            {
                _decoder.Decode(Array.Empty<byte>(), 0, 0, pcm, 0, FrameSamples, false);
            }
            else
            {
                _decoder.Decode(payload, 0, length, pcm, 0, FrameSamples, useFec);
            }
        }
        catch
        {
            Array.Clear(pcm, 0, pcm.Length);
        }

        ProcessPcmAndPlay(pcm);
    }

    private void ProcessPcmAndPlay(short[] pcm)
    {
        if (_playBuffer == null)
        {
            return;
        }

        AudioMeter.ComputePeakRms(pcm, out var peak, out var rms);
        _meterA.Update(peak, rms);
        UpdateMeterText(_meterA, _lblMeterA);
        UpdateMeterWarnings(peak, _meterA.SmoothedRmsDb);

        var rmsDbPre = LinearToDb(rms);
        float outPeak;
        float outRms;
        if (_enableRecvProcessing)
        {
            ApplyAgcAndGain(
                pcm,
                rmsDbPre,
                ref _recvAgcGainDb,
                _outputGain,
                out outPeak,
                out outRms,
                AgcTargetRmsDb,
                AgcNoBoostBelowDb,
                AgcMaxBoostDb,
                AgcMaxCutDb,
                AgcAttack,
                AgcRelease,
                NoiseGateFloorDb,
                NoiseGateRangeDb);
        }
        else
        {
            ApplyGain(pcm, _outputGain);
            AudioMeter.ComputePeakRms(pcm, out outPeak, out outRms);
        }
        UpdateOutputLevel(outPeak, outRms);

        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _playBuffer.AddSamples(bytes, 0, bytes.Length);
        MaybeStartOutput();
    }

    private void UpdateJitter(uint sequence)
    {
        var arrivalMs = _receiverClock.Elapsed.TotalMilliseconds;
        var expectedMs = sequence * FrameMs;
        var transit = arrivalMs - expectedMs;
        var d = transit - _lastTransitMs;
        _lastTransitMs = transit;
        _jitterMs += (Math.Abs(d) - _jitterMs) / 16.0;
    }

    private void UpdateStatsLabel()
    {
        var packets = _packetsReceived;
        var lossPercent = packets + _packetsLost == 0 ? 0 : (int)(_packetsLost * 100.0 / (packets + _packetsLost));
        var jitter = (int)Math.Round(_jitterMs);
        var delay = _playBuffer != null ? (int)_playBuffer.BufferedDuration.TotalMilliseconds : 0;
        var text = $"Packets: {packets}  Loss: {lossPercent}%  Jitter: {jitter}ms  Delay: {delay}ms";
        _lblStats.Text = text;

        if ((DateTime.UtcNow - _lastStatsLogged).TotalSeconds >= 10)
        {
            AppLogger.Log($"受信統計 loss={lossPercent}% jitter={jitter}ms delay={delay}ms");
            _lastStatsLogged = DateTime.UtcNow;
        }

        if (_lastSenderEndpoint != null && _receiverUdp != null)
        {
            var statsPacket = NetworkProtocol.BuildStats(0, lossPercent, jitter);
            _receiverUdp.Send(statsPacket, statsPacket.Length, _lastSenderEndpoint);
        }
    }

    private void UpdateReceiverStatus()
    {
        var now = DateTime.UtcNow;
        if (_lastPacketTime == DateTime.MinValue)
        {
            SetStatus("待受中");
            return;
        }

        var elapsed = now - _lastPacketTime;
        if (elapsed.TotalSeconds <= 1)
        {
            _hadConnection = true;
            SetStatus("接続中");
        }
        else if (elapsed.TotalSeconds <= 5 && _hadConnection)
        {
            SetStatus("再接続中");
        }
        else
        {
            SetStatus("待受中");
        }
    }

    private void EnsureSilence()
    {
        if (_playBuffer == null || _output == null)
        {
            return;
        }

        var bufferedMs = _playBuffer.BufferedDuration.TotalMilliseconds;
        if (_outputStarted && bufferedMs < _rebufferThresholdMs)
        {
            _output.Pause();
            _outputStarted = false;
            _outputStartPendingSince = DateTime.MinValue;
            AppLogger.Log("出力再バッファ開始");
            return;
        }

        if (_outputStarted && bufferedMs < 40)
        {
            var bytes = new byte[FrameSamples * 2];
            _playBuffer.AddSamples(bytes, 0, bytes.Length);
        }
    }

    private void MaybeStartOutput()
    {
        if (_output == null || _playBuffer == null || _outputStarted)
        {
            return;
        }

        var bufferedMs = _playBuffer.BufferedDuration.TotalMilliseconds;
        if (bufferedMs <= 0)
        {
            _outputStartPendingSince = DateTime.MinValue;
            return;
        }

        if (bufferedMs >= _prebufferMs)
        {
            _output.Play();
            _outputStarted = true;
            _outputStartPendingSince = DateTime.MinValue;
            AppLogger.Log($"出力開始 Prebuffer={_prebufferMs}ms");
            return;
        }

        if (_outputStartPendingSince == DateTime.MinValue)
        {
            _outputStartPendingSince = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _outputStartPendingSince).TotalMilliseconds >= _outputForceStartMs)
        {
            _output.Play();
            _outputStarted = true;
            _outputStartPendingSince = DateTime.MinValue;
            AppLogger.Log($"出力強制開始 Buffered={bufferedMs:0}ms");
        }
    }
    private async void BtnCheckTone_Click(object? sender, EventArgs e)
    {
        if (_playBuffer == null)
        {
            MessageBox.Show("出力デバイスが初期化されていません。", "LanMicBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _btnCheckTone.Enabled = false;
        try
        {
            _suppressNetworkAudioUntil = DateTime.UtcNow.AddSeconds(1.1);
            var tone = GenerateSinePcm16(1000, 1.0f, -12f);
            _playBuffer.AddSamples(tone, 0, tone.Length);

            var pass = await RunLoopbackCheckAsync();
            _lblCheckResult.Text = pass ? "結果: PASS" : "結果: FAIL";
            AppLogger.Log($"チェック音結果 {(pass ? "PASS" : "FAIL")}");
        }
        finally
        {
            _btnCheckTone.Enabled = true;
        }
    }

    private async Task<bool> RunLoopbackCheckAsync()
    {
        var outputIndex = FindCableOutputIndex();
        if (outputIndex < 0 || outputIndex >= _captureDevices.Count)
        {
            return false;
        }

        var device = _captureDevices[outputIndex];
        var tcs = new TaskCompletionSource<bool>();
        var capture = new WasapiCapture(device);
        var maxRmsDb = -120f;
        var warningTimer = new System.Windows.Forms.Timer { Interval = 1100 };

        capture.DataAvailable += (_, args) =>
        {
            ReadOnlySpan<byte> buffer = args.Buffer.AsSpan(0, args.BytesRecorded);
            ConvertToPcm16(buffer, capture.WaveFormat, out var pcm);
            AudioMeter.ComputePeakRms(pcm, out var peak, out var rms);
            var rmsDb = LinearToDb(rms);
            if (rmsDb > maxRmsDb)
            {
                maxRmsDb = rmsDb;
            }
        };

        capture.RecordingStopped += (_, _) =>
        {
            warningTimer.Stop();
            warningTimer.Dispose();
            capture.Dispose();
            var pass = maxRmsDb >= VadThresholdDb;
            tcs.TrySetResult(pass);
        };

        warningTimer.Tick += (_, _) => capture.StopRecording();
        warningTimer.Start();
        capture.StartRecording();

        return await tcs.Task;
    }

    private void ConvertToPcm16(ReadOnlySpan<byte> buffer, WaveFormat format, out short[] pcm)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = buffer.Length / 4;
            pcm = new short[samples];
            for (var i = 0; i < samples; i++)
            {
                var value = BitConverter.ToSingle(buffer.Slice(i * 4, 4));
                var clamped = Math.Clamp(value, -1f, 1f);
                pcm[i] = (short)(clamped * short.MaxValue);
            }
            return;
        }

        if (format.BitsPerSample == 16)
        {
            pcm = new short[buffer.Length / 2];
            Buffer.BlockCopy(buffer.ToArray(), 0, pcm, 0, buffer.Length);
            return;
        }

        pcm = Array.Empty<short>();
    }

    private static byte[] GenerateSinePcm16(int frequency, float durationSeconds, float levelDb)
    {
        var samples = (int)(SampleRate * durationSeconds);
        var buffer = new short[samples * Channels];
        var amplitude = (float)Math.Pow(10, levelDb / 20.0);
        var twoPi = 2.0 * Math.PI * frequency;
        var fadeSamples = (int)(SampleRate * 0.01);

        for (var i = 0; i < samples; i++)
        {
            var t = i / (double)SampleRate;
            var value = Math.Sin(twoPi * t) * amplitude;
            if (i < fadeSamples)
            {
                value *= i / (double)fadeSamples;
            }
            else if (i > samples - fadeSamples)
            {
                value *= (samples - i) / (double)fadeSamples;
            }

            var sample = (short)(Math.Clamp(value, -1.0, 1.0) * short.MaxValue);
            buffer[i] = sample;
        }

        var bytes = new byte[buffer.Length * 2];
        Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private void UpdateMeterText(AudioMeter meter, Label label)
    {
        var peakDb = meter.SmoothedPeakDb;
        var rmsDb = meter.SmoothedRmsDb;
        var text = $"音量: Peak {peakDb:0.0} dBFS / RMS {rmsDb:0.0} dBFS";
        if (label.InvokeRequired)
        {
            label.BeginInvoke(() => label.Text = text);
        }
        else
        {
            label.Text = text;
        }
    }

    private void UpdateOutputLevel(float peak, float rms)
    {
        var peakDb = 20f * (float)Math.Log10(peak + 1e-9f);
        var rmsDb = 20f * (float)Math.Log10(rms + 1e-9f);
        var text = $"Peak {peakDb:0.0} dBFS / RMS {rmsDb:0.0} dBFS";
        if (_lblOutputLevel.InvokeRequired)
        {
            _lblOutputLevel.BeginInvoke(() => _lblOutputLevel.Text = text);
        }
        else
        {
            _lblOutputLevel.Text = text;
        }
    }

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

    private sealed class PacketEntry
    {
        public PacketType Type { get; }
        public byte[] Payload { get; }

        public PacketEntry(PacketType type, byte[] payload)
        {
            Type = type;
            Payload = payload;
        }
    }

    private void UpdateMeterWarnings(float peak, float rmsDb)
    {
        var warnings = new List<string>();
        if (rmsDb < _vadThresholdDb)
        {
            _lowRmsSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _lowRmsSince.Value).TotalSeconds >= 2)
            {
                warnings.Add("入力不足: RMSが低い可能性");
            }
        }
        else
        {
            _lowRmsSince = null;
        }
        if (peak > ClipThresholdLinear)
        {
            warnings.Add("クリップ注意");
        }

        var text = warnings.Count == 0 ? "" : string.Join(" / ", warnings);
        if (_lblMeterWarning.InvokeRequired)
        {
            _lblMeterWarning.BeginInvoke(() => _lblMeterWarning.Text = text);
        }
        else
        {
            _lblMeterWarning.Text = text;
        }
    }
}
