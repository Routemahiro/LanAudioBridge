using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Concentus.Structs;
using LanMicBridge;
using LanMicBridge.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LanMicBridge.Engine;

internal sealed class ReceiverEngine : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameMs = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000;
    private const int DefaultPort = 48750;
    private const float ClipThresholdLinear = 0.89f;

    private const float AgcTargetRmsDb = -20f;
    private const float AgcNoBoostBelowDb = -50f;
    private const float AgcMaxBoostDb = 24f;
    private const float AgcMaxCutDb = -18f;
    private const float AgcAttack = 0.25f;
    private const float AgcRelease = 0.08f;
    private const float NoiseGateFloorDb = -60f;
    private const float NoiseGateRangeDb = 10f;

    private const int TestToneHz = 1000;
    private const float TestToneLevelDb = -12f;

    public event Action<string>? StatusChanged;
    public event Action<AudioLevel>? InputLevelChanged;
    public event Action<AudioLevel>? OutputLevelChanged;
    public event Action<string>? WarningChanged;
    public event Action<ReceiverStats>? StatsUpdated;
    public event Action<string>? Error;

    public string? OutputDeviceId { get; private set; }

    public JitterMode JitterMode { get; private set; } = JitterMode.LowLatency;

    public int Port { get; private set; } = DefaultPort;

    public float OutputGain
    {
        get => _outputGain;
        set => _outputGain = value;
    }

    public int OutputForceStartMs
    {
        get => _outputForceStartMs;
        set => _outputForceStartMs = Math.Clamp(value, 200, 5000);
    }

    public bool EnableProcessing
    {
        get => _enableProcessing;
        set => _enableProcessing = value;
    }

    public float VadThresholdDb
    {
        get => _vadThresholdDb;
        set => _vadThresholdDb = value;
    }

    public bool IsRunning => _receiverUdp != null;

    private readonly AudioMeter _inputMeter = new();
    private readonly Stopwatch _receiverClock = new();

    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _renderDevice;

    private WasapiOut? _output;
    private BufferedWaveProvider? _playBuffer;
    private UdpClient? _receiverUdp;
    private CancellationTokenSource? _receiverCts;
    private Task? _receiverTask;
    private OpusDecoder? _decoder;

    private readonly object _jitterLock = new();
    private readonly SortedDictionary<uint, PacketEntry> _jitterBuffer = new();
    private CancellationTokenSource? _playoutCts;
    private Task? _playoutTask;
    private Task? _statsTask;
    private Task? _receiverStatusTask;
    private Task? _silenceTask;

    private uint _playoutSequence;
    private bool _playoutInitialized;
    private bool _playoutBuffering = true;
    private DateTime _playoutBufferingSince = DateTime.MinValue;
    private int _baseJitterFrames;
    private int _adaptiveJitterFrames;
    private int _minJitterFrames;
    private int _maxJitterFrames;
    private int _jitterWindowFrames;
    private int _jitterMissesWindow;
    private PacketType _lastPacketType = PacketType.Audio;

    private long _packetsReceived;
    private long _packetsLost;
    private long _bytesReceived;
    private double _jitterMs;
    private double _lastTransitMs;
    private DateTime _lastPacketTime = DateTime.MinValue;
    private bool _hadConnection;
    private IPEndPoint? _lastSenderEndpoint;
    private DateTime _lastStatsLogged = DateTime.MinValue;

    private bool _outputStarted;
    private int _prebufferMs;
    private int _rebufferThresholdMs;
    private DateTime _outputStartPendingSince = DateTime.MinValue;
    private DateTime _suppressNetworkAudioUntil = DateTime.MinValue;

    private DateTime? _lowRmsSince;
    private string _lastWarning = string.Empty;
    private string _lastStatus = string.Empty;

    private volatile float _outputGain = 1.0f;
    private volatile int _outputForceStartMs = 1000;
    private volatile bool _enableProcessing = true;
    private volatile float _vadThresholdDb = -45f;
    private float _recvAgcGainDb;

    public void Configure(string? outputDeviceId, JitterMode jitterMode, int? port = null)
    {
        OutputDeviceId = string.IsNullOrWhiteSpace(outputDeviceId) ? null : outputDeviceId;
        JitterMode = jitterMode;
        if (port.HasValue)
        {
            Port = port.Value;
        }
    }

    public void Start()
    {
        if (_receiverUdp != null)
        {
            return;
        }

        try
        {
            _deviceEnumerator ??= new MMDeviceEnumerator();
            _decoder = OpusDecoder.Create(SampleRate, Channels);
            _receiverUdp = new UdpClient(Port);
            _receiverUdp.Client.ReceiveBufferSize = 1024 * 1024;

            _receiverCts = new CancellationTokenSource();
            _receiverTask = Task.Run(() => ReceiveLoop(_receiverCts.Token));

            _playoutCts = new CancellationTokenSource();
            _playoutTask = Task.Run(() => PlayoutLoop(_playoutCts.Token));

            _statsTask = Task.Run(() => StatsLoop(_receiverCts.Token));
            _receiverStatusTask = Task.Run(() => ReceiverStatusLoop(_receiverCts.Token));
            _silenceTask = Task.Run(() => SilenceLoop(_receiverCts.Token));

            _receiverClock.Restart();
            ResetPlayoutState();
            RestartOutput();
            SetStatus("待受中");
            AppLogger.Log("受信待受開始");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("受信開始失敗", ex);
            SetStatus("エラー: 受信開始失敗");
            RaiseError("受信を開始できませんでした — ポートが別のアプリに使われている可能性があります。アプリを再起動してください。");
            Stop();
        }
    }

    public void Stop()
    {
        _receiverCts?.Cancel();
        WaitTaskSafely(_receiverTask, 500);
        WaitTaskSafely(_statsTask, 500);
        WaitTaskSafely(_receiverStatusTask, 500);
        WaitTaskSafely(_silenceTask, 500);
        _receiverTask = null;
        _statsTask = null;
        _receiverStatusTask = null;
        _silenceTask = null;
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

        StopOutput();

        _packetsReceived = 0;
        _packetsLost = 0;
        _bytesReceived = 0;
        _hadConnection = false;
        ResetPlayoutState();
        SetStatus("待受中");
    }

    public void Dispose()
    {
        Stop();
        _decoder = null;
        _renderDevice?.Dispose();
        _renderDevice = null;
        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;
    }

    public void SetOutputDevice(string? outputDeviceId)
    {
        OutputDeviceId = string.IsNullOrWhiteSpace(outputDeviceId) ? null : outputDeviceId;
        if (IsRunning)
        {
            RestartOutput();
        }
    }

    public void SetJitterMode(JitterMode jitterMode)
    {
        JitterMode = jitterMode;
        if (IsRunning)
        {
            RestartOutput();
        }
    }

    public async Task<bool> PlayCheckToneAsync(CancellationToken cancellationToken = default)
    {
        if (_playBuffer == null)
        {
            RaiseError("出力デバイスが初期化されていません。");
            return false;
        }

        try
        {
            _suppressNetworkAudioUntil = DateTime.UtcNow.AddSeconds(1.1);
            var tone = GenerateSinePcm16(TestToneHz, 1.0f, TestToneLevelDb);
            _playBuffer.AddSamples(tone, 0, tone.Length);
            var pass = await RunLoopbackCheckAsync(cancellationToken);
            AppLogger.Log($"チェック音結果 {(pass ? "PASS" : "FAIL")}");
            return pass;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("チェック音失敗", ex);
            RaiseError("チェック音の再生に失敗しました — 出力デバイスの設定を確認してください。");
            return false;
        }
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
                SetStatus("エラー: 受信エラー");
                RaiseError("受信中にエラーが発生しました — ネットワーク接続を確認してください。復旧しない場合はアプリを再起動してください。");
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
                EnqueuePacket(seq, PacketType.Audio, payload);
                break;
            case PacketType.Pcm:
                EnqueuePacket(seq, PacketType.Pcm, payload);
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
                    (DateTime.UtcNow - _playoutBufferingSince).TotalMilliseconds >= OutputForceStartMs;
                if (bufferedEnough || bufferedTooLong)
                {
                    _playoutBuffering = false;
                    next = stopwatch.Elapsed;
                    AppLogger.Log($"再生開始 JitterFrames={_adaptiveJitterFrames} Buffered={bufferedCount} Wait={OutputForceStartMs}ms");
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
                DecodeAndPlay(entry.Payload, entry.Payload.Length, lost: false, useFec: false);
            }
            else
            {
                ProcessPcmPayload(entry.Payload);
            }

            AdjustJitterTarget(gotPacket: true);
            return;
        }

        _packetsLost++;

        if (TryPeekPacket(sequence + 1, out var next) && next != null && next.Type == PacketType.Audio)
        {
            DecodeAndPlay(next.Payload, next.Payload.Length, lost: false, useFec: true);
            return;
        }

        if (_lastPacketType == PacketType.Pcm)
        {
            ProcessPcmAndPlay(new short[FrameSamples]);
        }
        else
        {
            DecodeAndPlay(Array.Empty<byte>(), 0, lost: true, useFec: false);
        }

        AdjustJitterTarget(gotPacket: false);
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

        AudioMeter.ComputePeakRms(pcm, out var peakIn, out var rmsIn);
        _inputMeter.Update(peakIn, rmsIn);
        var inputLevel = new AudioLevel(_inputMeter.SmoothedPeakDb, _inputMeter.SmoothedRmsDb);
        InputLevelChanged?.Invoke(inputLevel);
        UpdateWarnings(peakIn, inputLevel.RmsDb);

        var rmsDbPre = AudioProcessor.LinearToDb(rmsIn);
        float outPeak;
        float outRms;
        if (EnableProcessing)
        {
            AudioProcessor.ApplyAgcAndGain(
                pcm,
                rmsDbPre,
                ref _recvAgcGainDb,
                OutputGain,
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
            AudioProcessor.ApplyGain(pcm, OutputGain);
            AudioMeter.ComputePeakRms(pcm, out outPeak, out outRms);
        }

        var outputLevel = new AudioLevel(AudioProcessor.LinearToDb(outPeak), AudioProcessor.LinearToDb(outRms));
        OutputLevelChanged?.Invoke(outputLevel);

        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _playBuffer.AddSamples(bytes, 0, bytes.Length);
        MaybeStartOutput();
    }

    private void UpdateWarnings(float peakLinear, float rmsDb)
    {
        var warnings = new List<string>();
        if (rmsDb < VadThresholdDb)
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

        if (peakLinear > ClipThresholdLinear)
        {
            warnings.Add("クリップ注意");
        }

        var text = warnings.Count == 0 ? "" : string.Join(" / ", warnings);
        if (!string.Equals(text, _lastWarning, StringComparison.Ordinal))
        {
            _lastWarning = text;
            WarningChanged?.Invoke(text);
        }
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

    private async Task StatsLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token);
                UpdateStats();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.LogException("統計更新失敗", ex);
            }
        }
    }

    private void UpdateStats()
    {
        var packets = _packetsReceived;
        var lossPercent = packets + _packetsLost == 0 ? 0 : (int)(_packetsLost * 100.0 / (packets + _packetsLost));
        var jitter = (int)Math.Round(_jitterMs);
        var delay = _playBuffer != null ? (int)_playBuffer.BufferedDuration.TotalMilliseconds : 0;
        StatsUpdated?.Invoke(new ReceiverStats(packets, lossPercent, jitter, delay));

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

    private async Task ReceiverStatusLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, token);
                UpdateReceiverStatus();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.LogException("受信状態更新失敗", ex);
            }
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

    private async Task SilenceLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(100, token);
                EnsureSilence();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.LogException("無音維持失敗", ex);
            }
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

        if ((DateTime.UtcNow - _outputStartPendingSince).TotalMilliseconds >= OutputForceStartMs)
        {
            _output.Play();
            _outputStarted = true;
            _outputStartPendingSince = DateTime.MinValue;
            AppLogger.Log($"出力強制開始 Buffered={bufferedMs:0}ms");
        }
    }

    private void RestartOutput()
    {
        try
        {
            StopOutput();

            if (_deviceEnumerator == null || string.IsNullOrWhiteSpace(OutputDeviceId))
            {
                return;
            }

            try
            {
                _renderDevice?.Dispose();
                _renderDevice = _deviceEnumerator.GetDevice(OutputDeviceId);
            }
            catch
            {
                _renderDevice = null;
                return;
            }

            var latency = JitterMode switch
            {
                JitterMode.Stable => 140,
                JitterMode.UltraStable => 220,
                _ => 60
            };
            var bufferSeconds = JitterMode switch
            {
                JitterMode.Stable => 3.5,
                JitterMode.UltraStable => 4.5,
                _ => 1.8
            };

            _output = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, true, latency);
            _playBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(bufferSeconds)
            };
            _output.Init(_playBuffer);
            _outputStarted = false;
            _prebufferMs = JitterMode switch
            {
                JitterMode.Stable => 220,
                JitterMode.UltraStable => 320,
                _ => 120
            };
            _baseJitterFrames = Math.Max(3, _prebufferMs / FrameMs);
            _adaptiveJitterFrames = _baseJitterFrames;
            _minJitterFrames = Math.Max(3, _baseJitterFrames - 2);
            _maxJitterFrames = _baseJitterFrames + 8;
            _rebufferThresholdMs = Math.Max(40, _prebufferMs / 2);
            AppLogger.Log($"出力初期化 Device={_renderDevice.FriendlyName} Latency={latency}ms Prebuffer={_prebufferMs}ms JitterFrames={_baseJitterFrames}");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("出力初期化失敗", ex);
            SetStatus("エラー: 出力初期化失敗");
            RaiseError("出力デバイスの初期化に失敗しました — デバイスが取り外されていないか確認してください。");
        }
    }

    private void StopOutput()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _playBuffer = null;
        _outputStarted = false;
    }

    private async Task<bool> RunLoopbackCheckAsync(CancellationToken cancellationToken)
    {
        // CABLE Output を探して録音し、一定時間内のRMSで判定する
        if (_deviceEnumerator == null)
        {
            return false;
        }

        MMDevice? device = null;
        try
        {
            foreach (var d in _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                var name = d.FriendlyName ?? string.Empty;
                if (name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase))
                {
                    device = d;
                    break;
                }
                d.Dispose();
            }
        }
        catch
        {
            device?.Dispose();
            device = null;
        }

        if (device == null)
        {
            RaiseError("CABLE Output が見つかりません。VB-CABLEのインストールを確認してください。");
            return false;
        }

        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var capture = new WasapiCapture(device);
            var maxRmsDb = -120f;

            capture.DataAvailable += (_, args) =>
            {
                ReadOnlySpan<byte> buffer = args.Buffer.AsSpan(0, args.BytesRecorded);
                ConvertToPcm16(buffer, capture.WaveFormat, out var pcm);
                AudioMeter.ComputePeakRms(pcm, out var peak, out var rms);
                var rmsDb = AudioProcessor.LinearToDb(rms);
                if (rmsDb > maxRmsDb)
                {
                    maxRmsDb = rmsDb;
                }
            };

            capture.RecordingStopped += (_, _) =>
            {
                capture.Dispose();
                device.Dispose();
                var pass = maxRmsDb >= VadThresholdDb;
                tcs.TrySetResult(pass);
            };

            capture.StartRecording();
            await Task.Delay(1100, cancellationToken);
            capture.StopRecording();
            return await tcs.Task;
        }
        catch
        {
            device.Dispose();
            return false;
        }
    }

    private static void ConvertToPcm16(ReadOnlySpan<byte> buffer, WaveFormat format, out short[] pcm)
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
}

