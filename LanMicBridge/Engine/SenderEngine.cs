using System.Net;
using System.Net.Sockets;
using Concentus.Enums;
using Concentus.Structs;
using LanMicBridge;
using LanMicBridge.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LanMicBridge.Engine;

internal sealed class SenderEngine : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameMs = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000;
    private const int DefaultPort = 48750;
    // Opusは無音でも毎フレーム送る（欠損扱い→PLCノイズの原因を避ける）ため、KeepAlive/VADハングオーバーは不要。

    private const float SendAgcTargetRmsDb = -24f;
    // 小さい声を拾いやすくしつつ、必要なら十分に持ち上げられるようにする
    private const float SendAgcNoBoostBelowDb = -65f;
    private const float SendAgcMaxBoostDb = 24f;
    private const float SendAgcMaxCutDb = -12f;
    private const float SendAgcAttack = 0.12f;
    private const float SendAgcRelease = 0.05f;
    private const float SendNoiseGateFloorDb = -65f;
    private const float SendNoiseGateRangeDb = 8f;

    private const int TestToneHz = 1000;
    private const float TestToneLevelDb = -12f;

    public event Action<string>? StatusChanged;
    public event Action<AudioLevel>? MeterChanged;
    public event Action<string>? Error;

    public CaptureApiMode CaptureApi { get; private set; } = CaptureApiMode.Wasapi;
    public string? CaptureDeviceId { get; private set; }
    public int CaptureMmeIndex { get; private set; } = -1;
    public SendQuality Quality { get; private set; } = SendQuality.Ultra;
    public SendMode Mode { get; private set; } = SendMode.Opus;

    public float SendGain
    {
        get => _sendGain;
        set => _sendGain = value;
    }

    public float VadThresholdDb
    {
        get => _vadThresholdDb;
        set => _vadThresholdDb = value;
    }

    public bool EnableProcessing
    {
        get => _enableProcessing;
        set => _enableProcessing = value;
    }

    public bool SendTestTone
    {
        get => _sendTestTone;
        set => _sendTestTone = value;
    }

    public bool IsRunning => _senderUdp != null;

    private IWaveIn? _capture;
    private BufferedWaveProvider? _captureBuffer;
    private ISampleProvider? _sendSampleProvider;
    private float[]? _sendSampleBuffer;
    private OpusEncoder? _encoder;
    private UdpClient? _senderUdp;
    private CancellationTokenSource? _senderCts;
    private Task? _senderTask;
    private Task? _senderReceiveTask;

    private uint _senderId;
    private uint _sendSequence;
    private DateTime _lastHello = DateTime.MinValue;
    private DateTime _lastAccept = DateTime.MinValue;
    private bool _accepted;

    private int _selectedBitrate = 32000;
    private int _selectedComplexity = 5;

    private volatile float _sendGain = 1.0f;
    private volatile float _vadThresholdDb = -45f;
    private volatile bool _enableProcessing = true;
    private volatile bool _sendTestTone;
    private volatile bool _sendPcmDirect;

    private float _sendAgcGainDb;
    private double _testPhase;
    private DateTime _lastMeterUpdate = DateTime.MinValue;
    private string _lastStatus = string.Empty;

    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _captureDevice;

    public void Configure(CaptureApiMode captureApi, string? captureDeviceId, int captureMmeIndex, SendQuality quality, SendMode mode)
    {
        CaptureApi = captureApi;
        CaptureDeviceId = string.IsNullOrWhiteSpace(captureDeviceId) ? null : captureDeviceId;
        CaptureMmeIndex = captureMmeIndex;
        Quality = quality;
        Mode = mode;
        _sendPcmDirect = mode == SendMode.PcmDirect;
        ApplyQualitySelection();
    }

    public void Start(IPAddress targetIp, int? port = null)
    {
        if (_senderUdp != null)
        {
            return;
        }

        _senderId = (uint)Random.Shared.Next(1, int.MaxValue);
        _sendSequence = 0;
        _accepted = false;
        _lastAccept = DateTime.MinValue;
        _lastHello = DateTime.MinValue;

        try
        {
            _deviceEnumerator ??= new MMDeviceEnumerator();

            _capture = CreateCapture();
            if (_capture == null)
            {
                RaiseError("キャプチャデバイスが選択されていません。");
                Stop();
                return;
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

            var usePort = port ?? DefaultPort;
            _senderUdp = new UdpClient(0);
            _senderUdp.Client.SendBufferSize = 1024 * 1024;
            _senderUdp.Connect(targetIp, usePort);

            _senderCts = new CancellationTokenSource();
            _senderTask = Task.Run(() => SendLoop(_senderCts.Token));
            _senderReceiveTask = Task.Run(() => SenderReceiveLoop(_senderCts.Token));

            _capture.StartRecording();
            SetStatus("接続中");
            AppLogger.Log($"送信開始 IP={targetIp}");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("送信開始失敗", ex);
            RaiseError("送信を開始できませんでした — IPアドレスとネットワーク接続を確認してください。");
            Stop();
        }
    }

    public void Stop()
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
            try
            {
                _capture.StopRecording();
            }
            catch
            {
            }
            _capture.Dispose();
            _capture = null;
        }

        _captureDevice?.Dispose();
        _captureDevice = null;

        _sendSampleProvider = null;
        _sendSampleBuffer = null;
        _captureBuffer = null;
        _encoder = null;

        _senderUdp?.Close();
        _senderUdp?.Dispose();
        _senderUdp = null;

        _accepted = false;
        SetStatus("待機中");
    }

    public void Dispose()
    {
        Stop();
        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;
    }

    public void SetQuality(SendQuality quality)
    {
        Quality = quality;
        ApplyQualitySelection();
    }

    public void SetMode(SendMode mode)
    {
        Mode = mode;
        _sendPcmDirect = mode == SendMode.PcmDirect;
    }

    private IWaveIn? CreateCapture()
    {
        if (CaptureApi == CaptureApiMode.Mme)
        {
            if (CaptureMmeIndex < 0 || CaptureMmeIndex >= WaveInEvent.DeviceCount)
            {
                return null;
            }

            return new WaveInEvent
            {
                DeviceNumber = CaptureMmeIndex,
                BufferMilliseconds = 50,
                NumberOfBuffers = 3
            };
        }

        if (_deviceEnumerator == null || string.IsNullOrWhiteSpace(CaptureDeviceId))
        {
            return null;
        }

        try
        {
            _captureDevice?.Dispose();
            _captureDevice = _deviceEnumerator.GetDevice(CaptureDeviceId);
            return new WasapiCapture(_captureDevice, true, 50);
        }
        catch
        {
            return null;
        }
    }

    private void ApplyQualitySelection()
    {
        switch (Quality)
        {
            case SendQuality.Low:
                _selectedBitrate = 16000;
                _selectedComplexity = 3;
                break;
            case SendQuality.High:
                _selectedBitrate = 64000;
                _selectedComplexity = 8;
                break;
            case SendQuality.Ultra:
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

    private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            AppLogger.LogException("録音停止", e.Exception);
            RaiseError("マイクが切断されました — デバイスを再接続してアプリを再起動してください。");
            Stop();
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

                if (SendTestTone)
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

                // RMS(事前)は「ゲイン適用前」を基準にする（VAD/ゲート判定を素の入力レベルで行う）
                AudioMeter.ComputePeakRms(pcm, out _, out var rmsPre);
                var rmsDbPre = AudioProcessor.LinearToDb(rmsPre);
                var now = DateTime.UtcNow;

                float peak;
                float rms;
                if (EnableProcessing)
                {
                    AudioProcessor.ApplyAgcAndGain(
                        pcm,
                        rmsDbPre,
                        ref _sendAgcGainDb,
                        SendGain,
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
                    AudioProcessor.ApplyGain(pcm, SendGain);
                    AudioMeter.ComputePeakRms(pcm, out peak, out rms);
                }

                var rmsDbPost = AudioProcessor.LinearToDb(rms);
                if ((now - _lastMeterUpdate).TotalMilliseconds >= 200)
                {
                    MeterChanged?.Invoke(new AudioLevel(AudioProcessor.LinearToDb(peak), rmsDbPost));
                    _lastMeterUpdate = now;
                }

                if (_sendPcmDirect)
                {
                    Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
                    var pcmPacket = NetworkProtocol.BuildPcm(_senderId, _sendSequence++, pcmBytes);
                    senderUdp.Send(pcmPacket, pcmPacket.Length);
                    SetStatus(_accepted ? "接続中" : "再接続中");
                }
                else
                {
                    var encoded = encoder!.Encode(pcm, 0, FrameSamples, payload, 0, payload.Length);
                    var packet = NetworkProtocol.BuildAudio(_senderId, _sendSequence++, payload.AsSpan(0, encoded));
                    senderUdp.Send(packet, packet.Length);
                    SetStatus(_accepted ? "接続中" : "再接続中");
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
                SetStatus("エラー");
                RaiseError("送信中にエラーが発生しました — ネットワーク接続を確認してください。復旧しない場合はアプリを再起動してください。");
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
                        SetStatus("接続中");
                    }
                }

                if (_accepted && (DateTime.UtcNow - _lastAccept).TotalSeconds > 5)
                {
                    _accepted = false;
                    SetStatus("再接続中");
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
                RaiseError("受信側との通信にエラーが発生しました — ネットワーク接続を確認してください。");
                await Task.Delay(200, token);
            }
        }
    }

    private void FillTestToneFrame(short[] pcm)
    {
        var amplitude = (float)Math.Pow(10, TestToneLevelDb / 20.0);
        var phaseStep = 2.0 * Math.PI * TestToneHz / SampleRate;

        for (var i = 0; i < pcm.Length; i++)
        {
            var value = Math.Sin(_testPhase) * amplitude;
            _testPhase += phaseStep;
            if (_testPhase >= Math.PI * 2)
            {
                _testPhase -= Math.PI * 2;
            }

            pcm[i] = (short)Math.Round(Math.Clamp(value, -1.0, 1.0) * short.MaxValue);
        }
    }

    private void SetStatus(string text)
    {
        if (string.Equals(text, _lastStatus, StringComparison.Ordinal))
        {
            return;
        }

        _lastStatus = text;
        StatusChanged?.Invoke(text);
    }

    private void RaiseError(string message)
    {
        Error?.Invoke(message);
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

