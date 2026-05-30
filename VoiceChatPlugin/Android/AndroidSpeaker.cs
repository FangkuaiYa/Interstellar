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
    private const int RingCapacity = 96000;
    private const int ReadBlock = 1920;
    private const int PrefillTarget = 32768;

    private readonly ManualSpeaker _manualSpeaker;

    private AudioSource? _audioSource;
    private AudioClip? _clip;
    private GameObject? _gameObject;

    private readonly float[] _ring = new float[RingCapacity];
    private int _wpos, _rpos, _count;
    private readonly object _lock = new();

    private readonly float[] _scratch = new float[ReadBlock];
    private readonly float[] _silence = new float[ReadBlock];

    private Action<Il2CppStructArray<float>>? _pcmManaged;
    private AudioClip.PCMReaderCallback? _pcmCb;
    private bool _started, _playStarted, _disposed;
    private float _diagTimer;
    private int _cbCount, _urCount, _lastCb, _lastUr;

    public ManualSpeaker Speaker => _manualSpeaker;

    public AndroidSpeaker()
    {
        _manualSpeaker = new ManualSpeaker(null);
    }

    public void Start()
    {
        if (_started) return;

        _pcmManaged = new Action<Il2CppStructArray<float>>(OnPcmRead);
        _pcmCb = DelegateSupport.ConvertDelegate<AudioClip.PCMReaderCallback>(_pcmManaged);
        if (_pcmCb == null)
            throw new InvalidOperationException("Failed to create IL2CPP PCM reader callback.");

        _clip = AudioClip.Create("VC_Out", SampleRate / 4, Channels, SampleRate, true, _pcmCb);

        _gameObject = new GameObject("VC_AndroidSpeaker");
        UnityEngine.Object.DontDestroyOnLoad(_gameObject);

        _audioSource = _gameObject.AddComponent<AudioSource>();
        _audioSource.loop = true;
        _audioSource.volume = 1f;
        _audioSource.spatialBlend = 0f;
        _audioSource.clip = _clip;

        _started = true;
        VoiceChatPluginMain.Logger.LogInfo($"[VC:AndroidSpk] Ring buffer: cap={RingCapacity} block={ReadBlock} prefill={PrefillTarget}");
    }

    public void Update()
    {
        if (!_started || _disposed) return;

        try { _manualSpeaker.Read(_scratch); }
        catch { Array.Copy(_silence, _scratch, ReadBlock); }

        lock (_lock)
        {
            if (_count + ReadBlock > RingCapacity)
            {
                int drop = _count + ReadBlock - RingCapacity;
                _rpos = (_rpos + drop) % RingCapacity;
                _count -= drop;
            }

            int first = Math.Min(ReadBlock, RingCapacity - _wpos);
            Array.Copy(_scratch, 0, _ring, _wpos, first);
            int remain = ReadBlock - first;
            if (remain > 0) Array.Copy(_scratch, first, _ring, 0, remain);
            _wpos = (_wpos + ReadBlock) % RingCapacity;
            _count += ReadBlock;

            if (!_playStarted && _count >= PrefillTarget)
            {
                _playStarted = true;
                _audioSource?.Play();
                VoiceChatPluginMain.Logger.LogInfo($"[VC:AndroidSpk] Play after prefill, ring={_count * 1000f / (SampleRate * Channels):F0}ms");
            }
        }

        _diagTimer += Time.unscaledDeltaTime;
        if (_diagTimer > 3f)
        {
            _diagTimer = 0f;
            float ms;
            int cb, ur;
            lock (_lock) { ms = (float)_count / (SampleRate * Channels) * 1000f; }
            cb = _cbCount - _lastCb; _lastCb = _cbCount;
            ur = _urCount - _lastUr; _lastUr = _urCount;
            VoiceChatPluginMain.Logger.LogInfo(
                $"[VC:AndroidSpk] ring={ms:F0}ms cb+{cb} ur+{ur} totalUR={_urCount}");
        }
    }

    private void OnPcmRead(Il2CppStructArray<float> data)
    {
        int needed = data.Length;
        int written = 0;

        lock (_lock)
        {
            while (written < needed && _count > 0)
            {
                int n = Math.Min(needed - written, Math.Min(_count, RingCapacity - _rpos));
                for (int i = 0; i < n; i++)
                    data[written + i] = _ring[_rpos + i];
                written += n;
                _rpos = (_rpos + n) % RingCapacity;
                _count -= n;
            }
        }

        if (written < needed)
        {
            _urCount++;
            for (int i = written; i < needed; i++) data[i] = 0f;
        }

        _cbCount++;
    }

    public void Stop()
    {
        if (_audioSource != null) { _audioSource.Stop(); _audioSource.clip = null; }
        if (_clip != null) { UnityEngine.Object.Destroy(_clip); _clip = null; }
        if (_gameObject != null) { UnityEngine.Object.Destroy(_gameObject); _gameObject = null; }
        _audioSource = null; _started = false; _playStarted = false;
        lock (_lock) { _wpos = _rpos = _count = 0; }
        VoiceChatPluginMain.Logger.LogInfo("[VC:AndroidSpk] Stopped.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
