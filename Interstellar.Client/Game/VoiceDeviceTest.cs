using System;
using Interstellar.Audio;
using NAudio.Wave;
using UnityEngine;

namespace Interstellar.Voice;

/// <summary>
/// Device tests driven from the settings panel:
///   • microphone test — opens the selected input directly so the panel's level
///     meter moves while you speak. Works in the main menu, where no voice room
///     exists yet; inside a game the meter simply reads the room's own mic level
///     instead (the room already has the device open — no second capture handle).
///   • speaker test — plays a short chime through the selected output device.
///
/// Both open devices directly with Interstellar's own audio classes, so there is
/// no native sidecar involved. Everything is released when the test stops, when
/// the panel closes and when a voice room starts (a leftover capture handle
/// would keep the mic "in use" indicator lit after the test ends).
/// </summary>
internal static class VoiceDeviceTest
{
    private const int SpeakerTestMs = 1400;
    private const int SampleRate = 48000;

    private static IMicrophone? _mic;
    private static WindowsSpeaker? _speaker;
    private static int _speakerStopTick;

    /// <summary>True while a standalone capture handle is open (menu test).</summary>
    public static bool MicRunning => _mic != null;
    public static bool SpeakerPlaying => _speaker != null;

    /// <summary>0..1 input level for the panel's meter: the standalone test
    /// capture while it runs, otherwise the room's live mic level.</summary>
    public static float MicLevel
    {
        get
        {
            try
            {
                if (_mic != null) return Mathf.Clamp01(_mic.Level);
                var room = VoiceRoom.Current;
                if (room != null && room.UsingMicrophone)
                    return Mathf.Clamp01(room.LocalMicLevel);
            }
            catch { }
            return 0f;
        }
    }

    /// <summary>True when the meter has a live source (test capture or a room
    /// with an open mic) — used to decide whether the start button is offered.</summary>
    public static bool RoomOwnsMicrophone
        => VoiceRoom.Current != null && VoiceRoom.Current.UsingMicrophone;

    public static void ToggleMicTest()
    {
        if (_mic != null) { StopMicTest(); return; }

        try
        {
            var mic = new WindowsMicrophone(VoiceConfig.MicrophoneDevice);
            ((IMicrophone)mic).SetVolume(1f);
            ((IMicrophone)mic).Initialize(new MicContext());
            _mic = mic;
            InterstellarPlugin.Logger.LogInfo("[VC:Test] Microphone test started.");
        }
        catch (Exception ex)
        {
            _mic = null;
            InterstellarPlugin.Logger.LogError("[VC:Test] Microphone test failed: " + ex.Message);
        }
    }

    public static void StopMicTest()
    {
        var mic = _mic;
        if (mic == null) return;
        _mic = null;
        try { ((IMicrophone)mic).Close(); }
        catch (Exception ex) { InterstellarPlugin.Logger.LogWarning("[VC:Test] Mic close: " + ex.Message); }
    }

    /// <summary>Play a short two-note chime on the selected output. Pressing
    /// again while it plays restarts it.</summary>
    public static void PlaySpeakerTone()
    {
        StopSpeakerTest();
        try
        {
            var tone = new ToneProvider(CreateChime());
            var speaker = new WindowsSpeaker(VoiceConfig.SpeakerDevice);
            ((ISpeaker)speaker).Initialize(new ToneContext(tone));
            _speaker = speaker;
            _speakerStopTick = Environment.TickCount + SpeakerTestMs;
            InterstellarPlugin.Logger.LogInfo("[VC:Test] Speaker test started.");
        }
        catch (Exception ex)
        {
            _speaker = null;
            InterstellarPlugin.Logger.LogError("[VC:Test] Speaker test failed: " + ex.Message);
        }
    }

    public static void StopSpeakerTest()
    {
        var speaker = _speaker;
        if (speaker == null) return;
        _speaker = null;
        try { ((ISpeaker)speaker).Close(); }
        catch (Exception ex) { InterstellarPlugin.Logger.LogWarning("[VC:Test] Speaker close: " + ex.Message); }
    }

    /// <summary>Called every frame (independent of the panel being open) so a
    /// playing chime always stops, even if the panel was closed mid-test.</summary>
    public static void Tick()
    {
        if (_speaker != null && unchecked(Environment.TickCount - _speakerStopTick) >= 0)
            StopSpeakerTest();
    }

    public static void StopAll()
    {
        StopMicTest();
        StopSpeakerTest();
    }

    // ── pieces ──────────────────────────────────────────────────────────────

    /// <summary>No-op sink: WindowsMicrophone tracks the input peak itself and
    /// hands us the level through <see cref="IMicrophone.Level"/>, so the test
    /// only needs to keep the capture running.</summary>
    private sealed class MicContext : IMicrophoneContext
    {
        public void SendAudio(float[] samples, int samplesLength, double samplesMilliseconds, float coeff) { }
    }

    private sealed class ToneContext : ISpeakerContext
    {
        private readonly ISampleProvider _provider;
        public ToneContext(ISampleProvider provider) => _provider = provider;
        public ISampleProvider? GetEndpoint() => _provider;
    }

    /// <summary>Feeds a pre-generated buffer, then silence, until the test
    /// closes the speaker — NAudio pulls from this on its playback thread.</summary>
    private sealed class ToneProvider : ISampleProvider
    {
        private readonly float[] _data;
        private int _pos;
        private bool _loggedBufferType;

        public WaveFormat WaveFormat { get; }

        public ToneProvider(float[] data)
        {
            _data = data;
            WaveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // NAudio hands its own byte[] playback buffer through to us as float[],
            // so Array.Copy would throw ArrayTypeMismatchException on every pull —
            // which is exactly why this test used to be silent. Copy byte-level.
            if (!_loggedBufferType)
            {
                _loggedBufferType = true;
                InterstellarPlugin.Logger.LogInfo(
                    "[VC:Test] Playback buffer is " + buffer.GetType().FullName + ".");
            }
            int n = Math.Min(count, _data.Length - _pos);
            if (n > 0)
            {
                SampleFill.Copy(_data, _pos, buffer, offset, n);
                _pos += n;
            }
            if (n < count) SampleFill.Clear(buffer, offset + n, count - n);
            return count;
        }
    }

    /// <summary>Two-note chime with a squared-sine envelope — audible on
    /// headphones and speakers alike, short enough not to wear out its welcome.</summary>
    private static float[] CreateChime()
    {
        const double duration = 1.2;
        int n = (int)(SampleRate * duration);
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            double p = i / (double)Math.Max(1, n - 1);
            double env = Math.Sin(Math.PI * p);
            env *= env;
            double s = Math.Sin(2d * Math.PI * 523.25d * t) * 0.72d
                     + Math.Sin(2d * Math.PI * 659.25d * t) * 0.28d;
            data[i] = Math.Clamp((float)(s * env * 0.35d), -0.9f, 0.9f);
        }
        return data;
    }
}
