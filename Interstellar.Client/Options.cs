using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin
{
    [HarmonyPatch]
    internal class Options
    {
        // TODO: Game options settings code is temporarily disabled.
        // Will be re-implemented without the Reactor localization dependency.
    }

    /*
    // ── Original options code (commented out) ─────────────────────
    // Needs rework: StringNames injection without Reactor dependency.
    ...
    */
}
