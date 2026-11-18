// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using TheOtherRoles.Modules.CustomHats;
using UnityEngine;

namespace UsefulTORStuff;

internal sealed class FeelingCuteHat {
    private const string OwnerHash = "6000e7a041af292fa9c437cf6d85c0c0d813f67d354b55298fe25f5cb9c8a124";
    private const string AssetUrl = "https://raw.githubusercontent.com/DaUnknown-0/UTS-Music/main/costumes/";
    private const int WalkCount = 12;

    private static readonly Vector2 Pivot = new(0.53f, 0.575f);
    private static readonly FeelingCuteHat[] Costumes = {
        new("Feeling Cute Blue Cat", "UTS_FeelingCute", "feeling-cute-v1.zip", 1836671,
            "f35cbe6aea427288a6798615c02839ebb0f43e07d6c35983bec3c7a83b61879d"),
        new("Feeling Cute Pawprint Cat", "UTS_FeelingCutePawprint", "feeling-cute-v2.zip", 1945853,
            "12ba5c7dff534f8616cc6e54d38aabba61cbd151d28505e4e4f215a7a500a4f9"),
        new("Feeling Cute Overalls", "UTS_FeelingCuteOveralls", "feeling-cute-v3.zip", 1342816,
            "ebdb8bc06f656fe0ad6649ef8630feb79a35fb55061bfbaba8694e0ec392808b", coversBody: false),
    };
    private static string lastPuid;
    private static bool lastMatch;
    private static bool animationFailed;

    private readonly string hatName, packageFile, packageHash, main, back, climb;
    private readonly int packageSize;
    private readonly bool coversBody;
    private readonly string[] diskFiles;
    private Task<byte[]> download;
    private Sprite[] poses, accents;
    private Dictionary<string, HatViewData> views;
    private HatViewData view;

    private FeelingCuteHat(string hatName, string fileStem, string packageFile, int packageSize, string packageHash,
        bool coversBody = true) {
        this.hatName = hatName;
        ProductId = "hat_" + hatName.Replace(' ', '_');
        this.packageFile = packageFile;
        this.packageSize = packageSize;
        this.packageHash = packageHash;
        this.coversBody = coversBody;
        main = fileStem + ".png";
        back = fileStem + "_back.png";
        climb = fileStem + "_climb.png";
        diskFiles = new[] { main, back, climb };
    }

    private string ProductId { get; }
    internal bool Registered { get; private set; }
    internal static bool AnyRegistered { get; private set; }

    internal static FeelingCuteHat Find(string productId) {
        foreach (var costume in Costumes)
            if (costume.ProductId == productId) return costume;
        return null;
    }

    internal static bool IsDiskFile(string name) => Costumes.Any(costume => costume.diskFiles.Contains(name));

    private static string LocalPuid() {
        var data = PlayerControl.LocalPlayer?.Data;
        if (!string.IsNullOrEmpty(data?.Puid)) return data.Puid;
        return EOSManager.Instance?.ProductUserId?.ToString();
    }

    internal static bool LocalMatches() {
        string puid = LocalPuid();
        if (string.IsNullOrEmpty(puid)) return false;
        if (puid != lastPuid) {
            lastMatch = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(puid)))
                .Equals(OwnerHash, StringComparison.OrdinalIgnoreCase);
            lastPuid = puid;
        }
        return lastMatch;
    }

    internal static bool IdentityAvailable() => !string.IsNullOrEmpty(LocalPuid());

    private void Warn(string message) => UsefulTORStuffPlugin.Logger?.LogWarning($"[{hatName}] {message}");

    private string CacheFile => Path.Combine(BepInEx.Paths.BepInExRootPath, "uts-costumes", packageFile);

    internal static void Init() {
        foreach (var costume in Costumes) costume.LoadPackage();
    }

    private void LoadPackage() {
        try {
            if (File.Exists(CacheFile) && new FileInfo(CacheFile).Length == packageSize) {
                byte[] cached = File.ReadAllBytes(CacheFile);
                if (Verify(cached)) {
                    Register(cached);
                    return;
                }
            }
        } catch (Exception ex) {
            Warn("Der Kostümcache konnte nicht geladen werden. " + ex.Message);
        }
        download = Task.Run(Fetch);
    }

    private bool Verify(byte[] bytes) => bytes.Length == packageSize &&
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(packageHash, StringComparison.OrdinalIgnoreCase);

    private async Task<byte[]> Fetch() {
        string source = AssetUrl;
        string local = Environment.GetEnvironmentVariable("UTS_COSTUME_SOURCE");
        if (Uri.TryCreate(local, UriKind.Absolute, out var loopback) && loopback.Scheme == "http" &&
            loopback.Host == "127.0.0.1" && local.EndsWith("/", StringComparison.Ordinal))
            source = local;

        using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = new HttpClient();
        using var response = await http.GetAsync(source + packageFile, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var data = new byte[packageSize];
        int offset = 0, count;
        while (offset < data.Length && (count = await stream.ReadAsync(data.AsMemory(offset), deadline.Token)) > 0)
            offset += count;
        if (offset != data.Length || await stream.ReadAsync(new byte[1], deadline.Token) != 0 || !Verify(data))
            throw new InvalidDataException("Größe oder SHA-256 des Kostümpakets stimmt nicht.");

        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
        string temporary = CacheFile + "." + Guid.NewGuid().ToString("N") + ".part";
        try {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, CacheFile, true);
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return data;
    }

    [HarmonyPatch(typeof(HatManager), nameof(HatManager.GetHatById))]
    private static class HatLookupPatch {
        [HarmonyPriority(Priority.First)]
        private static void Prefix() => Poll();
    }

    [HarmonyPatch(typeof(HatManager), nameof(HatManager.GetUnlockedHats))]
    private static class WardrobeLookupPatch {
        private static void Prefix(HatManager __instance) {
            Poll();
            foreach (var costume in Costumes)
                if (costume.Registered && costume.view == null) __instance.GetHatById(costume.ProductId);
        }
    }

    internal static void Poll() {
        foreach (var costume in Costumes) costume.PollPackage();
    }

    private void PollPackage() {
        if (download == null || !download.IsCompleted) return;
        var complete = download;
        download = null;
        try { Register(complete.GetAwaiter().GetResult()); }
        catch (Exception ex) { Warn("Das Kostüm konnte nicht geladen werden. " + ex.Message); }
    }

    private void Register(byte[] data) {
        var manager = typeof(CustomHatManager);
        var list = manager.GetField("UnregisteredHats", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            as List<CustomHat>;
        views = manager.GetField("ViewDataCache", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            as Dictionary<string, HatViewData>;
        var loader = manager.Assembly.GetType("TheOtherRoles.Modules.CustomHats.Patches.HatManagerPatches");
        var loaded = loader?.GetField("isLoaded", BindingFlags.NonPublic | BindingFlags.Static);
        string dir = UnknownHat.ResolveHatsDirectory();
        if (list == null || views == null || loaded == null || dir == null)
            throw new InvalidOperationException("TOR-Hutregistrierung fehlt.");
        if (views.ContainsKey(hatName) || list.Any(h => h?.Name == hatName))
            throw new InvalidOperationException($"Der Hutname „{hatName}“ ist bereits belegt.");

        using var archive = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        var poseBytes = new byte[WalkCount][];
        var accentBytes = new byte[WalkCount + 2][];
        for (int i = 0; i < WalkCount; i++) {
            poseBytes[i] = ReadEntry(archive, $"walk_{i + 1:00}.png");
            if (coversBody) accentBytes[i + 1] = ReadEntry(archive, $"accent_walk_{i + 1:00}.png");
        }
        if (coversBody) {
            accentBytes[0] = ReadEntry(archive, "accent_idle.png");
            accentBytes[^1] = ReadEntry(archive, "accent_climb.png");
        }

        Directory.CreateDirectory(dir);
        foreach (string file in diskFiles) {
            byte[] bytes = ReadEntry(archive, file);
            string path = Path.Combine(dir, file);
            if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes))
                File.WriteAllBytes(path, bytes);
        }

        poses = poseBytes.Select(LoadSprite).ToArray();
        if (coversBody) accents = accentBytes.Select(LoadSprite).ToArray();
        list.Add(new CustomHat {
            Name = hatName, Author = "DaUnknown-0", Package = "Unknown's Collective",
            Resource = main, BackResource = back, ClimbResource = climb,
            Adaptive = false, Bounce = true, Behind = true,
        });
        loaded.SetValue(null, false);
        Registered = true;
        AnyRegistered = true;
        UsefulTORStuffPlugin.Logger?.LogInfo($"[{hatName}] Kostüm registriert.");
    }

    private static byte[] ReadEntry(ZipArchive archive, string name) {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException(name);
        using var stream = entry.Open();
        using var bytes = new MemoryStream((int)entry.Length);
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static Sprite LoadSprite(byte[] data) {
        var texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
        if (!ImageConversion.LoadImage(texture, data, true)) {
            UnityEngine.Object.Destroy(texture);
            throw new InvalidDataException("Ein Kostümbild konnte nicht gelesen werden.");
        }
        texture.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Pivot, texture.width * 0.375f);
        sprite.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    private sealed class Display {
        internal HatParent Hat;
        internal CosmeticsLayer Cosmetics;
        internal PlayerControl Player;
        internal SpriteRenderer Accent;
        internal Transform Body, Skin, Visor, HiddenFront;
        internal Vector3 BodyScale, SkinScale, VisorScale, FrontScale;
    }

    private static readonly Dictionary<IntPtr, Display> displays = new();

    private static void Conceal(ref Transform target, ref Vector3 scale, Transform next) {
        if (target != next) {
            Reveal(ref target, scale);
            if (next == null || next.localScale == Vector3.zero) return;
            target = next;
            scale = next.localScale;
        }
        if (target != null && target.localScale != Vector3.zero) target.localScale = Vector3.zero;
    }

    private static void Reveal(ref Transform target, Vector3 scale) {
        if (target != null && target.localScale == Vector3.zero) target.localScale = scale;
        target = null;
    }

    private static void RestoreBody(Display display) {
        Reveal(ref display.Body, display.BodyScale);
        Reveal(ref display.Skin, display.SkinScale);
        Reveal(ref display.Visor, display.VisorScale);
    }

    internal static void Restore(HatParent hat) {
        if (displays.Count == 0 || !displays.TryGetValue(hat.Pointer, out var display)) return;
        RestoreBody(display);
        Reveal(ref display.HiddenFront, display.FrontScale);
        if (display.Accent != null && display.Accent.enabled) display.Accent.enabled = false;
    }

    internal void Animate(HatParent hat) {
        if (animationFailed) return;
        try { Draw(hat); }
        catch (Exception ex) {
            animationFailed = true;
            foreach (var display in displays.Values) {
                RestoreBody(display);
                Reveal(ref display.HiddenFront, display.FrontScale);
                if (display.Accent != null) display.Accent.enabled = false;
            }
            Warn("Die Kostümanimation wurde nach einem Fehler angehalten. " + ex.Message);
        }
    }

    private void Draw(HatParent hat) {
        var front = hat.FrontLayer;
        if (front == null || !front.enabled || front.sprite == null) { Restore(hat); return; }
        if (!displays.TryGetValue(hat.Pointer, out var display) || display.Hat == null) {
            if (displays.Count > 128)
                foreach (var dead in displays.Where(e => e.Value.Hat == null).Select(e => e.Key).ToArray())
                    displays.Remove(dead);
            display = new Display {
                Hat = hat, Cosmetics = hat.GetComponentInParent<CosmeticsLayer>(),
                Player = hat.GetComponentInParent<PlayerControl>(),
            };
            displays[hat.Pointer] = display;
        }

        var player = display.Player;
        var cosmetics = display.Cosmetics;
        bool unsupported = (cosmetics != null && cosmetics.CurrentBodyType != PlayerBodyTypes.Normal) ||
            (player != null && (player.Data == null || player.Data.IsDead || player.inVent || player.inMovingPlat));
        if (unsupported) {
            Restore(hat);
            Conceal(ref display.HiddenFront, ref display.FrontScale, front.transform);
            return;
        }
        Reveal(ref display.HiddenFront, display.FrontScale);

        var back = hat.BackLayer;
        bool livePose = back != null && back.enabled;
        if (view == null) views.TryGetValue(hatName, out view);
        bool climb = !livePose && view != null && front.sprite == view.ClimbImage;
        if (!livePose && !climb) { Restore(hat); return; }

        if (!coversBody) Restore(hat);
        else if (cosmetics != null && cosmetics.currentBodySprite != null) {
            Conceal(ref display.Body, ref display.BodyScale, cosmetics.currentBodySprite.BodySprite?.transform);
            Conceal(ref display.Skin, ref display.SkinScale, cosmetics.skin?.transform);
            Conceal(ref display.Visor, ref display.VisorScale, cosmetics.visor?.transform);
        }

        int index = livePose ? UnknownHat.RunFrameIndex(player) : -1;
        if (livePose) {
            UnknownHat.FollowHead(hat, front, back);
            Sprite pose = index >= 0 ? poses[index] : view?.MainImage;
            if (pose != null && front.sprite != pose) front.sprite = pose;
        }
        if (coversBody) DrawAccent(hat, display, front, accents[climb ? accents.Length - 1 : index + 1]);
    }

    private static void DrawAccent(HatParent hat, Display display, SpriteRenderer front, Sprite sprite) {
        if (display.Accent == null) {
            var go = new GameObject("FeelingCuteAccent");
            go.transform.SetParent(front.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.0002f);
            display.Accent = go.AddComponent<SpriteRenderer>();
        }
        var accent = display.Accent;
        if (accent.sprite != sprite) accent.sprite = sprite;
        if (accent.sharedMaterial != front.sharedMaterial) accent.sharedMaterial = front.sharedMaterial;
        accent.flipX = front.flipX;
        accent.maskInteraction = front.maskInteraction;
        accent.sortingLayerID = front.sortingLayerID;
        accent.sortingOrder = front.sortingOrder;
        accent.gameObject.layer = front.gameObject.layer;
        int id = hat.matProperties.ColorId;
        Color tint = id >= 0 && id < Palette.PlayerColors.Length ? (Color)Palette.PlayerColors[id] : Color.white;
        accent.color = new Color(tint.r, tint.g, tint.b, front.color.a);
        if (!accent.enabled) accent.enabled = true;
    }
}
