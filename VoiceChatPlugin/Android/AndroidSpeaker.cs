using System;
using Il2CppInterop.Runtime.Injection;
using Interstellar.VoiceChat;
using UnityEngine;

namespace VoiceChatPlugin.Android;

public class AndroidSpeakerBehaviour : MonoBehaviour
{
    private AudioSource? _audioSource;
    private AudioClip? _silenceClip;
    private ManualSpeaker? _speaker;
    private int _outputSampleRate = 48000;
    private int _outputChannels = 1;
    private bool _started;

    static AndroidSpeakerBehaviour()
    {
        ClassInjector.RegisterTypeInIl2Cpp<AndroidSpeakerBehaviour>();
    }

    public void Initialise(ManualSpeaker speaker, int sampleRate = 48000, int channels = 1)
    {
        _speaker = speaker;
        _outputSampleRate = sampleRate;
        _outputChannels = channels;

        _audioSource = gameObject.AddComponent<AudioSource>();
        _audioSource.loop = true;
        _audioSource.volume = 1f;
        _audioSource.spatialBlend = 0f;

        int clipSamples = _outputSampleRate;
        _silenceClip = AudioClip.Create(
            "VC_AndroidSpeaker_Silence", clipSamples, _outputChannels, _outputSampleRate, false);

        float[] silence = new float[clipSamples * _outputChannels];
        _silenceClip.SetData(silence, 0);

        _audioSource.clip = _silenceClip;
        _audioSource.Play();
        _started = true;

        VoiceChatPluginMain.Logger.LogInfo(
            $"[VC:AndroidSpk] Speaker started: {_outputSampleRate} Hz, {_outputChannels} ch");
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (_speaker == null || !_started)
        {
            Array.Clear(data, 0, data.Length);
            return;
        }

        try
        {
            _speaker.Read(data);

            if (_outputChannels == 1 && channels == 2)
            {
                int monoLen = data.Length / 2;
                for (int i = monoLen - 1; i >= 0; i--)
                {
                    float sample = data[i];
                    data[i * 2] = sample;
                    data[i * 2 + 1] = sample;
                }
            }
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogWarning($"[VC:AndroidSpk] Read error: {ex.Message}");
            Array.Clear(data, 0, data.Length);
        }
    }

    private void OnDestroy()
    {
        if (_audioSource != null)
        {
            _audioSource.Stop();
            _audioSource.clip = null;
        }

        if (_silenceClip != null)
        {
            Destroy(_silenceClip);
            _silenceClip = null;
        }

        _started = false;
        VoiceChatPluginMain.Logger.LogInfo("[VC:AndroidSpk] Speaker destroyed.");
    }

    public bool IsPlaying => _started && _audioSource != null && _audioSource.isPlaying;
}

public static class AndroidSpeakerFactory
{
    public static (ManualSpeaker speaker, GameObject gameObject) Create(
        int sampleRate = 48000, int channels = 1)
    {
        var go = new GameObject("VC_AndroidSpeaker");
        UnityEngine.Object.DontDestroyOnLoad(go);

        var manualSpeaker = new ManualSpeaker(null);
        var behaviour = go.AddComponent<AndroidSpeakerBehaviour>();
        behaviour.Initialise(manualSpeaker, sampleRate, channels);

        return (manualSpeaker, go);
    }
}
