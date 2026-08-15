// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.
// Based on The Other Roles (https://github.com/TheOtherRolesAU/TheOtherRoles), GPL-3.0.

/*
 * WebConfig - a local, host-only web page for editing EVERY lobby setting from a browser:
 * all mod CustomOptions (TOR's + every loaded mod's, they all live in CustomOption.options)
 * plus the standard Among Us "Vanilla" options.
 *
 * Architecture (the important part):
 *   - An HttpListener bound to 127.0.0.1 ONLY (never the LAN) serves a single self-contained
 *     page (Resources/webconfig.html) + a tiny JSON API. It runs on a background thread.
 *   - Il2Cpp / Unity objects may only be touched on the main thread, so the HTTP thread never
 *     calls into the game directly. Instead every request that needs game state enqueues a Job
 *     and blocks (with a timeout) until the MAIN thread runs it. The pump is a HudManager.Update
 *     postfix (picked up by PatchAll) - i.e. requests only resolve while a lobby/game is live;
 *     with no HudManager the request times out and the page shows "waiting for a lobby".
 *   - HOST GATE: reads are always served (so a non-host sees a read-only view); writes are
 *     refused with 403 unless AmongUsClient.Instance.AmHost. Binding to loopback means the page
 *     is only reachable from the host's own machine.
 *
 * Applying changes:
 *   - Mod options: option.updateSelection(sel) is TOR's canonical setter (clamps, persists to the
 *     preset config, fires onChange, syncs to clients). Its tail unconditionally refreshes the
 *     in-game options menu and can NRE when that menu was never opened this lobby, so we wrap it
 *     and always follow with ShareOptionSelections() (idempotent - the same broadcast TOR's own
 *     paste flow uses) to guarantee clients receive the change even on the menu-closed path.
 *   - Vanilla options: a curated table mirroring the Among Us Normal settings, applied through the
 *     IGameOptions typed setters + LogicOptions.SyncOptions() (the same tail loadVanillaOptions uses).
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AmongUs.GameOptions;
using HarmonyLib;
using TheOtherRoles;
using UnityEngine;

namespace UsefulTORStuff {
    public static class WebConfig {
        private static HttpListener listener;
        private static Thread thread;
        private static volatile bool running;
        private static int activePort;

        // Jobs marshalled from the HTTP thread onto the Unity main thread.
        private sealed class Job {
            public Func<(int status, string ctype, string body)> Work;
            public (int status, string ctype, string body) Result;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            // AUDIT-2026-08-15: set by Marshal() on the HTTP thread once it gives up waiting;
            // read by Pump() on the main thread before running Work(). A plain write-once/read-once
            // flag, so volatile (not Interlocked) is enough to make the write visible across threads.
            public volatile bool Cancelled;
        }
        private static readonly ConcurrentQueue<Job> queue = new ConcurrentQueue<Job>();

        // ====================================================================
        // Lifecycle
        // ====================================================================
        public static void Start(int preferredPort) {
            try {
                if (!HttpListener.IsSupported) {
                    UsefulTORStuffPlugin.Logger?.LogWarning("[WebConfig] HttpListener not supported on this platform - web config disabled.");
                    return;
                }
                for (int p = preferredPort; p < preferredPort + 5; p++) {
                    try {
                        var l = new HttpListener();
                        l.Prefixes.Add($"http://127.0.0.1:{p}/");
                        l.Prefixes.Add($"http://localhost:{p}/");
                        l.Start();
                        listener = l;
                        activePort = p;
                        break;
                    } catch (HttpListenerException) {
                        // Port busy (another instance / another app) - try the next one.
                    }
                }
                if (listener == null) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[WebConfig] no free port in {preferredPort}..{preferredPort + 4} - web config disabled.");
                    return;
                }

                running = true;
                thread = new Thread(Listen) { IsBackground = true, Name = "UTS-WebConfig" };
                thread.Start();
                AppDomain.CurrentDomain.ProcessExit += (_, __) => Stop();
                UsefulTORStuffPlugin.Logger?.LogInfo($"[WebConfig] settings editor live at http://127.0.0.1:{activePort}/ (host-only, loopback).");
            } catch (Exception e) {
                UsefulTORStuffPlugin.Logger?.LogError($"[WebConfig] start failed: {e}");
            }
        }

        public static void Stop() {
            running = false;
            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }
            listener = null;
        }

        public static int Port => activePort;

        private static void Listen() {
            while (running) {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; } // Stop() disposes the listener -> GetContext throws -> exit loop
                try { Handle(ctx); }
                catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogError($"[WebConfig] request failed: {e}");
                    try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                }
            }
        }

        // Drained by the HudManager.Update postfix below (main thread).
        private static void Pump() {
            int n = 0;
            while (n++ < 64 && queue.TryDequeue(out var job)) {
                // AUDIT-2026-08-15: Marshal() may have already given up on this job (timeout) and
                // answered the browser itself - discard it here instead of applying a stale write
                // to whatever lobby happens to be ticking now.
                if (job.Cancelled) {
                    UsefulTORStuffPlugin.Logger?.LogInfo("[WebConfig] dropping cancelled/timed-out job.");
                    continue;
                }
                try { job.Result = job.Work(); }
                catch (Exception e) {
                    job.Result = (500, "text/plain", "error: " + e.Message);
                    UsefulTORStuffPlugin.Logger?.LogError($"[WebConfig] job failed: {e}");
                }
                finally { job.Done.Set(); }
            }
        }

        [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
        private static class PumpPatch {
            public static void Postfix() {
                if (running && !queue.IsEmpty) Pump();
            }
        }

        // ====================================================================
        // HTTP routing
        // ====================================================================
        private static void Handle(HttpListenerContext ctx) {
            string path = ctx.Request.Url.AbsolutePath;
            string method = ctx.Request.HttpMethod;

            if (path == "/" || path == "/index.html") {
                Write(ctx, 200, "text/html; charset=utf-8", PageHtml());
                return;
            }
            if (path == "/api/state" && method == "GET") {
                var r = Marshal(BuildState);
                Write(ctx, r.status, r.ctype, r.body);
                return;
            }
            if (path == "/api/set" && method == "POST") {
                var form = ReadForm(ctx);
                var r = Marshal(() => ApplySet(form));
                Write(ctx, r.status, r.ctype, r.body);
                return;
            }
            Write(ctx, 404, "text/plain", "not found");
        }

        private static (int status, string ctype, string body) Marshal(Func<(int, string, string)> work) {
            var job = new Job { Work = work };
            queue.Enqueue(job);
            // Only resolves while HudManager.Update is ticking (a live lobby/game).
            if (!job.Done.Wait(4000)) {
                // AUDIT-2026-08-15: we're answering the browser now, but the job is still sitting in
                // the queue - a later Pump() (possibly in a different lobby) would otherwise run it
                // unconditionally. Mark it so Pump() drops it instead of applying a stale write.
                job.Cancelled = true;
                return (503, "text/plain", "Game not ready - open a lobby first.");
            }
            return job.Result;
        }

        private static void Write(HttpListenerContext ctx, int status, string ctype, string body) {
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = ctype;
            try { ctx.Response.Headers["Cache-Control"] = "no-store"; } catch { }
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static Dictionary<string, string> ReadForm(HttpListenerContext ctx) {
            var dict = new Dictionary<string, string>();
            try {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                    body = reader.ReadToEnd();
                foreach (var pair in body.Split('&')) {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    string k = eq < 0 ? pair : pair.Substring(0, eq);
                    string v = eq < 0 ? "" : pair.Substring(eq + 1);
                    dict[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
                }
            } catch { }
            return dict;
        }

        // ====================================================================
        // State (main thread): all mod CustomOptions grouped by type + curated vanilla options.
        // ====================================================================
        private static (int, string, string) BuildState() {
            bool host = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
            var sb = new StringBuilder(16384);
            sb.Append('{');
            sb.Append("\"host\":").Append(host ? "true" : "false");
            sb.Append(",\"ready\":true");

            // --- Mod options, grouped by CustomOptionType in registration order ---
            sb.Append(",\"tabs\":[");
            var options = CustomOption.options;
            var typesInOrder = new List<CustomOption.CustomOptionType>();
            foreach (var o in options)
                if (!typesInOrder.Contains(o.type)) typesInOrder.Add(o.type);

            bool firstTab = true;
            foreach (var type in typesInOrder) {
                if (!firstTab) sb.Append(','); firstTab = false;
                sb.Append("{\"type\":").Append((int)type);
                sb.Append(",\"name\":\"").Append(Esc(TypeName(type))).Append('"');
                sb.Append(",\"options\":[");
                bool firstOpt = true;
                foreach (var o in options) {
                    if (o.type != type) continue;
                    if (o.selections == null || o.selections.Length == 0) continue;
                    if (!firstOpt) sb.Append(','); firstOpt = false;
                    // TOR/mods embed TMP rich-text (<color=#..>..</color>, <b>, ..) in the display name.
                    // Strip the tags for the browser and carry the first colour over as the row colour
                    // (visual parity with the in-game menu).
                    string nameColor;
                    string cleanName = StripTags(o.name, out nameColor);
                    sb.Append("{\"id\":").Append(o.id);
                    sb.Append(",\"name\":\"").Append(Esc(cleanName)).Append('"');
                    if (nameColor != null) sb.Append(",\"color\":\"").Append(nameColor).Append('"');
                    sb.Append(",\"header\":").Append(o.isHeader ? "true" : "false");
                    sb.Append(",\"heading\":\"").Append(Esc(StripTags(o.heading ?? "", out _))).Append('"');
                    sb.Append(",\"parent\":").Append(o.parent != null ? o.parent.id : -1);
                    sb.Append(",\"inverted\":").Append(o.invertedParent ? "true" : "false");
                    sb.Append(",\"sel\":").Append(Mathf.Clamp(o.selection, 0, o.selections.Length - 1));
                    sb.Append(",\"selections\":[");
                    for (int i = 0; i < o.selections.Length; i++) {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(Esc(StripTags(o.selections[i]?.ToString() ?? "", out _))).Append('"');
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
            }
            sb.Append(']');

            // --- Vanilla options ---
            sb.Append(",\"vanilla\":[");
            IGameOptions opts = null;
            try { opts = GameManager.Instance?.LogicOptions?.currentGameOptions; } catch { }
            if (opts != null) {
                bool firstV = true;
                foreach (var v in VanillaTable) {
                    float value;
                    try { value = v.Get(opts); } catch { continue; }
                    if (!firstV) sb.Append(','); firstV = false;
                    sb.Append("{\"key\":\"").Append(Esc(v.Key)).Append('"');
                    sb.Append(",\"label\":\"").Append(Esc(v.Label)).Append('"');
                    sb.Append(",\"section\":\"").Append(Esc(v.Section)).Append('"');
                    sb.Append(",\"kind\":\"").Append(v.Kind).Append('"');
                    sb.Append(",\"value\":").Append(Num(value));
                    if (v.Kind == "int" || v.Kind == "float") {
                        sb.Append(",\"min\":").Append(Num(v.Min));
                        sb.Append(",\"max\":").Append(Num(v.Max));
                        sb.Append(",\"step\":").Append(Num(v.Step));
                        sb.Append(",\"suffix\":\"").Append(Esc(v.Suffix ?? "")).Append('"');
                        if (v.ZeroInf) sb.Append(",\"zeroInf\":true");
                    } else if (v.Kind == "enum") {
                        sb.Append(",\"labels\":[");
                        for (int i = 0; i < v.Labels.Length; i++) {
                            if (i > 0) sb.Append(',');
                            sb.Append('"').Append(Esc(v.Labels[i])).Append('"');
                        }
                        sb.Append(']');
                    }
                    sb.Append('}');
                }
            }
            sb.Append(']');

            sb.Append('}');
            return (200, "application/json; charset=utf-8", sb.ToString());
        }

        // ====================================================================
        // Apply a single change (main thread, host-gated).
        // ====================================================================
        private static (int, string, string) ApplySet(Dictionary<string, string> form) {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
                return (403, "text/plain", "not host");

            string scope = form.TryGetValue("scope", out var s) ? s : "";

            if (scope == "mod") {
                if (!int.TryParse(form.GetValueOrDefault("id"), out int id)) return (400, "text/plain", "bad id");
                if (!int.TryParse(form.GetValueOrDefault("sel"), out int sel)) return (400, "text/plain", "bad sel");
                var option = CustomOption.options.FirstOrDefault(x => x.id == id);
                if (option == null || option.selections == null || option.selections.Length == 0)
                    return (404, "text/plain", "unknown option");
                sel = Mathf.Clamp(sel, 0, option.selections.Length - 1);
                // Canonical setter (clamps, persists, fires onChange, refreshes an open menu). Its tail
                // NRE's when the in-game menu was never opened, so guard it and guarantee the sync below.
                try { option.updateSelection(sel); } catch (Exception e) {
                    // The only realistic throw is updateSelection's tail menu-refresh (NRE when the
                    // in-game options menu was never opened) - which runs AFTER selection/onChange/share,
                    // so re-invoking onChange here would double-fire it. Just make sure the value + its
                    // persisted config entry are set; the ShareOptionSelections() below handles the sync.
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[WebConfig] updateSelection({id}) threw (harmless, menu closed): {e.Message}");
                    option.selection = sel;
                    if (option.entry != null) option.entry.Value = sel;
                }
                // Idempotent full re-broadcast - covers the menu-closed path where updateSelection
                // skips the per-option share (optionBehaviour is null until the menu is built).
                try { CustomOption.ShareOptionSelections(); } catch { }
                return (200, "text/plain", "ok");
            }

            if (scope == "vanilla") {
                string key = form.GetValueOrDefault("key");
                if (!float.TryParse(form.GetValueOrDefault("value"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value))
                    return (400, "text/plain", "bad value");
                var v = VanillaTable.FirstOrDefault(x => x.Key == key);
                if (v == null) return (404, "text/plain", "unknown vanilla option");
                IGameOptions opts = GameManager.Instance?.LogicOptions?.currentGameOptions;
                if (opts == null) return (503, "text/plain", "no game options");
                if (v.Kind == "int" || v.Kind == "float") {
                    float stepped = v.Step > 0 ? Mathf.Round(value / v.Step) * v.Step : value;
                    value = Mathf.Clamp(stepped, v.Min, v.Max);
                }
                v.Set(opts, value);
                try { GameManager.Instance.LogicOptions.SyncOptions(); } catch (Exception e) {
                    UsefulTORStuffPlugin.Logger?.LogWarning($"[WebConfig] SyncOptions failed: {e.Message}");
                }
                return (200, "text/plain", "ok");
            }

            return (400, "text/plain", "bad scope");
        }

        // ====================================================================
        // Curated vanilla option table - mirrors the Among Us Normal settings menu.
        // ====================================================================
        private sealed class VOpt {
            public string Key, Label, Section, Kind, Suffix;
            public float Min, Max, Step;
            public bool ZeroInf;
            public string[] Labels;
            public Func<IGameOptions, float> Get;
            public Action<IGameOptions, float> Set;
        }

        private static readonly VOpt[] VanillaTable = BuildVanillaTable();

        private static VOpt[] BuildVanillaTable() {
            VOpt B(string key, string label, string section, Func<IGameOptions, bool> g, Action<IGameOptions, bool> s) =>
                new VOpt { Key = key, Label = label, Section = section, Kind = "bool",
                           Get = o => g(o) ? 1f : 0f, Set = (o, v) => s(o, v > 0.5f) };
            VOpt I(string key, string label, string section, float min, float max, float step, string suffix,
                   Func<IGameOptions, int> g, Action<IGameOptions, int> s, bool zeroInf = false) =>
                new VOpt { Key = key, Label = label, Section = section, Kind = "int", Min = min, Max = max, Step = step,
                           Suffix = suffix, ZeroInf = zeroInf, Get = o => g(o), Set = (o, v) => s(o, (int)Mathf.Round(v)) };
            VOpt F(string key, string label, string section, float min, float max, float step, string suffix,
                   Func<IGameOptions, float> g, Action<IGameOptions, float> s) =>
                new VOpt { Key = key, Label = label, Section = section, Kind = "float", Min = min, Max = max, Step = step,
                           Suffix = suffix, Get = g, Set = s };
            VOpt E(string key, string label, string section, string[] labels,
                   Func<IGameOptions, int> g, Action<IGameOptions, int> s) =>
                new VOpt { Key = key, Label = label, Section = section, Kind = "enum", Labels = labels,
                           Get = o => g(o), Set = (o, v) => s(o, (int)Mathf.Round(v)) };

            return new[] {
                // --- Meetings & Voting ---
                B("ConfirmImpostor", "Confirm Ejects", "Meetings & Voting",
                    o => o.GetBool(BoolOptionNames.ConfirmImpostor), (o, v) => o.SetBool(BoolOptionNames.ConfirmImpostor, v)),
                I("NumEmergencyMeetings", "Emergency Meetings", "Meetings & Voting", 0, 9, 1, "",
                    o => o.GetInt(Int32OptionNames.NumEmergencyMeetings), (o, v) => o.SetInt(Int32OptionNames.NumEmergencyMeetings, v)),
                I("EmergencyCooldown", "Emergency Cooldown", "Meetings & Voting", 0, 60, 5, "s",
                    o => o.GetInt(Int32OptionNames.EmergencyCooldown), (o, v) => o.SetInt(Int32OptionNames.EmergencyCooldown, v)),
                I("DiscussionTime", "Discussion Time", "Meetings & Voting", 0, 120, 15, "s",
                    o => o.GetInt(Int32OptionNames.DiscussionTime), (o, v) => o.SetInt(Int32OptionNames.DiscussionTime, v)),
                I("VotingTime", "Voting Time", "Meetings & Voting", 0, 300, 15, "s",
                    o => o.GetInt(Int32OptionNames.VotingTime), (o, v) => o.SetInt(Int32OptionNames.VotingTime, v), zeroInf: true),
                B("AnonymousVotes", "Anonymous Votes", "Meetings & Voting",
                    o => o.GetBool(BoolOptionNames.AnonymousVotes), (o, v) => o.SetBool(BoolOptionNames.AnonymousVotes, v)),

                // --- Roles & Gameplay ---
                I("NumImpostors", "Impostors", "Roles & Gameplay", 1, 3, 1, "",
                    o => o.GetInt(Int32OptionNames.NumImpostors), (o, v) => o.SetInt(Int32OptionNames.NumImpostors, v)),
                F("KillCooldown", "Kill Cooldown", "Roles & Gameplay", 10, 60, 2.5f, "s",
                    o => o.GetFloat(FloatOptionNames.KillCooldown), (o, v) => o.SetFloat(FloatOptionNames.KillCooldown, v)),
                E("KillDistance", "Kill Distance", "Roles & Gameplay", new[] { "Short", "Medium", "Long" },
                    o => o.GetInt(Int32OptionNames.KillDistance), (o, v) => o.SetInt(Int32OptionNames.KillDistance, v)),
                F("PlayerSpeedMod", "Player Speed", "Roles & Gameplay", 0.5f, 3f, 0.25f, "x",
                    o => o.GetFloat(FloatOptionNames.PlayerSpeedMod), (o, v) => o.SetFloat(FloatOptionNames.PlayerSpeedMod, v)),
                F("CrewLightMod", "Crewmate Vision", "Roles & Gameplay", 0.25f, 5f, 0.25f, "x",
                    o => o.GetFloat(FloatOptionNames.CrewLightMod), (o, v) => o.SetFloat(FloatOptionNames.CrewLightMod, v)),
                F("ImpostorLightMod", "Impostor Vision", "Roles & Gameplay", 0.25f, 5f, 0.25f, "x",
                    o => o.GetFloat(FloatOptionNames.ImpostorLightMod), (o, v) => o.SetFloat(FloatOptionNames.ImpostorLightMod, v)),

                // --- Tasks ---
                E("TaskBarMode", "Task Bar Updates", "Tasks", new[] { "Always", "Meetings", "Never" },
                    o => o.GetInt(Int32OptionNames.TaskBarMode), (o, v) => o.SetInt(Int32OptionNames.TaskBarMode, v)),
                B("VisualTasks", "Visual Tasks", "Tasks",
                    o => o.GetBool(BoolOptionNames.VisualTasks), (o, v) => o.SetBool(BoolOptionNames.VisualTasks, v)),
                B("GhostsDoTasks", "Ghosts Do Tasks", "Tasks",
                    o => o.GetBool(BoolOptionNames.GhostsDoTasks), (o, v) => o.SetBool(BoolOptionNames.GhostsDoTasks, v)),
                I("NumCommonTasks", "Common Tasks", "Tasks", 0, 2, 1, "",
                    o => o.GetInt(Int32OptionNames.NumCommonTasks), (o, v) => o.SetInt(Int32OptionNames.NumCommonTasks, v)),
                I("NumLongTasks", "Long Tasks", "Tasks", 0, 15, 1, "",
                    o => o.GetInt(Int32OptionNames.NumLongTasks), (o, v) => o.SetInt(Int32OptionNames.NumLongTasks, v)),
                I("NumShortTasks", "Short Tasks", "Tasks", 0, 23, 1, "",
                    o => o.GetInt(Int32OptionNames.NumShortTasks), (o, v) => o.SetInt(Int32OptionNames.NumShortTasks, v)),
            };
        }

        // ====================================================================
        // Helpers
        // ====================================================================
        private static string TypeName(CustomOption.CustomOptionType type) {
            switch (type) {
                case CustomOption.CustomOptionType.General: return "General";
                case CustomOption.CustomOptionType.Impostor: return "Impostor";
                case CustomOption.CustomOptionType.Neutral: return "Neutral";
                case CustomOption.CustomOptionType.Crewmate: return "Crewmate";
                case CustomOption.CustomOptionType.Modifier: return "Modifier";
                case CustomOption.CustomOptionType.Guesser: return "Guesser";
                case CustomOption.CustomOptionType.HideNSeekMain: return "Hide N Seek";
                case CustomOption.CustomOptionType.HideNSeekRoles: return "Hide N Seek Roles";
                case CustomOption.CustomOptionType.PropHunt: return "Prop Hunt";
                default: return type.ToString();
            }
        }

        private static string Num(float f) {
            // Invariant, trim trailing ".0" for whole numbers so the page shows "45" not "45.0".
            if (Mathf.Abs(f - Mathf.Round(f)) < 0.0001f)
                return ((int)Mathf.Round(f)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // TMP rich-text: <color=#RRGGBB[AA]> / <#RRGGBB[AA]> / <b> / <size=..> etc. Removes every tag
        // and returns the plain text; the FIRST colour tag (if any) is handed back so the browser can
        // recolour the row the same way TMP would in the in-game menu.
        private static readonly Regex TagRe = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex ColorRe = new Regex("<(?:color=)?#([0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?)>", RegexOptions.Compiled);
        private static string StripTags(string s, out string color) {
            color = null;
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var m = ColorRe.Match(s);
            if (m.Success) color = "#" + m.Groups[1].Value;
            return TagRe.Replace(s, "").Trim();
        }

        private static string Esc(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s) {
                switch (c) {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ====================================================================
        // Embedded page
        // ====================================================================
        private static string pageHtml;
        private static string PageHtml() {
            if (pageHtml != null) return pageHtml;
            try {
                using var stream = typeof(WebConfig).Assembly
                    .GetManifestResourceStream("UsefulTORStuff.Resources.webconfig.html");
                using var reader = new StreamReader(stream);
                pageHtml = reader.ReadToEnd();
            } catch (Exception e) {
                pageHtml = "<!doctype html><meta charset=utf-8><body style='font-family:sans-serif;background:#111;color:#eee'>"
                         + "<h2>Web config page failed to load</h2><pre>" + e.Message + "</pre>";
            }
            return pageHtml;
        }
    }
}
