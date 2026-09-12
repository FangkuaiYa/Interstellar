using System;
using System.Threading;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Interstellar.Voice;
using UnityEngine;

namespace Interstellar.Android;

public class AndroidSpeaker : IDisposable
{
    private const int SourceSampleRate = 48000;
    private const int Channels = 2;

    private const float JitterBufferSeconds = 0.05f; // ~50ms of headroom
    private float[] _ring = Array.Empty<float>();
    private int _ringWrite, _ringRead, _ringCount, _ringCapacity;
    private readonly object _ringLock = new();

    private readonly ManualSpeaker _manualSpeaker;

    private AudioSource? _audioSource;
    private AudioClip? _clip;
    private GameObject? _gameObject;

    private int _deviceSampleRate = SourceSampleRate;
    private double _resamplePos;

    private float[] _callbackScratch = Array.Empty<float>();
    private float[] _networkScratch = Array.Empty<float>();

    private Action<Il2CppStructArray<float>>? _pcmManaged;
    private AudioClip.PCMReaderCallback? _pcmCb;
    private bool _started, _disposed;
    private float _diagTimer;
    private int _cbCount, _urCount, _lastCb, _lastUr;

    public ManualSpeaker Speaker => _manualSpeaker;

    public AndroidSpeaker()
    {
        _manualSpeaker = new ManualSpeaker(null);
    }

    public void Setup()
    {
        _gameObject = new GameObject("VC_AndroidSpeaker");
        UnityEngine.Object.DontDestroyOnLoad(_gameObject);

        _audioSource = _gameObject.AddComponent<AudioSource>();
        _audioSource.loop = true;
        _audioSource.volume = 1f;
        _audioSource.spatialBlend = 0f;
    }

    public void StartPlayback()
    {
        if (_started) return;

        // Query the real device output rate. Falls back to 48000 if the
        // platform reports 0 (some OEM ROMs do this before first playback).
        _deviceSampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : SourceSampleRate;

        _ringCapacity = Math.Max(1, (int)(_deviceSampleRate * Channels * JitterBufferSeconds));
        _ring = new float[_ringCapacity];
        _ringWrite = _ringRead = _ringCount = 0;
        _resamplePos = 0;

        // Create AudioClip AFTER Initialize so PCM callback never fires before speakerContext is set
        _pcmManaged = new Action<Il2CppStructArray<float>>(OnPcmRead);
        _pcmCb = DelegateSupport.ConvertDelegate<AudioClip.PCMReaderCallback>(_pcmManaged);
        if (_pcmCb == null)
            throw new InvalidOperationException("Failed to create IL2CPP PCM reader callback.");

        _clip = AudioClip.Create("VC_Out", _deviceSampleRate / 4, Channels, _deviceSampleRate, true, _pcmCb);
        _audioSource!.clip = _clip;
        _audioSource!.Play();
        _started = true;
        InterstellarPlugin.Logger.LogInfo($"[VC:AndroidSpk] Started PCM callback speaker (device rate={_deviceSampleRate}Hz, jitter buffer={JitterBufferSeconds * 1000f:F0}ms).");
    }

    /// <summary>Call early to ensure AudioSource is created and ready
    /// before the WebSocket connection completes.</summary>
    public void Warmup()
    {
        if (!_started && !_disposed && _gameObject != null)
        {
            StartPlayback();
        }
    }

    public void Update()
    {
        if (!_started || _disposed) return;

        _diagTimer += Time.unscaledDeltaTime;
        if (_diagTimer > 3f)
        {
            _diagTimer = 0f;
            int callbacks = Volatile.Read(ref _cbCount);
            int underruns = Volatile.Read(ref _urCount);
            int cb = callbacks - _lastCb;
            int ur = underruns - _lastUr;
            _lastCb = callbacks;
            _lastUr = underruns;
            InterstellarPlugin.Logger.LogInfo(
                $"[VC:AndroidSpk] cb+{cb} ur+{ur} totalUR={underruns}");
        }
    }

    private void OnPcmRead(Il2CppStructArray<float> data)
    {
        int wantSourceSamples = (int)Math.Ceiling(data.Length * ((double)SourceSampleRate / _deviceSampleRate)) + Channels * 64;
        if (_networkScratch.Length < wantSourceSamples) _networkScratch = new float[wantSourceSamples];

        try
        {
            _manualSpeaker.Read(new ArraySegment<float>(_networkScratch, 0, wantSourceSamples));
        }
        catch
        {
            Interlocked.Increment(ref _urCount);
            Array.Clear(_networkScratch, 0, wantSourceSamples);
        }

        lock (_ringLock)
        {
            for (int i = 0; i < wantSourceSamples; i++)
            {
                _ring[_ringWrite] = _networkScratch[i];
                _ringWrite = (_ringWrite + 1) % _ringCapacity;
                if (_ringCount < _ringCapacity) _ringCount++;
                else _ringRead = (_ringRead + 1) % _ringCapacity; // drop oldest on overflow, keep buffer bounded
            }
        }

        if (_callbackScratch.Length != data.Length) _callbackScratch = new float[data.Length];

        double ratio = (double)SourceSampleRate / _deviceSampleRate;
        int frames = data.Length / Channels;
        bool underrun = false;

        lock (_ringLock)
        {
            int ringFrames = _ringCount / Channels;
            for (int f = 0; f < frames; f++)
            {
                int idx0 = (int)_resamplePos;
                float frac = (float)(_resamplePos - idx0);

                if (idx0 + 1 >= ringFrames)
                {
                    // Not enough buffered audio — fill silence rather than
                    // reading garbage, and keep resample position pinned so
                    // we don't skip ahead once more data arrives.
                    underrun = true;
                    for (int c = 0; c < Channels; c++) _callbackScratch[f * Channels + c] = 0f;
                    continue;
                }

                for (int c = 0; c < Channels; c++)
                {
                    int s0 = (_ringRead + idx0 * Channels + c) % _ringCapacity;
                    int s1 = (_ringRead + (idx0 + 1) * Channels + c) % _ringCapacity;
                    float a = _ring[s0], b = _ring[s1];
                    _callbackScratch[f * Channels + c] = a + (b - a) * frac;
                }

                _resamplePos += ratio;
            }

            int consumedFrames = (int)_resamplePos;
            if (consumedFrames > 0)
            {
                int consumedSamples = Math.Min(consumedFrames * Channels, _ringCount);
                _ringRead = (_ringRead + consumedSamples) % _ringCapacity;
                _ringCount -= consumedSamples;
                _resamplePos -= consumedFrames;
            }
        }

        if (underrun) Interlocked.Increment(ref _urCount);

        // Android AudioTrack output is significantly quieter than desktop.
        // 2x gain brings speech to a usable level; Clamp prevents hard clipping.
        const float androidSpeakerGain = 2f;
        for (int i = 0; i < data.Length; i++)
        {
            float sample = _callbackScratch[i] * androidSpeakerGain;
            data[i] = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
        }

        Interlocked.Increment(ref _cbCount);
    }

    public void Stop()
    {
        if (_audioSource != null) { _audioSource.Stop(); _audioSource.clip = null; }
        if (_clip != null) { UnityEngine.Object.Destroy(_clip); _clip = null; }
        if (_gameObject != null) { UnityEngine.Object.Destroy(_gameObject); _gameObject = null; }
        _audioSource = null;
        _started = false;
        InterstellarPlugin.Logger.LogInfo("[VC:AndroidSpk] Stopped.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
