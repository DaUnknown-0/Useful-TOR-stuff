// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * UTSPets - custom pets in the wardrobe's pet tab, on the pattern of TOR's custom hats (2026-11-06).
 *
 * TOR creates a HatData per custom hat, appends it to HatManager.allHats and serves the pictures from its own cache
 * (CosmeticsCache.GetHat). Pets work the same way, except that a pet in the game is a whole prefab (PetBehaviour with
 * renderers, shadow, animator, physics). So every custom pet borrows the prefab of a vanilla pet (the "carrier":
 * PetPrefabRef is copied along with the PetData): loading, following, petting, the shadow and the network all stay the
 * game's. Only the picture is ours: when a copy of the prefab is made for one of our pets (CosmeticsLayer.InstantiatePetCopy)
 * it gets our PetData, and PetBehaviour.LateUpdate puts our sprite on it every frame (hover and fly cycle from a sprite
 * sheet). Chip picture and name come from CosmeticData.SetPreview/CoLoadIcon/GetItemName.
 *
 * Sources:
 *   - built in: Bit, the companion drone of The Unknown (Resources/pets/pet_bit.png, rendered from the film model in
 *     Assets/Branding/bit_pet), locked to the mod's author like the costume The Unknown (UnknownHat.cs).
 *   - online: pets/pets.json on main of DaUnknown-0/UTS-Music, sheets next to it (size + SHA-256 checked, kept in
 *     BepInEx/uts-pets; tools/build_online_pet.py writes both). An entry with the key of a built-in pet replaces its sheet.
 *     New or changed entries apply at once: new pets join the list, a changed sheet shows on the next frame.
 *   - other mods: a mod may hand over its own pets through the AppDomain key "UTSPets.External" (object[] of entries,
 *     each object[] { string key, string name, byte[] sheet PNG, string layout JSON as in pets.json, string carrier or null,
 *     string[] lockedTo hashes or null }). Read at start and every few seconds after; setting a new array replaces the set.
 *     The mod's own players see its pets; players without that mod see the carrier pet in their place.
 *
 * Lock ("lockedTo"): SHA-256 (lowercase hex) of the PUID or friend code of everyone who may wear the pet; empty = everyone.
 * Every pet is registered for EVERY player (or nobody would see the owner's pet); only the wardrobe is locked: no chip, no
 * equip, no RpcSetPet announcement, a saved choice goes back to "no pet". The same for pets that arrive or change live.
 *
 * When the list is read: at start from the last list on disk (works offline), then online; again on every lobby join (at
 * most once a minute) and with "/loadpets" (the author only; also asks everyone in the lobby to reload, UTS module 232
 * Sub 0, every receiver checks the sender itself).
 *
 * Diagnostics: UTS_PET_DIAG=1 (freeplay: equip, walk, screenshots in BepInEx/UTSPetShots; UTS_PET_DIAG_KEYS=a,b for several pets,
 * UTS_PET_DIAG_COLOR=<colour id> to wear another colour), "wardrobe" (pet tab in the main
 * menu), ",notowner" (act as somebody else). Nothing here is gameplay: no option, no host authority.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AmongUs.Data;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace UsefulTORStuff {
    public static class UTSPets {
        private const string LogPrefix = "[UTSPets]";
        internal const byte RpcModule = 232;
        private const byte SubReload = 0;
        internal const string Command = "/loadpets";
        internal const string IdPrefix = "pet_uts_";
        private const string EmptyPet = "pet_EmptyPet";
        private const string ListName = "pets.json";
        private const string DefaultUrl = "https://raw.githubusercontent.com/DaUnknown-0/UTS-Music/main/pets/";

        // test server for autotests only (loopback), see tools/build_online_pet.py
        private static string BaseUrl {
            get {
                string o = Environment.GetEnvironmentVariable("UTS_PET_SOURCE");
                return o != null && o.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && o.EndsWith("/") ? o : DefaultUrl;
            }
        }
        private const int MaxPets = 64;
        private const long MaxFile = 8L * 1024 * 1024;

        // carrier prefab: a plain vanilla pet with a single renderer (first one present wins, else any)
        private static readonly string[] Carriers = { "pet_UFO", "pet_Robot", "pet_Alien", "pet_Crewmate", "pet_Ellie", "pet_Bedcrab" };

        private static void Log(string s) => UsefulTORStuffPlugin.Logger?.LogInfo($"{LogPrefix} {s}");
        private static void Warn(string s) => UsefulTORStuffPlugin.Logger?.LogWarning($"{LogPrefix} {s}");

        // ---------------------------------------------------------------- data

        internal sealed class Def {
            public string Key, Name, Author = "DaUnknown-0", Carrier;
            public string File, Sha256;               // online: file name in the repo; built in: null
            public long Size;
            public byte[] Sheet;                      // sheet (PNG) once available
            public int CellW, CellH, Cols = 6;
            public int[] Idle, Walk;
            public float Fps = 12f, Height = 0.62f;
            public float Drop;                        // world units the picture sits below the carrier's sprite (ground pets on a hovering carrier)
            public int Accent;                        // > 0: frame i has a white colour mask at cell i + Accent, tinted in the owner's colour
            public bool Flip;                         // pictures face left instead of right
            public HashSet<string> Locks;             // null/empty = everyone
            public string ProductId => IdPrefix + Key;
            public string Signature => $"{Sha256 ?? "builtin"}|{CellW}x{CellH}|{Cols}|{string.Join(",", Idle ?? Array.Empty<int>())}|{string.Join(",", Walk ?? Array.Empty<int>())}|{Fps}|{Height}|{Drop}|{Accent}|{Flip}";
        }

        private sealed class Live {
            public Def Def;
            public Texture2D Tex;
            public Sprite[] Idle, Walk;               // sized for an unscaled parent (chips, previews)
            public Sprite[] AccIdle, AccWalk;         // colour masks to Idle/Walk, null without Def.Accent
            public Sprite Icon;
            public PetData Data;
            public string Signature;
            public readonly Dictionary<int, Sprite[][]> Scaled = new();   // per carrier root scale (x1000): idle, walk, accent idle, accent walk
        }

        private static readonly Dictionary<string, Live> live = new(StringComparer.Ordinal);       // ProductId -> registered
        private static readonly Dictionary<string, Def> pending = new(StringComparer.Ordinal);     // ProductId -> waiting for the HatManager
        private static readonly ConcurrentQueue<Action> mainQueue = new();
        private static readonly Regex KeyRx = new("^[a-z][a-z0-9]{1,23}$");
        private static readonly Regex FileRx = new("^pet_[a-z0-9_]{1,60}\\.png$");
        private static readonly Regex ShaRx = new("^[0-9a-f]{64}$");
        private static Material defaultMat;

        private static string Folder {
            get {
                string d = Path.Combine(BepInEx.Paths.BepInExRootPath, "uts-pets");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        // ---------------------------------------------------------------- start

        internal static void Init() {
            try { UTSRpc.Register(RpcModule, HandleRpc); }
            catch (Exception e) { Warn($"rpc registration failed: {e.Message}"); }
            try { Enqueue(BuiltInBit()); }
            catch (Exception e) { Warn($"built-in pet failed: {e.Message}"); }
            try { LoadFromDisk(); }
            catch (Exception e) { Warn($"cached list unreadable: {e.Message}"); }
            Refresh(true, "start");
        }

        private static Def BuiltInBit() {
            var sheet = ReadResource("UsefulTORStuff.Resources.pets.pet_bit.png");
            var meta = ReadResource("UsefulTORStuff.Resources.pets.pet_bit.json");
            if (sheet == null || meta == null) throw new FileNotFoundException("pet_bit resources missing");
            var d = new Def { Key = "bit", Name = "Bit", Carrier = "pet_UFO", Sheet = sheet, Locks = new HashSet<string>(EasterEggCoding.OwnerHashList) };
            using var doc = JsonDocument.Parse(meta);
            ReadLayout(doc.RootElement, d);
            return d;
        }

        private static void ReadLayout(JsonElement e, Def d) {
            if (e.TryGetProperty("cell", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 2) {
                d.CellW = c[0].GetInt32();
                d.CellH = c[1].GetInt32();
            }
            if (e.TryGetProperty("cols", out var cols)) d.Cols = Math.Clamp(cols.GetInt32(), 1, 64);
            d.Idle = Ints(e, "idle");
            d.Walk = Ints(e, "walk");
            if (e.TryGetProperty("fps", out var fps)) d.Fps = Mathf.Clamp(fps.GetSingle(), 1f, 60f);
            if (e.TryGetProperty("height", out var h)) d.Height = Mathf.Clamp(h.GetSingle(), 0.1f, 3f);
            if (e.TryGetProperty("drop", out var dr)) d.Drop = Mathf.Clamp(dr.GetSingle(), -2f, 2f);
            if (e.TryGetProperty("accent", out var ac) && ac.ValueKind == JsonValueKind.Number) d.Accent = Math.Clamp(ac.GetInt32(), 0, 4096);
            if (e.TryGetProperty("flip", out var fl) && (fl.ValueKind == JsonValueKind.True || fl.ValueKind == JsonValueKind.False)) d.Flip = fl.GetBoolean();
            if (d.CellW < 8 || d.CellH < 8 || d.CellW > 1024 || d.CellH > 1024) throw new FormatException($"{d.Key}: bad cell size");
            if (d.Idle == null || d.Idle.Length == 0) throw new FormatException($"{d.Key}: no idle frames");
            if (d.Walk == null || d.Walk.Length == 0) d.Walk = d.Idle;
        }

        private static int[] Ints(JsonElement e, string name) {
            if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return null;
            var l = new List<int>();
            foreach (var x in a.EnumerateArray()) if (x.ValueKind == JsonValueKind.Number) l.Add(Math.Clamp(x.GetInt32(), 0, 255));
            return l.Count > 0 ? l.Take(64).ToArray() : null;
        }

        // ---------------------------------------------------------------- online list

        private static float lastRefresh = -999f;
        private static bool refreshing;

        /// <summary>Fetch the list again (lobby join at most once a minute, otherwise at once).</summary>
        internal static void Refresh(bool force, string why) {
            float now = Time.realtimeSinceStartup;
            if (refreshing || (!force && now - lastRefresh < 60f)) return;
            lastRefresh = now;
            refreshing = true;
            Task.Run(async () => {
                try {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("UsefulTORStuff-Pets");
                    var resp = await http.GetAsync(BaseUrl + ListName + "?t=" + DateTime.UtcNow.Ticks).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) { mainQueue.Enqueue(() => Log($"no online list ({(int)resp.StatusCode}, {why})")); return; }
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var defs = Parse(json);
                    foreach (var d in defs) {
                        string path = Path.Combine(Folder, d.File);
                        byte[] data = null;
                        if (File.Exists(path)) {
                            var b = File.ReadAllBytes(path);
                            if (b.Length == d.Size && Sha(b) == d.Sha256) data = b;
                        }
                        if (data == null) {
                            var b = await http.GetByteArrayAsync(BaseUrl + d.File).ConfigureAwait(false);
                            if (b.Length != d.Size || Sha(b) != d.Sha256) { mainQueue.Enqueue(() => Warn($"{d.Key}: {d.File} failed the size/hash check")); continue; }
                            File.WriteAllBytes(path, b);
                            data = b;
                        }
                        d.Sheet = data;
                    }
                    File.WriteAllText(Path.Combine(Folder, ListName), json);
                    var ready = defs.Where(d => d.Sheet != null).ToList();
                    mainQueue.Enqueue(() => { foreach (var d in ready) Enqueue(d); Log($"online list: {ready.Count} pet(s) ({why})"); });
                } catch (Exception e) {
                    mainQueue.Enqueue(() => Warn($"online list failed ({why}): {e.Message}"));
                } finally {
                    refreshing = false;
                }
            });
        }

        private static void LoadFromDisk() {
            string p = Path.Combine(Folder, ListName);
            if (!File.Exists(p)) return;
            int n = 0;
            foreach (var d in Parse(File.ReadAllText(p))) {
                string f = Path.Combine(Folder, d.File);
                if (!File.Exists(f)) continue;
                var b = File.ReadAllBytes(f);
                if (b.Length != d.Size || Sha(b) != d.Sha256) continue;
                d.Sheet = b;
                Enqueue(d);
                n++;
            }
            Log($"cached list: {n} pet(s)");
        }

        internal static List<Def> Parse(string json) {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int format = root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.Number ? fmt.GetInt32() : 0;
            if (format != 1) throw new FormatException($"format {format} is not supported by this version");
            var list = new List<Def>();
            if (!root.TryGetProperty("pets", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in arr.EnumerateArray()) {
                if (list.Count >= MaxPets) break;
                try {
                    string min = Str(e, "minVersion");
                    if (min != null && Version.TryParse(min, out var mv) && UsefulTORStuffPlugin.Version < mv) continue;
                    var d = new Def { Key = Str(e, "key"), Name = Str(e, "name"), Author = Str(e, "author") ?? "DaUnknown-0", Carrier = Str(e, "carrier") };
                    if (d.Key == null || !KeyRx.IsMatch(d.Key)) throw new FormatException($"bad key '{d.Key}'");
                    if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 32) d.Name = d.Key;
                    if (!e.TryGetProperty("sheet", out var sh)) throw new FormatException($"{d.Key}: no sheet");
                    d.File = Str(sh, "file");
                    d.Sha256 = Str(sh, "sha256")?.ToLowerInvariant();
                    d.Size = sh.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : -1;
                    if (d.File == null || !FileRx.IsMatch(d.File)) throw new FormatException($"{d.Key}: bad file name");
                    if (d.Sha256 == null || !ShaRx.IsMatch(d.Sha256)) throw new FormatException($"{d.Key}: bad sha256");
                    if (d.Size <= 0 || d.Size > MaxFile) throw new FormatException($"{d.Key}: bad size");
                    ReadLayout(e, d);
                    if (e.TryGetProperty("lockedTo", out var lk) && lk.ValueKind == JsonValueKind.Array) {
                        d.Locks = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var x in lk.EnumerateArray()) {
                            string h = x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim().ToLowerInvariant() : null;
                            if (h != null && ShaRx.IsMatch(h)) d.Locks.Add(h);
                        }
                    }
                    list.Add(d);
                } catch (Exception ex) { Warn($"pet skipped: {ex.Message}"); }
            }
            return list;
        }

        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string Sha(byte[] b) {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(b)).ToLowerInvariant();
        }

        // ---------------------------------------------------------------- registration (main thread)

        private static void Enqueue(Def d) {
            pending[d.ProductId] = d;
            TryRegister();
        }

        /// <summary>Create waiting pets or swap changed sheets, once the HatManager has its list.</summary>
        internal static void TryRegister() {
            if (pending.Count == 0) return;
            var hm = HatManager.Instance;
            if (hm == null || hm.allPets == null || hm.allPets.Length == 0) return;
            foreach (var d in pending.Values.ToList()) {
                try {
                    if (live.TryGetValue(d.ProductId, out var l)) {
                        if (l.Signature != d.Signature) { BuildSprites(l, d); Log($"'{d.Name}' updated live"); }
                        l.Def = d;      // name, lock
                        if (l.Data != null) l.Data.name = d.Name;
                    } else {
                        var carrier = PickCarrier(hm, d.Carrier);
                        if (carrier == null) { Warn("no vanilla pet found to carry the custom pets"); return; }
                        var data = UnityEngine.Object.Instantiate(carrier);
                        data.name = d.Name;
                        data.ProductId = d.ProductId;
                        data.displayOrder = -100 + live.Count;      // custom pets first in the tab
                        data.Free = true;
                        data.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
                        var nl = new Live { Def = d, Data = data };
                        BuildSprites(nl, d);
                        var all = hm.allPets.ToList();
                        all.RemoveAll(p => p != null && p.ProductId == d.ProductId);
                        all.Add(data);
                        hm.allPets = new Il2CppReferenceArray<PetData>(all.ToArray());
                        live[d.ProductId] = nl;
                        Log($"registered '{d.Name}' as {d.ProductId} (carrier {carrier.ProductId}, {(d.Locks == null || d.Locks.Count == 0 ? "everyone" : d.Locks.Count + " lock(s)")})");
                    }
                } catch (Exception e) {
                    Warn($"'{d.Key}' not registered: {e.Message}");
                }
                pending.Remove(d.ProductId);
            }
        }

        private static PetData PickCarrier(HatManager hm, string wish) {
            PetData any = null;
            var byId = new Dictionary<string, PetData>(StringComparer.Ordinal);
            foreach (var p in hm.allPets) {
                if (p == null || string.IsNullOrEmpty(p.ProductId) || p.ProductId == EmptyPet || p.ProductId.StartsWith(IdPrefix, StringComparison.Ordinal)) continue;
                if (p.PetPrefabRef == null) continue;
                byId[p.ProductId] = p;
                any ??= p;
            }
            if (wish != null && byId.TryGetValue(wish, out var w)) return w;
            foreach (var c in Carriers) if (byId.TryGetValue(c, out var p)) return p;
            return any;
        }

        private static void BuildSprites(Live l, Def d) {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, d.Sheet, false)) throw new FormatException("sheet is no PNG");
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            l.Tex = tex;
            l.Def = d;
            l.Scaled.Clear();
            var cut = Cut(l, 1f);
            l.Idle = cut[0]; l.Walk = cut[1]; l.AccIdle = cut[2]; l.AccWalk = cut[3];
            l.Icon = MakeIcon(l);
            l.Signature = d.Signature;
        }

        /// <summary>The sheet cut into sprites whose world height is Def.Height under a parent of the given scale.</summary>
        private static Sprite[][] Cut(Live l, float parentScale) {
            var d = l.Def;
            var tex = l.Tex;
            int rows = Math.Max(1, tex.height / d.CellH);
            int count = d.Cols * rows;
            float ppu = d.CellH * Mathf.Max(0.01f, parentScale) / d.Height;
            Sprite Cell(int i) {
                i = Math.Clamp(i, 0, count - 1);
                int cx = i % d.Cols, cy = i / d.Cols;
                var s = Sprite.Create(tex, new Rect(cx * d.CellW, tex.height - (cy + 1) * d.CellH, d.CellW, d.CellH), new Vector2(0.5f, d.Drop / d.Height), ppu);
                s.name = "pet_" + d.Key + "_" + i;
                s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
                return s;
            }
            bool acc = d.Accent > 0;
            return new[] { d.Idle.Select(Cell).ToArray(), d.Walk.Select(Cell).ToArray(),
                           acc ? d.Idle.Select(i => Cell(i + d.Accent)).ToArray() : null, acc ? d.Walk.Select(i => Cell(i + d.Accent)).ToArray() : null };
        }

        /// <summary>Chip picture: the first idle frame, centred on the body and large enough to fill a wardrobe chip.</summary>
        private static Sprite MakeIcon(Live l) {
            var d = l.Def;
            int i = Math.Clamp(d.Idle[0], 0, d.Cols * Math.Max(1, l.Tex.height / d.CellH) - 1);
            int cx = i % d.Cols, cy = i / d.Cols;
            var s = Sprite.Create(l.Tex, new Rect(cx * d.CellW, l.Tex.height - (cy + 1) * d.CellH, d.CellW, d.CellH), new Vector2(0.5f, 0.62f),
                                  d.CellH / (d.Height * 3.8f));
            s.name = "pet_" + d.Key + "_icon";
            s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            return s;
        }

        private static Sprite[][] FramesFor(Live l, float scale) {
            int key = Mathf.RoundToInt(Mathf.Abs(scale) * 1000f);
            if (key == 1000) return new[] { l.Idle, l.Walk, l.AccIdle, l.AccWalk };
            if (!l.Scaled.TryGetValue(key, out var v)) {
                if (l.Scaled.Count > 16) l.Scaled.Clear();
                v = Cut(l, key / 1000f);
                l.Scaled[key] = v;
            }
            return v;
        }

        private static bool IsOurs(string productId, out Live l) {
            l = null;
            return productId != null && productId.StartsWith(IdPrefix, StringComparison.Ordinal) && live.TryGetValue(productId, out l);
        }

        // ---------------------------------------------------------------- who may wear it

        private static HashSet<string> localHashes = new(StringComparer.Ordinal);
        private static float localCheckedAt = -100f;

        private static HashSet<string> LocalHashes() {
            float now = Time.realtimeSinceStartup;
            if (now - localCheckedAt < 3f && localHashes.Count > 0) return localHashes;
            localCheckedAt = now;
            var set = new HashSet<string>(StringComparer.Ordinal);
            try {
                string puid = null, fc = null;
                var lp = PlayerControl.LocalPlayer;
                if (lp != null && lp.Data != null) { puid = lp.Data.Puid; fc = lp.Data.FriendCode; }
                var eos = EOSManager.Instance;
                if (string.IsNullOrEmpty(puid) && eos != null) puid = eos.ProductUserId?.ToString();
                if (string.IsNullOrEmpty(fc) && eos != null) fc = eos.FriendCode;
                foreach (var id in new[] { puid, fc })
                    if (!string.IsNullOrEmpty(id)) set.Add(Sha(Encoding.UTF8.GetBytes(id)));
            } catch { }
            localHashes = set;
            return set;
        }

        internal static bool LocalMay(string productId) {
            if (!IsOurs(productId, out var l)) return true;
            var locks = l.Def.Locks;
            if (locks == null || locks.Count == 0) return true;
            if (DiagNotOwner) return false;
            return LocalHashes().Overlaps(locks);
        }

        // ---------------------------------------------------------------- wardrobe

        [HarmonyPatch(typeof(HatManager), nameof(HatManager.GetPetById))]
        private static class GetByIdPatch {
            public static void Prefix() { try { TryRegister(); } catch { } }
        }

        [HarmonyPatch(typeof(HatManager), nameof(HatManager.GetUnlockedPets))]
        private static class UnlockedPatch {
            public static void Prefix() { try { TryRegister(); } catch { } }

            public static void Postfix(ref Il2CppReferenceArray<PetData> __result) {
                try {
                    if (__result == null || live.Count == 0) return;
                    bool drop = false;
                    foreach (var p in __result) if (p != null && !LocalMay(p.ProductId)) { drop = true; break; }
                    if (!drop) return;
                    var kept = new List<PetData>(__result.Length);
                    foreach (var p in __result) if (p != null && LocalMay(p.ProductId)) kept.Add(p);
                    __result = new Il2CppReferenceArray<PetData>(kept.ToArray());
                } catch (Exception e) { Warn($"wardrobe filter failed: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(PetsTab), nameof(PetsTab.ClickEquip))]
        private static class EquipPatch {
            public static bool Prefix(PetsTab __instance) {
                try { if (__instance != null && !LocalMay(__instance.GetCurrentProdID())) return false; } catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetPet))]
        private static class RpcSetPetPatch {
            public static void Prefix(PlayerControl __instance, ref string petId) {
                try {
                    if (__instance != PlayerControl.LocalPlayer || LocalMay(petId)) return;
                    petId = EmptyPet;
                    Log("locked pet: announcing no pet instead");
                } catch { }
            }
        }

        private static void SanitizeSaved() {
            try {
                var c = DataManager.Player?.Customization;
                if (c == null || LocalMay(c.Pet)) return;
                if (!IsOurs(c.Pet, out _)) return;
                c.Pet = EmptyPet;
                var lp = PlayerControl.LocalPlayer;
                if (lp != null && lp.Data != null && IsOurs(lp.Data.DefaultOutfit.PetId, out _)) lp.RpcSetPet(EmptyPet);
                Log("saved pet reset (locked)");
            } catch { }
        }

        [HarmonyPatch(typeof(PetsTab), nameof(PetsTab.OnEnable))]
        private static class TabOpenPatch {
            public static void Prefix() { try { TryRegister(); } catch { } }
            public static void Postfix() => SanitizeSaved();
        }

        // chip picture and name in the tab
        [HarmonyPatch(typeof(CosmeticData), nameof(CosmeticData.SetPreview))]
        private static class PreviewPatch {
            public static bool Prefix(CosmeticData __instance, SpriteRenderer renderer) {
                try {
                    if (__instance == null || !IsOurs(__instance.ProductId, out var l) || renderer == null) return true;
                    renderer.sprite = l.Icon;
                    if (DefaultMat() != null) renderer.sharedMaterial = DefaultMat();
                    return false;
                } catch { return true; }
            }
        }

        [HarmonyPatch(typeof(CosmeticData), nameof(CosmeticData.CoLoadIcon))]
        private static class IconPatch {
            public static bool Prefix(CosmeticData __instance, Il2CppSystem.Action<Sprite, Innersloth.Assets.AddressableAsset> onLoaded, ref Il2CppSystem.Collections.IEnumerator __result) {
                try {
                    if (__instance == null || !IsOurs(__instance.ProductId, out var l)) return true;
                    __result = Done(onLoaded, l.Icon).WrapToIl2Cpp();
                    return false;
                } catch { return true; }
            }

            private static IEnumerator Done(Il2CppSystem.Action<Sprite, Innersloth.Assets.AddressableAsset> cb, Sprite s) {
                try { cb?.Invoke(s, null); } catch (Exception e) { Warn($"icon callback: {e.Message}"); }
                yield break;
            }
        }

        [HarmonyPatch(typeof(CosmeticData), nameof(CosmeticData.GetItemName))]
        private static class NamePatch {
            public static void Postfix(CosmeticData __instance, ref string __result) {
                try { if (__instance != null && IsOurs(__instance.ProductId, out var l)) __result = l.Def.Name; } catch { }
            }
        }

        private static Material DefaultMat() {
            if (defaultMat == null) { try { defaultMat = HatManager.Instance != null ? HatManager.Instance.DefaultShader : null; } catch { } }
            return defaultMat;
        }

        // ---------------------------------------------------------------- the pet in the game

        // Which PetData a CosmeticsLayer is setting up (the copy of the carrier prefab only knows the carrier's PetData).
        private static readonly Dictionary<IntPtr, string> wanted = new();

        [HarmonyPatch(typeof(CosmeticsLayer), nameof(CosmeticsLayer.SetPetIdle), new[] { typeof(PetData), typeof(int), typeof(Il2CppSystem.Action) })]
        private static class SetIdleDataPatch {
            public static void Prefix(CosmeticsLayer __instance, PetData petData) {
                try { if (__instance != null) wanted[__instance.Pointer] = petData != null ? petData.ProductId : null; } catch { }
            }
        }

        [HarmonyPatch(typeof(CosmeticsLayer), nameof(CosmeticsLayer.SetPetIdle), new[] { typeof(string), typeof(int), typeof(Il2CppSystem.Action) })]
        private static class SetIdleIdPatch {
            public static void Prefix(CosmeticsLayer __instance, string petId) {
                try { TryRegister(); if (__instance != null) wanted[__instance.Pointer] = petId; } catch { }
            }
        }

        [HarmonyPatch(typeof(CosmeticsLayer), nameof(CosmeticsLayer.InstantiatePetCopy))]
        private static class CopyPatch {
            public static void Postfix(CosmeticsLayer __instance) {
                try {
                    if (__instance == null || !wanted.TryGetValue(__instance.Pointer, out var id)) return;
                    var pet = __instance.currentPet;
                    if (pet == null) return;
                    if (IsOurs(id, out var l)) {
                        pet.data = l.Data;
                        Dress(pet, l);
                    }
                } catch (Exception e) { Warn($"pet copy: {e.Message}"); }
            }
        }

        private static void Dress(PetBehaviour pet, Live l) {
            var rends = pet.renderers;
            if (rends == null) return;
            for (int i = 0; i < rends.Length; i++) {
                var r = rends[i];
                if (r == null) continue;
                if (i == 0) {
                    if (DefaultMat() != null) r.sharedMaterial = DefaultMat();
                    r.sprite = l.Icon;
                } else r.enabled = false;
            }
        }

        private static bool animFailed;

        // The scale of whatever holds the pet (player, wardrobe preview): the carrier's own scale is what we undo.
        private static float SceneScale(PetBehaviour pet) {
            var p = pet.transform.parent;
            return p != null ? p.lossyScale.y : 1f;
        }

        // colour mask: a child renderer over the pet picture, tinted in the owner's body colour (like the costume accents)
        private const string AccName = "uts_pet_accent";

        private static SpriteRenderer AccentRenderer(SpriteRenderer r0, bool want) {
            var t = r0.transform.Find(AccName);
            if (!want) {
                if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
                return null;
            }
            if (t == null) {
                var go = new GameObject(AccName) { layer = r0.gameObject.layer };
                go.transform.SetParent(r0.transform, false);
                go.transform.localPosition = new Vector3(0f, 0f, -0.0002f);
                go.AddComponent<SpriteRenderer>();
                t = go.transform;
            }
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            return t.GetComponent<SpriteRenderer>();
        }

        // the colour the owner shows right now (Morphling, Camouflage included); previews without an owner use the local colour
        private static Color OwnerColor(PetBehaviour pet) {
            try {
                var pc = pet.TargetPlayer;
                int id = -1;
                if (pc != null && pc.Data != null) {
                    try { id = pc.CurrentOutfit.ColorId; } catch { id = pc.Data.DefaultOutfit.ColorId; }
                } else if (DataManager.Player != null) id = DataManager.Player.Customization.Color;
                var bodies = Palette.PlayerColors;
                if (bodies != null && id >= 0 && id < bodies.Length) return bodies[id];
            } catch { }
            return Color.white;
        }

        [HarmonyPatch(typeof(PetBehaviour), nameof(PetBehaviour.LateUpdate))]
        private static class AnimPatch {
            public static void Postfix(PetBehaviour __instance) {
                if (animFailed || __instance == null || live.Count == 0) return;
                try {
                    var data = __instance.data;
                    if (data == null || !IsOurs(data.ProductId, out var l)) return;
                    var rends = __instance.renderers;
                    if (rends == null || rends.Length == 0 || rends[0] == null) return;
                    bool moving = false;
                    try {
                        var rb = __instance.rigidbody;
                        if (rb != null) moving = rb.velocity.sqrMagnitude > 0.004f;
                    } catch { }
                    var r0 = rends[0];
                    var set = FramesFor(l, r0.transform.lossyScale.y / Mathf.Max(1e-4f, Mathf.Abs(SceneScale(__instance))));
                    var frames = moving ? set[1] : set[0];
                    int f = (int)(Time.time * l.Def.Fps) % frames.Length;
                    if (r0.sprite != frames[f]) r0.sprite = frames[f];
                    if (l.Def.Flip) r0.flipX = !__instance.FlipX;
                    var dm = DefaultMat();
                    if (dm != null && r0.sharedMaterial != dm) r0.sharedMaterial = dm;
                    var acc = moving ? set[3] : set[2];
                    var ar = AccentRenderer(r0, acc != null);
                    if (ar != null) {
                        if (ar.sprite != acc[f]) ar.sprite = acc[f];
                        ar.flipX = r0.flipX;
                        if (dm != null && ar.sharedMaterial != dm) ar.sharedMaterial = dm;
                        // same order as the pet picture, in front of it only by the small z offset (like the costume accents):
                        // with order + 1 the mask was drawn over everything in the world, e.g. over a crate the pet stood
                        // behind, and it stayed visible in the dark outside your vision (User 06.11., two screenshots)
                        ar.sortingLayerID = r0.sortingLayerID;
                        ar.sortingOrder = r0.sortingOrder;
                        if (ar.enabled != r0.enabled) ar.enabled = r0.enabled;
                        ar.maskInteraction = r0.maskInteraction;
                        var body = OwnerColor(__instance);
                        ar.color = new Color(body.r, body.g, body.b, r0.color.a);
                    }
                    for (int i = 1; i < rends.Length; i++) if (rends[i] != null && rends[i].enabled) rends[i].enabled = false;
                } catch (Exception e) {
                    animFailed = true;
                    Warn($"animation disabled after an error: {e.Message}");
                }
            }
        }

        // ---------------------------------------------------------------- triggers

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
        private static class JoinedPatch {
            public static void Postfix() { try { Refresh(false, "lobby joined"); } catch { } }
        }

        // /loadpets: the author only; reloads here and asks everyone in the lobby to do the same
        [HarmonyPatch(typeof(ChatController), nameof(ChatController.SendChat))]
        private static class CommandPatch {
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(ChatController __instance) {
                try {
                    if (__instance == null || __instance.freeChatField == null) return true;
                    string text = __instance.freeChatField.Text;
                    if (string.IsNullOrEmpty(text) || text.Trim().ToLowerInvariant() != Command) return true;
                    __instance.freeChatField.Clear();
                    __instance.quickChatMenu?.Clear();
                    var lp = PlayerControl.LocalPlayer;
                    if (!EasterEggCoding.IsOwner(lp)) {
                        __instance.AddChat(lp, "Forgotten Fixes: this command is only for the mod author.");
                        return false;
                    }
                    Refresh(true, Command);
                    bool sent = false;
                    try {
                        var w = UTSRpc.Begin(RpcModule);
                        if (w != null) { w.Write(SubReload); AmongUsClient.Instance.FinishRpcImmediately(w); sent = true; }
                    } catch (Exception e) { Warn($"reload broadcast failed: {e.Message}"); }
                    __instance.AddChat(lp, sent ? "Forgotten Fixes: pet list is being reloaded for everyone in the lobby." : "Forgotten Fixes: pet list is being reloaded.");
                    return false;
                } catch (Exception e) {
                    Warn($"{Command}: {e.Message}");
                    return true;
                }
            }
        }

        private static void HandleRpc(MessageReader reader) {
            try {
                byte sub = reader.ReadByte();
                if (sub != SubReload) return;
                if (!EasterEggCoding.IsOwner(UTSRpc.Sender)) return;     // only from the author's client
                Refresh(true, "reload request");
            } catch (Exception e) { Warn($"rpc failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- pump (main thread)

        // pets handed over by other mods (see the header); checked now and then, only a new array is read again
        private static object externalSeen;
        private static float externalAt;

        private static void ScanExternal() {
            object o;
            try { o = AppDomain.CurrentDomain.GetData("UTSPets.External"); } catch { return; }
            if (o == null || ReferenceEquals(o, externalSeen)) return;
            externalSeen = o;
            if (o is not object[] entries) return;
            foreach (var x in entries) {
                try {
                    if (x is not object[] e || e.Length < 4) continue;
                    var d = new Def { Key = e[0] as string, Name = e[1] as string, Sheet = e[2] as byte[], Carrier = e.Length > 4 ? e[4] as string : null };
                    if (d.Key == null || !KeyRx.IsMatch(d.Key) || d.Sheet == null || d.Sheet.Length == 0 || d.Sheet.Length > MaxFile) throw new FormatException("bad entry");
                    if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 32) d.Name = d.Key;
                    d.Sha256 = Sha(d.Sheet);
                    using (var doc = JsonDocument.Parse(e[3] as string ?? "{}")) ReadLayout(doc.RootElement, d);
                    if (e.Length > 5 && e[5] is string[] locks && locks.Length > 0) d.Locks = new HashSet<string>(locks.Select(h => h.Trim().ToLowerInvariant()).Where(h => ShaRx.IsMatch(h)), StringComparer.Ordinal);
                    Enqueue(d);
                    Log($"pet '{d.Name}' handed over by another mod");
                } catch (Exception ex) { Warn($"external pet skipped: {ex.Message}"); }
            }
        }

        internal static void Tick() {
            if (Time.realtimeSinceStartup - externalAt > 3f) { externalAt = Time.realtimeSinceStartup; ScanExternal(); }
            int n = 0;
            while (n++ < 16 && mainQueue.TryDequeue(out var a)) { try { a(); } catch (Exception e) { Warn($"queued action: {e.Message}"); } }
            if (pending.Count > 0 && Time.frameCount % 30 == 0) TryRegister();
            DiagTick();
        }

        // ---------------------------------------------------------------- diagnostics (freeplay)

        private static readonly string DiagMode = Environment.GetEnvironmentVariable("UTS_PET_DIAG") ?? "";
        private static bool DiagNotOwner => DiagMode.Contains("notowner");
        private static float diagStart = -1f;
        private static int diagStep;

        // UTS_PET_DIAG_KEYS=a,b,c: equip each pet in turn (6 s each), shoot it standing and walking right, then "all done"
        private static readonly string[] DiagKeys = (Environment.GetEnvironmentVariable("UTS_PET_DIAG_KEYS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static void DiagTickKeys(PlayerControl lp, float t, string dir) {
            int i = (int)((t - 4f) / 6f);
            float u = t - 4f - i * 6f;
            if (t < 4f) return;
            if (i >= DiagKeys.Length) {
                diagWalk = 0f;
                if (diagStep != 99) { diagStep = 99; Log("diag: all done"); }
                return;
            }
            int phase = u < 0.5f ? 0 : u < 2.5f ? 1 : u < 4.5f ? 2 : 3;
            int step = 1000 + i * 10 + phase;
            if (step == diagStep) return;
            diagStep = step;
            string key = DiagKeys[i];
            if (phase == 0) {
                Directory.CreateDirectory(dir);
                diagWalk = 0f;
                if (int.TryParse(Environment.GetEnvironmentVariable("UTS_PET_DIAG_COLOR"), out int col)) lp.RpcSetColor((byte)col);
                lp.RpcSetPet(IdPrefix + key);
                Log($"diag: pet {key} set (known {live.ContainsKey(IdPrefix + key)}, may wear {LocalMay(IdPrefix + key)})");
            } else if (phase == 1) {
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"pet_{key}_idle.png"));
                var pet = lp.cosmetics != null ? lp.cosmetics.CurrentPet : null;
                if (pet != null && pet.renderers != null && pet.renderers.Length > 0 && pet.shadows != null && pet.shadows.Length > 0)
                    Log($"diag: {key} sprite base y - shadow y = {pet.renderers[0].transform.position.y - pet.shadows[0].transform.position.y:F3}, flipX {pet.renderers[0].flipX}");
                diagWalk = 1f;
            } else if (phase == 2) {
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"pet_{key}_walk.png"));
                diagWalk = -1f;
            } else {
                diagWalk = 0f;
            }
        }

        private static void DiagTick() {
            if (DiagMode.Length == 0 || ShipStatus.Instance == null || PlayerControl.LocalPlayer == null || live.Count == 0) return;
            var lp = PlayerControl.LocalPlayer;
            if (diagStart < 0f) { diagStart = Time.time; diagStep = 0; return; }
            float t = Time.time - diagStart;
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "UTSPetShots");
            if (DiagKeys.Length > 0) { DiagTickKeys(lp, t, dir); return; }
            if (diagStep == 0 && t > 4f) {
                diagStep = 1;
                Directory.CreateDirectory(dir);
                var hm = HatManager.Instance;
                Log("diag: vanilla pets: " + string.Join(", ", hm.allPets.Where(p => p != null).Select(p => p.ProductId)));
                var unlocked = hm.GetUnlockedPets();
                Log($"diag: unlocked contains ours: {unlocked.Any(p => p != null && p.ProductId.StartsWith(IdPrefix))}, may wear bit: {LocalMay(IdPrefix + "bit")}");
                lp.RpcSetPet(IdPrefix + "bit");
                Log("diag: pet set");
            } else if (diagStep == 1 && t > 7f) {
                diagStep = 2;
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "pet_idle.png"));
                var pet = lp.cosmetics != null ? lp.cosmetics.CurrentPet : null;
                Log($"diag: current pet {(pet != null ? pet.name : "none")}, data {(pet?.data != null ? pet.data.ProductId : "-")}, renderers {pet?.renderers?.Length ?? 0}, outfit {lp.Data.DefaultOutfit.PetId}");
                if (pet != null && pet.renderers != null && pet.renderers.Length > 0) {
                    var r0 = pet.renderers[0];
                    Log($"diag: pet root scale {pet.transform.lossyScale}, renderer '{r0.name}' local {r0.transform.localScale} lossy {r0.transform.lossyScale} pos {r0.transform.localPosition}, sprite {r0.sprite?.name} bounds {r0.bounds.size}, flipX {r0.flipX}, shadows {pet.shadows?.Length ?? 0}");
                }
            } else if (diagStep == 2 && t > 7.5f) {
                diagWalk = 1f;          // walk right, the companion follows
                if (t > 9.0f) { ScreenCapture.CaptureScreenshot(Path.Combine(dir, "pet_walk_right.png")); diagStep = 3; }
            } else if (diagStep == 3 && t > 9.3f) {
                diagWalk = -1f;
                if (t > 10.8f) { ScreenCapture.CaptureScreenshot(Path.Combine(dir, "pet_walk_left.png")); diagStep = 4; }
            } else if (diagStep == 4 && t > 11f) {
                diagWalk = 0f;
                if (t > 14f) {
                    diagStep = 5;
                    ScreenCapture.CaptureScreenshot(Path.Combine(dir, "pet_rest.png"));
                    Log("diag: all done");
                }
            }
        }

        private static float diagWalk;

        [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
        private static class DiagWalkPatch {
            public static bool Prepare() => DiagMode.Length > 0;
            public static void Postfix(PlayerPhysics __instance) {
                try {
                    if (diagWalk == 0f || __instance == null || !__instance.AmOwner || __instance.body == null) return;
                    __instance.body.velocity = new Vector2(diagWalk * 2.5f, 0f);
                } catch { }
            }
        }

        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
        private static class DiagMenuPatch {
            private static bool fired;
            public static bool Prepare() => DiagMode.Length > 0;
            public static void Postfix(MainMenuManager __instance) {
                if (fired) return;
                fired = true;
                __instance.StartCoroutine((DiagMode.Contains("wardrobe") ? DiagWardrobe(__instance) : DiagEnterFreeplay()).WrapToIl2Cpp());
            }
        }

        private static IEnumerator DiagWardrobe(MainMenuManager mm) {
            float t0 = Time.time;
            while (true) {
                bool ok = false;
                try { ok = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                if (ok) break;
                if (Time.time - t0 > 60f) { Warn("diag: login timeout"); yield break; }
                yield return null;
            }
            yield return new WaitForSeconds(2f);
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "UTSPetShots");
            Directory.CreateDirectory(dir);
            PlayerCustomizationMenu menu = null;
            PetsTab tab = null;
            try {
                TryRegister();
                menu = UnityEngine.Object.Instantiate(mm.playerCustomizationPrefab);
                menu.Open();
                Log("diag: wardrobe opened");
            } catch (Exception e) { Warn($"diag: wardrobe failed: {e}"); yield break; }
            yield return new WaitForSeconds(1.5f);
            try {
                tab = menu.GetComponentInChildren<PetsTab>(true);
                menu.OpenTab(tab);
                Log($"diag: pets tab open, chips {tab.ColorChips?.Count ?? 0}");
            } catch (Exception e) { Warn($"diag: pets tab failed: {e}"); yield break; }
            yield return new WaitForSeconds(2f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "wardrobe_tab.png"));
            yield return new WaitForSeconds(0.5f);
            try {
                var chips = tab.ColorChips;
                if (chips != null && chips.Count > 0 && live.TryGetValue(IdPrefix + "bit", out var l)) {
                    tab.SelectPet(chips[0], l.Data);
                    Log($"diag: selected, current {tab.GetCurrentProdID()}, name '{l.Data.GetItemName()}'");
                }
            } catch (Exception e) { Warn($"diag: select failed: {e}"); }
            yield return new WaitForSeconds(2.5f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "wardrobe_preview.png"));
            yield return new WaitForSeconds(1f);
            Log("diag: all done");
        }

        private static IEnumerator DiagEnterFreeplay() {
            float t0 = Time.time;
            while (true) {
                bool ok = false;
                try { ok = EOSManager.Instance != null && EOSManager.Instance.HasFinishedLoginFlow(); } catch { }
                if (ok) break;
                if (Time.time - t0 > 60f) { Warn("diag: login timeout"); yield break; }
                yield return null;
            }
            try {
                var popover = UnityEngine.Object.FindObjectOfType<FreeplayPopover>(true);
                if (popover == null) { Warn("diag: no FreeplayPopover"); yield break; }
                for (var t = popover.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                popover.Show();
                popover.PlayMap(MapNames.Skeld);
                Log("diag: freeplay started");
            } catch (Exception e) { Warn($"diag: freeplay failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- helpers

        private static byte[] ReadResource(string name) {
            using var s = typeof(UTSPets).Assembly.GetManifestResourceStream(name);
            if (s == null) return null;
            var data = new byte[s.Length];
            int read = 0;
            while (read < data.Length) { int n = s.Read(data, read, data.Length - read); if (n <= 0) break; read += n; }
            return read == data.Length ? data : null;
        }
    }

    /// <summary>Pump for UTSPets (downloads onto the main thread, pending registrations, diagnostics).</summary>
    public class UTSPetsDriver : MonoBehaviour {
        public UTSPetsDriver(IntPtr ptr) : base(ptr) { }
        private void Update() { try { UTSPets.Tick(); } catch { } }
    }
}
