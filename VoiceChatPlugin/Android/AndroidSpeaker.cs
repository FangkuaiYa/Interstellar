using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Interstellar.VoiceChat;
using UnityEngine;

namespace VoiceChatPlugin.Android;

public class AndroidSpeaker : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    // ~1s ring buffer: 48000 * 1.0 * 2ch = 96000 floats
    private const int RingCapacity = 96000;
    // ~40ms read block: 48000 * 0.04 * 2ch = 3840 floats
    private const int ReadBlock = 3840;

    private readonly ManualSpeaker _manualSpeaker;

    private AudioSource? _audioSource;
    private AudioClip? _clip;
    private GameObject? _gameObject;

    private readonly float[] _ringBuffer = new float[RingCapacity];
    private int _ringWritePos;
    private int _ringReadPos;
    private int _ringCount;
    private readonly object _ringLock = new();

    private readonly float[] _readScratch = new float[ReadBlock];
    private readonly float[] _silenceBlock = new float[ReadBlock];

    private Action<Il2CppStructArray<float>>? _pcmReaderManaged;
    private AudioClip.PCMReaderCallback? _pcmReader;
    private float _diagTimer;
    private int _underrunCount;
    private int _totalCallbacks;
    private bool _loggedFirstCallback;
    private bool _started;
    private bool _disposed;

    public ManualSpeaker Speaker => _manualSpeaker;

    public AndroidSpeaker()
    {
        _manualSpeaker = new ManualSpeaker(null);
    }

    public void Start()
    {
        if (_started) return;

        _pcmReaderManaged = new Action<Il2CppStructArray<float>>(OnPcmRead);
        _pcmReader = DelegateSupport.ConvertDelegate<AudioClip.PCMReaderCallback>(_pcmReaderManaged);
        if (_pcmReader == null)
            throw new InvalidOperationException("Failed to create IL2CPP PCM reader callback.");

        // Buffer 1 second of stereo audio for the streaming clip
        _clip = AudioClip.Create("VC_Out", SampleRate, Channels, SampleRate, true, _pcmReader);

        _gameObject = new GameObject("VC_AndroidSpeaker");
        UnityEngine.Object.DontDestroyOnLoad(_gameObject);

        _audioSource = _gameObject.AddComponent<AudioSource>();
        _audioSource.loop = true;
        _audioSource.volume = 1f;
        _audioSource.spatialBlend = 0f;
        _audioSource.clip = _clip;
        _audioSource.Play();

        _started = true;
        VoiceChatPluginMain.Logger.LogInfo($"[VC:AndroidSpk] Started: {SampleRate}Hz stereo, ring={RingCapacity}");
    }

    public void Update()
    {
        if (!_started || _disposed) return;

        try
        {
            _manualSpeaker.Read(_readScratch);
        }
        catch
        {
            Array.Copy(_silenceBlock, _readScratch, ReadBlock);
        }

        lock (_ringLock)
        {
            if (_ringCount + ReadBlock > RingCapacity)
            {
                int drop = _ringCount + ReadBlock - RingCapacity;
                _ringReadPos = (_ringReadPos + drop) % RingCapacity;
                _ringCount -= drop;
            }

            int first = Math.Min(ReadBlock, RingCapacity - _ringWritePos);
            Array.Copy(_readScratch, 0, _ringBuffer, _ringWritePos, first);
            int remain = ReadBlock - first;
            if (remain > 0)
                Array.Copy(_readScratch, first, _ringBuffer, 0, remain);
            _ringWritePos = (_ringWritePos + ReadBlock) % RingCapacity;
            _ringCount += ReadBlock;
        }

        _diagTimer += Time.unscaledDeltaTime;
        if (_diagTimer > 3f)
        {
            _diagTimer = 0f;
            float bufMs;
            lock (_ringLock) { bufMs = (float)_ringCount / (SampleRate * Channels) * 1000f; }
            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC:AndroidSpk] ring={bufMs:F0}ms cb={_totalCallbacks} underruns={_underrunCount}");
        }
    }

    private void OnPcmRead(Il2CppStructArray<float> data)
    {
        int needed = data.Length;
        int written = 0;

        lock (_ringLock)
        {
            while (written < needed && _ringCount > 0)
            {
                int toCopy = Math.Min(needed - written, Math.Min(_ringCount, RingCapacity - _ringReadPos));
                for (int i = 0; i < toCopy; i++)
                    data[written + i] = _ringBuffer[_ringReadPos + i];
                written += toCopy;
                _ringReadPos = (_ringReadPos + toCopy) % RingCapacity;
                _ringCount -= toCopy;
            }
        }

        if (written < needed)
        {
            _underrunCount++;
            for (int i = written; i < needed; i++)
                data[i] = 0f;
        }

        _totalCallbacks++;
        if (!_loggedFirstCallback)
        {
            _loggedFirstCallback = true;
            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC:AndroidSpk] First PCM callback: needed={needed} written={written}");
        }
    }

    public void Stop()
    {
        if (_audioSource != null)
        {
            _audioSource.Stop();
            _audioSource.clip = null;
        }
        if (_clip != null)
        {
            UnityEngine.Object.Destroy(_clip);
            _clip = null;
        }
        if (_gameObject != null)
        {
            UnityEngine.Object.Destroy(_gameObject);
            _gameObject = null;
        }
        _audioSource = null;
        _started = false;

        lock (_ringLock)
        {
            _ringWritePos = 0;
            _ringReadPos = 0;
            _ringCount = 0;
        }

        VoiceChatPluginMain.Logger.LogInfo("[VC:AndroidSpk] Speaker stopped.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
