// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * FeedbackInputLock - while the feedback panel is open, no key reaches anything but the panel.
 *
 * User 2026-10-30: typing a note must never call a meeting, use a vent or fire an ability, whatever
 * key it is. The panel already skips the game's KeyboardJoystick.Update (walking, use, report, kill,
 * map) and pins the crewmate, but TOR, Unknown's Collection, Atlas and this mod read some 80 hotkeys
 * on their own (Input.GetKeyDown in Update loops), and every one of them would need its own patch.
 *
 * So the lock sits one level lower: Unity's native key functions (the icalls behind Input.GetKey,
 * GetKeyDown, GetKeyUp, anyKey, GetButton*, GetAxis* and inputString) are detoured while the panel is
 * open and answer "nothing pressed" to every caller, managed mods and the game's own code alike. The
 * panel itself reads its keys inside Own(), which lets the real answer through.
 *
 * Why not a Harmony patch on Input.GetKeyDown: that is a tiny Il2Cpp method (the dedup pitfall: a
 * detour on it can hit other methods with the same machine code), and a managed hook on the interop
 * wrapper would be lost to tiered compilation on such a hot method. The native detours are applied on
 * open and removed on close, so outside the panel nothing is touched and nothing costs time.
 *
 * Mouse buttons and the mouse position stay live: the panel and the map pick are used with the mouse.
 * If a function cannot be found or detoured, it is logged once and the softer locks still hold
 * (KeyboardJoystick prefix, TOR CustomButton prefix, crewmate pinned).
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;

namespace UsefulTORStuff;

internal static class FeedbackInputLock
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool KeyFn(int key);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool FlagFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool NameFn(IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float AxisFn(IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr StringFn();

    /// <summary>True while the native detours are in place.</summary>
    internal static bool Engaged { get; private set; }

    private static int own;   // > 0 while the panel reads its own keys
    private static readonly List<INativeDetour> detours = new();
    private static readonly HashSet<string> warned = new();
    private static IntPtr emptyString;

    // originals (trampolines) and the hooks; kept in fields so the delegates are never collected
    private static KeyFn keyOrig, keyDownOrig, keyUpOrig;
    private static FlagFn anyOrig, anyDownOrig;
    private static NameFn buttonOrig, buttonDownOrig, buttonUpOrig;
    private static AxisFn axisOrig, axisRawOrig;
    private static StringFn inputStringOrig;
    private static readonly KeyFn keyHook = k => own > 0 && keyOrig(k);
    private static readonly KeyFn keyDownHook = k => own > 0 && keyDownOrig(k);
    private static readonly KeyFn keyUpHook = k => own > 0 && keyUpOrig(k);
    private static readonly FlagFn anyHook = () => own > 0 && anyOrig();
    private static readonly FlagFn anyDownHook = () => own > 0 && anyDownOrig();
    private static readonly NameFn buttonHook = n => own > 0 && buttonOrig(n);
    private static readonly NameFn buttonDownHook = n => own > 0 && buttonDownOrig(n);
    private static readonly NameFn buttonUpHook = n => own > 0 && buttonUpOrig(n);
    private static readonly AxisFn axisHook = n => own > 0 ? axisOrig(n) : 0f;
    private static readonly AxisFn axisRawHook = n => own > 0 ? axisRawOrig(n) : 0f;
    private static readonly StringFn inputStringHook = () => own > 0 ? inputStringOrig() : EmptyString();

    /// <summary>Lets the real keys through for the panel's own reads: <c>using (FeedbackInputLock.Own()) { ... }</c>.</summary>
    internal static OwnScope Own() { own++; return default; }

    internal readonly struct OwnScope : IDisposable
    {
        public void Dispose() { if (own > 0) own--; }
    }

    internal static void Engage()
    {
        if (Engaged || FeedbackModule.HardInputLock?.Value == false) return;
        Engaged = true;
        Hook("UnityEngine.Input::GetKeyInt(UnityEngine.KeyCode)", keyHook, out keyOrig);
        Hook("UnityEngine.Input::GetKeyDownInt(UnityEngine.KeyCode)", keyDownHook, out keyDownOrig);
        Hook("UnityEngine.Input::GetKeyUpInt(UnityEngine.KeyCode)", keyUpHook, out keyUpOrig);
        Hook("UnityEngine.Input::get_anyKey()", anyHook, out anyOrig);
        Hook("UnityEngine.Input::get_anyKeyDown()", anyDownHook, out anyDownOrig);
        Hook("UnityEngine.Input::GetButton(System.String)", buttonHook, out buttonOrig);
        Hook("UnityEngine.Input::GetButtonDown(System.String)", buttonDownHook, out buttonDownOrig);
        Hook("UnityEngine.Input::GetButtonUp(System.String)", buttonUpHook, out buttonUpOrig);
        Hook("UnityEngine.Input::GetAxis(System.String)", axisHook, out axisOrig);
        Hook("UnityEngine.Input::GetAxisRaw(System.String)", axisRawHook, out axisRawOrig);
        Hook("UnityEngine.Input::get_inputString()", inputStringHook, out inputStringOrig);
    }

    internal static void Release()
    {
        if (!Engaged) return;
        Engaged = false;
        foreach (var d in detours) {
            try { d.Dispose(); }
            catch (Exception e) { UsefulTORStuffPlugin.Logger.LogError($"[Feedback] input lock release failed: {e.Message}"); }
        }
        detours.Clear();
    }

    private static void Hook<T>(string icall, T hook, out T original) where T : Delegate
    {
        original = null;
        try {
            IntPtr fn = IL2CPP.il2cpp_resolve_icall(icall);
            if (fn == IntPtr.Zero) { WarnOnce(icall, "not found"); return; }
            detours.Add(INativeDetour.CreateAndApply(fn, hook, out original));
        } catch (Exception e) {
            WarnOnce(icall, e.Message);
        }
    }

    private static void WarnOnce(string icall, string why)
    {
        if (warned.Add(icall)) UsefulTORStuffPlugin.Logger.LogWarning($"[Feedback] input lock: {icall} {why} - that input stays live");
    }

    private static IntPtr EmptyString()
    {
        if (emptyString == IntPtr.Zero) {
            emptyString = IL2CPP.ManagedStringToIl2Cpp("");
            IL2CPP.il2cpp_gchandle_new(emptyString, false);   // pinned for the process lifetime, never collected
        }
        return emptyString;
    }
}
