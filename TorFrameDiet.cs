// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * TorFrameDiet - TOR's per-frame code without the il2cpp heap garbage (User 2026-11-13: "fix the memory issue,
 * don't modify TOR's code").
 *
 * THE PROBLEM
 * Two TOR postfixes run all the time: HudManager.Update (Patches/UpdatePatch.cs: name tags and colours, the
 * kill/report/vent buttons, CustomButton.HudUpdate -> CustomButton.Update for every one of the ~110 TOR and UC
 * buttons) and PlayerControl.FixedUpdate (Patches/PlayerControlPatch.cs: targets, outlines, role ticks, for
 * every player at the physics rate). Through Il2CppInterop every call that returns a value type from the game
 * boxes it on the il2cpp heap: Unity's == / != / implicit bool, Time.deltaTime, isActiveAndEnabled,
 * localPosition, and every string handed to the game becomes a new il2cpp string even when it did not change.
 * Measured in the multi-client test: ~65 MB of garbage per lobby-to-lobby cycle from these two alone, which the
 * Boehm GC answers by growing the heap (memory project-il2cpp-gc-pitfalls).
 *
 * WHY NOT A TRANSPILER
 * A Harmony patch on a hot managed TOR method is lost when .NET's tiered compilation recompiles that method
 * from its original IL (memory project-tiering-detour-loss): fine in the test harness (tiering off), gone
 * within seconds on a normal player start. TOR's own file is not touched either.
 *
 * WHAT THIS DOES
 *  1. At the first lobby join it copies, from TOR's loaded assembly, the IL of both postfixes with the helpers
 *     of their classes, CustomButton.HudUpdate and CustomButton.Update into DynamicMethods (MonoMod's
 *     DynamicMethodDefinition). DynamicMethods are never re-tiered, so what is in them stays in them.
 *  2. In the copies, calls are rewritten:
 *       - calls between the copied methods go to the copies (through a dispatcher, see 3);
 *       - Unity's ==, != and implicit bool become a managed reference check plus the native pointer, the same
 *         test Unity makes, without the boxed result;
 *       - TMP_Text.text = s, ActionButton.OverrideText(s) and ActionButton.SetCoolDown(t, max) are skipped when
 *         nothing changed (TextMeshPro itself ignores an identical text, but the il2cpp string was already made);
 *       - HUD copies only (they run once per frame): Time.deltaTime, Behaviour.isActiveAndEnabled and
 *         Transform.localPosition are read once per frame per object, and HudUpdate's
 *         RemoveAll(item => item.actionButton == null) uses the cheap test. The FixedUpdate copies keep those
 *         calls: there deltaTime is the physics step and several physics steps can fall into one frame.
 *  3. Patches other mods put on the copied TOR methods (Forgotten Fixes' own audit guards, Unknown's Collection's
 *     ButtonLabelGuard, the Medic charges, MultiModifiers' mini age...) are run around the copy in Harmony's
 *     order: prefixes (a false one skips the body), body, postfixes, finalizers. Only patches whose parameters
 *     are __instance and/or __exception can be run this way; a method with any other patch (setTarget's
 *     __result patches, playerSizeUpdate's) keeps calling TOR's original, so no patch is ever skipped. Changed
 *     patch lists are picked up at the next lobby join.
 *  4. TOR's two postfixes are swapped for ones of this file that call the copies, one priority step above
 *     TOR's so they keep their place in front of every postfix that used to run after them. If a copy throws
 *     repeatedly, TOR's own postfix is put back and that part of the diet stays off for the session.
 * Switch: [Performance] TOR Frame Diet (default on). Behaviour stays TOR's: the copies are TOR's IL.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using BepInEx.Configuration;
using HarmonyLib;
using MonoMod.Utils;
using TheOtherRoles.Objects;
using UnityEngine;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace UsefulTORStuff {

    public static class TorFrameDiet {
        internal static ConfigEntry<bool> Enabled;

        private static bool installed;
        private static Harmony harmony;
        private static string patchSignature = "";

        internal static void Bind(ConfigFile config) {
            Enabled = config.Bind("Performance", "TOR Frame Diet", true,
                "Run TOR's per-frame code (HUD, buttons, player FixedUpdate) from copies that do not litter the il2cpp heap. Off = TOR's original.");
        }

        // ================================================================================================
        // Cheap replacements (only the copies call these)
        // ================================================================================================
        private static int frame;
        private static float deltaTime;
        private static readonly Dictionary<IntPtr, (int f, bool v)> activeCache = new();
        private static readonly Dictionary<IntPtr, (int f, Vector3 v)> posCache = new();
        private static readonly Dictionary<IntPtr, (float t, float max)> coolCache = new();

        private static bool Gone(UnityEngine.Object o) => ReferenceEquals(o, null) || o.m_CachedPtr == IntPtr.Zero;

        // Unity: two missing objects are equal, a missing one equals null, else identity (the native object).
        public static bool Exists(UnityEngine.Object o) => !Gone(o);
        public static bool Eq(UnityEngine.Object a, UnityEngine.Object b) {
            bool an = Gone(a), bn = Gone(b);
            if (an || bn) return an && bn;
            return a.Pointer == b.Pointer;
        }
        public static bool Neq(UnityEngine.Object a, UnityEngine.Object b) => !Eq(a, b);
        public static float DeltaTime() => deltaTime;

        public static bool IsActiveAndEnabled(Behaviour b) {
            var p = b.Pointer;
            if (activeCache.TryGetValue(p, out var c) && c.f == frame) return c.v;
            bool v = b.isActiveAndEnabled;
            activeCache[p] = (frame, v);
            return v;
        }

        public static Vector3 GetLocalPosition(Transform t) {
            var p = t.Pointer;
            if (posCache.TryGetValue(p, out var c) && c.f == frame) return c.v;
            var v = t.localPosition;
            posCache[p] = (frame, v);
            return v;
        }

        public static void SetLocalPosition(Transform t, Vector3 v) {
            var p = t.Pointer;
            if (posCache.TryGetValue(p, out var c) && c.f == frame && c.v == v) return;
            t.localPosition = v;
            posCache[p] = (frame, v);
        }

        public static void SetText(TMPro.TMP_Text t, string s) {
            if (s != null && string.Equals(t.text, s, StringComparison.Ordinal)) return;
            t.text = s;
        }

        public static void OverrideText(ActionButton b, string s) {
            var l = b.buttonLabelText;
            if (!Gone(l) && s != null && string.Equals(l.text, s, StringComparison.Ordinal)) return;
            b.OverrideText(s);
        }

        public static void SetCoolDown(ActionButton b, float timer, float max) {
            var p = b.Pointer;
            if (coolCache.TryGetValue(p, out var c) && c.t == timer && c.max == max) return;
            b.SetCoolDown(timer, max);
            coolCache[p] = (timer, max);
        }

        // HudUpdate's buttons.RemoveAll(item => item.actionButton == null), with the cheap test
        private static readonly Predicate<CustomButton> deadButton = item => item == null || Gone(item.actionButton);
        public static int RemoveDeadButtons(List<CustomButton> list, Predicate<CustomButton> ignored) => list.RemoveAll(deadButton);

        private static void ClearCaches() { activeCache.Clear(); posCache.Clear(); coolCache.Clear(); }

        // ================================================================================================
        // Copies and their dispatchers
        // ================================================================================================
        private sealed class Copy {
            public MethodBase Orig;
            public bool Hud;                       // runs in the HUD frame: the per-frame caches apply
            public Delegate Body;                  // the rewritten copy; null = call the original
            // every patch gets (instance, its patch class' __state slots, exception)
            public Func<object, object[], bool>[] Pre = Array.Empty<Func<object, object[], bool>>();
            public Action<object, object[]>[] Post = Array.Empty<Action<object, object[]>>();
            public Func<object, Exception, object[], Exception>[] Fin = Array.Empty<Func<object, Exception, object[], Exception>>();
            public int StateSlots;                 // one per patch class that uses __state (Harmony shares it per class)
            public bool Plain => Pre.Length == 0 && Post.Length == 0 && Fin.Length == 0;
        }

        private static readonly List<Copy> copies = new();
        private static readonly Dictionary<(Module, int), int> idOf = new();

        // Harmony's order around the body; `run` calls the copy (or nothing when a prefix said no).
        private static void Around(Copy c, object inst, Action<object> run) {
            Exception ex = null;
            var st = c.StateSlots > 0 ? new object[c.StateSlots] : null;
            try {
                bool go = true;
                for (int i = 0; i < c.Pre.Length; i++) if (!c.Pre[i](inst, st)) go = false;
                if (go) run(inst);
                for (int i = 0; i < c.Post.Length; i++) c.Post[i](inst, st);
            } catch (Exception e) { ex = e; }
            for (int i = 0; i < c.Fin.Length; i++) ex = c.Fin[i](inst, ex, st);
            if (ex != null) ExceptionDispatchInfo.Capture(ex).Throw();
        }

        // static void M()
        public static void D0(int id) {
            var c = copies[id];
            if (c.Body == null) { Invoke(c, null, null); return; }
            var body = (Action)c.Body;
            if (c.Plain) body(); else Around(c, null, _ => body());
        }

        // static void M(HudManager)
        public static void DHud(HudManager h, int id) {
            var c = copies[id];
            if (c.Body == null) { Invoke(c, null, new object[] { h }); return; }
            var body = (Action<HudManager>)c.Body;
            if (c.Plain) body(h); else Around(c, null, _ => body(h));
        }

        // static void M(PlayerControl)
        public static void DPlayer(PlayerControl p, int id) {
            var c = copies[id];
            if (c.Body == null) { Invoke(c, null, new object[] { p }); return; }
            var body = (Action<PlayerControl>)c.Body;
            if (c.Plain) body(p); else Around(c, null, _ => body(p));
        }

        // static void M(PlayerControl, Color)
        public static void DPlayerColor(PlayerControl p, Color col, int id) {
            var c = copies[id];
            if (c.Body == null) { Invoke(c, null, new object[] { p, col }); return; }
            var body = (Action<PlayerControl, Color>)c.Body;
            if (c.Plain) body(p, col); else Around(c, null, _ => body(p, col));
        }

        // CustomButton.Update()
        public static void DButton(CustomButton b, int id) {
            var c = copies[id];
            if (c.Body == null) { b.Update(); return; }
            var body = (Action<CustomButton>)c.Body;
            if (c.Plain) body(b); else Around(c, b, x => body((CustomButton)x));
        }

        // A method whose patches cannot be run around a copy: TOR's original through reflection, so Harmony's
        // detour (and every patch on it) applies. Exceptions come out unwrapped, like a direct call.
        private static void Invoke(Copy c, object inst, object[] args) {
            try { c.Orig.Invoke(inst, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
        }

        private static MethodInfo DispatcherFor(MethodBase m) {
            var ps = m.GetParameters();
            if (!m.IsStatic) return m.DeclaringType == typeof(CustomButton) && ps.Length == 0 ? AccessTools.Method(typeof(TorFrameDiet), nameof(DButton)) : null;
            if (((MethodInfo)m).ReturnType != typeof(void)) return null;
            if (ps.Length == 0) return AccessTools.Method(typeof(TorFrameDiet), nameof(D0));
            if (ps.Length == 1 && ps[0].ParameterType == typeof(HudManager)) return AccessTools.Method(typeof(TorFrameDiet), nameof(DHud));
            if (ps.Length == 1 && ps[0].ParameterType == typeof(PlayerControl)) return AccessTools.Method(typeof(TorFrameDiet), nameof(DPlayer));
            if (ps.Length == 2 && ps[0].ParameterType == typeof(PlayerControl) && ps[1].ParameterType == typeof(Color))
                return AccessTools.Method(typeof(TorFrameDiet), nameof(DPlayerColor));
            return null;
        }

        private static Type DelegateFor(MethodBase m) {
            var ps = m.GetParameters();
            if (!m.IsStatic) return typeof(Action<CustomButton>);
            if (ps.Length == 0) return typeof(Action);
            if (ps.Length == 1) return ps[0].ParameterType == typeof(HudManager) ? typeof(Action<HudManager>) : typeof(Action<PlayerControl>);
            return typeof(Action<PlayerControl, Color>);
        }

        // ---- the calls replaced inside the copies --------------------------------------------------------
        private static Dictionary<(Module, int), MethodInfo> fastAll, fastHud;

        private static void AddFast(Dictionary<(Module, int), MethodInfo> map, MethodBase target, string ours) {
            if (target == null) { UsefulTORStuffPlugin.Logger?.LogWarning($"[TorFrameDiet] no target for {ours}"); return; }
            map[(target.Module, target.MetadataToken)] = AccessTools.Method(typeof(TorFrameDiet), ours);
        }

        private static void BuildFast() {
            fastAll = new Dictionary<(Module, int), MethodInfo>();
            var uo = typeof(UnityEngine.Object);
            AddFast(fastAll, uo.GetMethod("op_Implicit", new[] { uo }), nameof(Exists));
            AddFast(fastAll, uo.GetMethod("op_Equality", new[] { uo, uo }), nameof(Eq));
            AddFast(fastAll, uo.GetMethod("op_Inequality", new[] { uo, uo }), nameof(Neq));
            AddFast(fastAll, AccessTools.PropertySetter(typeof(TMPro.TMP_Text), nameof(TMPro.TMP_Text.text)), nameof(SetText));
            AddFast(fastAll, AccessTools.Method(typeof(ActionButton), nameof(ActionButton.OverrideText), new[] { typeof(string) }), nameof(OverrideText));
            AddFast(fastAll, AccessTools.Method(typeof(ActionButton), nameof(ActionButton.SetCoolDown), new[] { typeof(float), typeof(float) }), nameof(SetCoolDown));
            fastHud = new Dictionary<(Module, int), MethodInfo>(fastAll);
            AddFast(fastHud, AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime)), nameof(DeltaTime));
            AddFast(fastHud, AccessTools.PropertyGetter(typeof(Behaviour), nameof(Behaviour.isActiveAndEnabled)), nameof(IsActiveAndEnabled));
            AddFast(fastHud, AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.localPosition)), nameof(GetLocalPosition));
            AddFast(fastHud, AccessTools.PropertySetter(typeof(Transform), nameof(Transform.localPosition)), nameof(SetLocalPosition));
        }

        private static Delegate BuildCopy(Copy c, List<string> notes) {
            var m = c.Orig;
            using var dmd = new DynamicMethodDefinition(m);
            var body = dmd.Definition.Body;
            var il = body.GetILProcessor();
            var module = dmd.Definition.Module;
            var fast = c.Hud ? fastHud : fastAll;
            int calls = 0, fasts = 0;
            var removeAll = typeof(List<CustomButton>).GetMethod(nameof(List<CustomButton>.RemoveAll));
            bool isHudUpdate = m.DeclaringType == typeof(CustomButton) && m.Name == nameof(CustomButton.HudUpdate);
            foreach (var ins in body.Instructions.ToList()) {
                if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) continue;
                if (ins.Operand is not Mono.Cecil.MethodReference mr) continue;
                MethodBase target;
                try { target = mr.ResolveReflection(); } catch { continue; }
                if (target == null) continue;
                var key = (target.Module, target.MetadataToken);
                if (idOf.TryGetValue(key, out int id)) {
                    // the call becomes "ldc.i4 id; call dispatcher": the arguments are already on the stack.
                    // Rewriting in place keeps branches that target this instruction pointing at its start.
                    var call = il.Create(OpCodes.Call, module.ImportReference(DispatcherFor(target)));
                    ins.OpCode = OpCodes.Ldc_I4;
                    ins.Operand = id;
                    il.InsertAfter(ins, call);
                    calls++;
                } else if (fast.TryGetValue(key, out var rep)) {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = module.ImportReference(rep);
                    fasts++;
                } else if (isHudUpdate && removeAll != null && target.MetadataToken == removeAll.MetadataToken && target.Module == removeAll.Module) {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = module.ImportReference(AccessTools.Method(typeof(TorFrameDiet), nameof(RemoveDeadButtons)));
                    fasts++;
                }
            }
            var gen = dmd.Generate();
            if (calls + fasts > 0) notes.Add($"{m.DeclaringType?.Name}.{m.Name}: {calls}/{fasts}");
            return gen.CreateDelegate(DelegateFor(m));
        }

        // ---- patches of other mods on the copied methods -------------------------------------------------
        // Parameters a patch may have here: __instance, __state (out/ref in a prefix, by value or ref after it),
        // __exception in a finalizer. Anything else (__result, __args, original arguments) keeps the original.
        private static bool Runnable(MethodInfo patch, bool finalizer) {
            foreach (var p in patch.GetParameters()) {
                if (p.Name == "__instance" && !p.ParameterType.IsByRef) continue;
                if (p.Name == "__state") continue;
                if (finalizer && p.Name == "__exception" && !p.ParameterType.IsByRef) continue;
                return false;
            }
            var r = patch.ReturnType;
            return finalizer ? r == typeof(void) || r == typeof(Exception) : r == typeof(void) || r == typeof(bool);
        }

        // Builds the call of one patch. __state lives in st[slot] (boxed); a by-ref __state works on a local that is
        // read from the slot before (default when empty, like Harmony's fresh local) and written back after the call.
        private static Expression CallPatch(MethodInfo patch, ParameterExpression inst, ParameterExpression ex,
                                            ParameterExpression st, int slot, Func<Expression, Expression> finish) {
            var args = new List<Expression>();
            var vars = new List<ParameterExpression>();
            var before = new List<Expression>();
            var after = new List<Expression>();
            foreach (var p in patch.GetParameters()) {
                if (p.Name == "__instance") { args.Add(Expression.Convert(inst, p.ParameterType)); continue; }
                if (p.Name == "__exception") { args.Add(Expression.Convert(ex, p.ParameterType)); continue; }
                // __state
                var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                var cell = Expression.ArrayAccess(st, Expression.Constant(slot));
                Expression read = Expression.Condition(Expression.Equal(cell, Expression.Constant(null)),
                    Expression.Default(t), Expression.Convert(cell, t));
                if (p.ParameterType.IsByRef) {
                    var v = Expression.Variable(t, "state");
                    vars.Add(v);
                    before.Add(Expression.Assign(v, read));
                    after.Add(Expression.Assign(cell, Expression.Convert(v, typeof(object))));
                    args.Add(v);
                } else {
                    args.Add(read);
                }
            }
            Expression call = Expression.Call(patch, args);
            var tmp = patch.ReturnType == typeof(void) ? null : Expression.Variable(patch.ReturnType, "ret");
            var body = new List<Expression>(before);
            if (tmp != null) { vars.Add(tmp); body.Add(Expression.Assign(tmp, call)); } else body.Add(call);
            body.AddRange(after);
            body.Add(finish(tmp));
            return Expression.Block(vars, body);
        }

        private static bool AttachPatches(Copy c, List<string> notes) {
            var info = Harmony.GetPatchInfo(c.Orig);
            if (info == null) return true;
            string name = $"{c.Orig.DeclaringType?.Name}.{c.Orig.Name}";
            if (info.Transpilers.Count > 0 || info.ILManipulators.Count > 0) { notes.Add($"{name}: transpiler, original kept"); return false; }
            var pre = info.Prefixes.OrderByDescending(p => p.priority).ThenBy(p => p.index).ToList();
            var post = info.Postfixes.OrderByDescending(p => p.priority).ThenBy(p => p.index).ToList();
            var fin = info.Finalizers.OrderByDescending(p => p.priority).ThenBy(p => p.index).ToList();
            foreach (var p in pre.Concat(post))
                if (!Runnable(p.PatchMethod, false)) { notes.Add($"{name}: patch {p.PatchMethod.DeclaringType?.Name}.{p.PatchMethod.Name}, original kept"); return false; }
            foreach (var p in fin)
                if (!Runnable(p.PatchMethod, true)) { notes.Add($"{name}: finalizer {p.PatchMethod.DeclaringType?.Name}.{p.PatchMethod.Name}, original kept"); return false; }

            // one __state slot per patch class (Harmony shares __state between the patches of one class)
            var slots = new Dictionary<Type, int>();
            int SlotOf(MethodInfo m) {
                if (!m.GetParameters().Any(p => p.Name == "__state")) return -1;
                var t = m.DeclaringType ?? typeof(object);
                if (!slots.TryGetValue(t, out int s)) slots[t] = s = slots.Count;
                return s;
            }
            var inst = Expression.Parameter(typeof(object), "inst");
            var exn = Expression.Parameter(typeof(Exception), "ex");
            var st = Expression.Parameter(typeof(object[]), "st");
            c.Pre = pre.Select(p => Expression.Lambda<Func<object, object[], bool>>(
                CallPatch(p.PatchMethod, inst, exn, st, SlotOf(p.PatchMethod), r => r == null ? Expression.Constant(true) : r), inst, st).Compile()).ToArray();
            c.Post = post.Select(p => Expression.Lambda<Action<object, object[]>>(
                CallPatch(p.PatchMethod, inst, exn, st, SlotOf(p.PatchMethod), _ => Expression.Empty()), inst, st).Compile()).ToArray();
            c.Fin = fin.Select(p => Expression.Lambda<Func<object, Exception, object[], Exception>>(
                CallPatch(p.PatchMethod, inst, exn, st, SlotOf(p.PatchMethod), r => r == null ? exn : r), inst, exn, st).Compile()).ToArray();
            c.StateSlots = slots.Count;
            if (!c.Plain) notes.Add($"{name}: {c.Pre.Length}+{c.Post.Length}+{c.Fin.Length} patches run around the copy");
            return true;
        }

        // ================================================================================================
        // The two swaps
        // ================================================================================================
        private sealed class Swap {
            public string Label, TorClass;
            public MethodInfo Target, TorPatch, Ours;
            public int Priority;
            public bool Active, Off;
            public int RootId = -1, Failures;
        }

        private static readonly List<Swap> swaps = new();

        private static IEnumerable<MethodBase> StaticHelpers(Type t) =>
            t == null ? Enumerable.Empty<MethodBase>()
                : t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsGenericMethod && m.GetMethodBody() != null && DispatcherFor(m) != null);

        private static List<(MethodBase m, bool hud)> Targets() {
            var list = new List<(MethodBase, bool)>();
            foreach (var m in StaticHelpers(TorType.Get("TheOtherRoles.Patches.HudManagerUpdatePatch"))) list.Add((m, true));
            list.Add((AccessTools.Method(typeof(CustomButton), nameof(CustomButton.HudUpdate)), true));
            list.Add((AccessTools.Method(typeof(CustomButton), nameof(CustomButton.Update)), true));
            foreach (var m in StaticHelpers(TorType.Get("TheOtherRoles.Patches.PlayerControlFixedUpdatePatch"))) list.Add((m, false));
            return list.Where(x => x.Item1 != null).ToList();
        }

        private static string Signature(IEnumerable<MethodBase> methods) =>
            string.Join("|", methods.Select(m => {
                var i = Harmony.GetPatchInfo(m);
                return i == null ? m.Name + ":-" : $"{m.Name}:{string.Join(",", i.Prefixes.Concat(i.Postfixes).Concat(i.Finalizers).Concat(i.Transpilers).Select(p => p.owner + "." + p.PatchMethod.Name))}";
            }));

        private static void BuildAll() {
            if (fastAll == null) BuildFast();
            var targets = Targets();
            copies.Clear();
            idOf.Clear();
            foreach (var (m, hud) in targets) {
                idOf[(m.Module, m.MetadataToken)] = copies.Count;
                copies.Add(new Copy { Orig = m, Hud = hud });
            }
            var notes = new List<string>();
            foreach (var c in copies) {
                if (!AttachPatches(c, notes)) continue;   // Body stays null: the dispatcher calls TOR's original
                try { c.Body = BuildCopy(c, notes); }
                catch (Exception e) { notes.Add($"{c.Orig.Name}: copy failed ({e.GetType().Name}: {e.Message}), original kept"); c.Body = null; }
            }
            foreach (var s in swaps) {
                s.RootId = copies.FindIndex(c => c.Orig.DeclaringType?.FullName == s.TorClass && c.Orig.Name == "Postfix");
                if (s.RootId < 0 || copies[s.RootId].Body == null) s.RootId = -1;
            }
            patchSignature = Signature(targets.Select(t => t.m));
            UsefulTORStuffPlugin.Logger?.LogInfo($"[TorFrameDiet] {copies.Count(c => c.Body != null)}/{copies.Count} TOR methods run from copies (calls to copies/cheap calls): "
                                                 + string.Join("; ", notes));
        }

        private static void Install() {
            installed = true;
            if (Enabled != null && !Enabled.Value) return;
            harmony ??= new Harmony("com.usefultorstuff.torframediet");
            swaps.Clear();
            swaps.Add(new Swap {
                Label = "HUD", TorClass = "TheOtherRoles.Patches.HudManagerUpdatePatch",
                Target = AccessTools.Method(typeof(HudManager), nameof(HudManager.Update)),
                Ours = AccessTools.Method(typeof(TorFrameDiet), nameof(HudPostfix)),
            });
            swaps.Add(new Swap {
                Label = "FixedUpdate", TorClass = "TheOtherRoles.Patches.PlayerControlFixedUpdatePatch",
                Target = AccessTools.Method(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate)),
                Ours = AccessTools.Method(typeof(TorFrameDiet), nameof(FixedPostfix)),
            });
            try {
                BuildAll();
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TorFrameDiet] building the copies failed, TOR's originals stay: {e}");
                return;
            }
            foreach (var s in swaps) {
                try {
                    var info = Harmony.GetPatchInfo(s.Target);
                    var tor = info?.Postfixes.FirstOrDefault(p => p.PatchMethod.DeclaringType?.FullName == s.TorClass && p.PatchMethod.Name == "Postfix");
                    if (tor == null) { UsefulTORStuffPlugin.Logger?.LogWarning($"[TorFrameDiet] {s.Label}: TOR's postfix not found, left alone."); continue; }
                    if (s.RootId < 0) { UsefulTORStuffPlugin.Logger?.LogWarning($"[TorFrameDiet] {s.Label}: TOR's postfix could not be copied, left alone."); continue; }
                    s.TorPatch = tor.PatchMethod;
                    s.Priority = tor.priority;
                    int earlierSame = info.Postfixes.Count(p => p.priority == tor.priority && p.index < tor.index && p.PatchMethod != tor.PatchMethod);
                    new Harmony(tor.owner).Unpatch(s.Target, s.TorPatch);
                    harmony.Patch(s.Target, postfix: new HarmonyMethod(s.Ours) { priority = s.Priority + 1 });
                    s.Active = true;
                    UsefulTORStuffPlugin.Logger?.LogInfo($"[TorFrameDiet] {s.Label}: TOR's postfix runs from the copy now (priority {s.Priority + 1}"
                                                         + (earlierSame > 0 ? $"; {earlierSame} postfix(es) of TOR's priority registered before it now run after it" : "") + ").");
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorFrameDiet] {s.Label}: swap failed: {e.Message}");
                    Restore(s);
                }
            }
        }

        private static void Restore(Swap s) {
            s.Off = true;
            try {
                if (s.Target == null || s.TorPatch == null) return;
                harmony?.Unpatch(s.Target, s.Ours);
                var info = Harmony.GetPatchInfo(s.Target);
                if (info == null || !info.Postfixes.Any(p => p.PatchMethod == s.TorPatch))
                    new Harmony("com.usefultorstuff.torframediet.restore").Patch(s.Target, postfix: new HarmonyMethod(s.TorPatch) { priority = s.Priority });
                UsefulTORStuffPlugin.Logger?.LogWarning($"[TorFrameDiet] {s.Label}: TOR's own postfix is back.");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[TorFrameDiet] {s.Label}: restore failed: {e}");
            }
        }

        // TOR's original would have thrown the same way into Harmony; after a run of failures in a row the
        // original comes back, so a copy problem never costs more than a moment.
        private static void Failed(Swap s, Exception e) {
            if (++s.Failures <= 3) UsefulTORStuffPlugin.Logger?.LogWarning($"[TorFrameDiet] {s.Label} copy threw: {e.GetType().Name}: {e.Message}");
            if (s.Failures >= 30) Restore(s);
        }

        public static void HudPostfix(HudManager __instance) {
            if (swaps.Count < 1) return;
            var s = swaps[0];
            if (s.Off || s.RootId < 0) return;
            frame++;
            deltaTime = Time.deltaTime;     // one read per frame for every button
            try { DHud(__instance, s.RootId); s.Failures = 0; }
            catch (Exception e) { Failed(s, e); }
        }

        public static void FixedPostfix(PlayerControl __instance) {
            if (swaps.Count < 2) return;
            var s = swaps[1];
            if (s.Off || s.RootId < 0) return;
            try { DPlayer(__instance, s.RootId); s.Failures = 0; }
            catch (Exception e) { Failed(s, e); }
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class JoinPatch {
            public static void Postfix() {
                try {
                    ClearCaches();
                    if (!installed) { Install(); return; }
                    if (!swaps.Any(s => s.Active && !s.Off)) return;
                    // another mod patched one of the copied methods since: build the copies again
                    var sig = Signature(copies.Select(c => c.Orig));
                    if (sig == patchSignature) return;
                    BuildAll();
                    foreach (var s in swaps.Where(s => s.Active && !s.Off && s.RootId < 0)) Restore(s);
                } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[TorFrameDiet] lobby check failed: {e.Message}");
                }
            }
        }
    }
}
