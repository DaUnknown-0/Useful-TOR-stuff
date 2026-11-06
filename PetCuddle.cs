// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * PetCuddle - while a player pets their pet, the crewmate kneels down, picks the pet up, cuddles it, rocks it, puts it
 * down and stands up again (User 2026-11-06, from DrSuspect's feedback "pets: animation for cuddling").
 *
 * No network message of its own: the game already tells every client when somebody pets (PlayerPhysics.RpcPet ->
 * CoPet; PlayerControl.petting, PetBehaviour.beingPet), so every client with this version plays it for every player and
 * older versions simply see the normal petting.
 *
 * The pictures (GPT, Assets/Branding/gpt_pets/cut_cuddle.py, Resources/pets/cuddle_*) live in the hat canvas (600 x 750,
 * the real crewmate measured in game, HalloweenCostumeArt/pipeline/realbean) and are placed like a hat: pivot (0.53, 0.575),
 * pixelsPerUnit = width * 0.375, at the hat's own spot. They use the game's colour key (red = body, blue = shadow,
 * green = visor), so the player's own body material colours them (Morphling and Camouflage included). Two layers: the body,
 * and the hands in front of the pet. Body, skin and visor cosmetic are hidden (scale 0, like the Halloween whole-figure
 * costumes) and so is the game's petting hand; the hat stays and follows the head. The pet is drawn as a copy of its
 * current picture: on the ground, then between the hands, at most PetHeldShare of the crewmate's height (the online pets
 * are nearly as tall as a crewmate and hid him completely). Any other movement of the player ends it at once and puts
 * everything back.
 *
 * 2026-11-06 (1.7.4, User screenshot "Groesse viel zu klein"): the pictures were laid over the BODY sprite's rectangle,
 * which is much tighter than the hat canvas they were drawn in, so the crewmate shrank behind the pet.
 *
 * Local choice (User 2026-11-06: "gewaehlt zwischen normalem Petten und Cuddle"): [Pets] PetCuddle in the config, or the
 * "Pet Cuddle" switch on the Unknown's Collection page of TOR's Mod Options. Off = the game's normal petting, for every
 * player on this screen.
 */

using System;
using System.Collections.Generic;
using System.Text.Json;
using BepInEx.Configuration;
using UnityEngine;

namespace UsefulTORStuff;

internal static class PetCuddle {
    internal static ConfigEntry<bool> Enabled;

    internal static void Bind(ConfigFile config) {
        Enabled = config.Bind("Pets", "PetCuddle", true,
            "While somebody pets their pet, their crewmate kneels down and cuddles it. Off = the game's normal petting. "
            + "Only affects your own game.");
        ExternalToggles.Register(() => UTSLocalization.Tr("uts.options.pet_cuddle_toggle"),
            () => Enabled.Value, v => Enabled.Value = v);
    }

    private const string Res = "UsefulTORStuff.Resources.pets.";
    private const float PetAboveHands = 0.30f;       // the pet's centre sits this share of its height above the hands
    private const float PetHeldShare = 0.42f;        // a held pet is at most this share of the standing crewmate's height
    private const float BeanShare = 433f / 750f;     // the standing crewmate's height in the hat canvas (realbean cut_idle)
    private const float BlendSeconds = 0.18f;        // picking up and putting down glide instead of jumping

    // ---------------------------------------------------------------- pictures

    private static bool loaded, loadFailed;
    private static int cellW, cellH, cols, pickup, putdown, frameCount;
    private static float fps = 8f;
    private static Vector2?[] hands = Array.Empty<Vector2?>();
    private static Vector2[] head = Array.Empty<Vector2>();
    private static Texture2D bodyTex, handTex;
    private static Sprite[] bodySprites, handSprites;
    private static int[] sequence = Array.Empty<int>();
    private static float spritePpu;
    private static Vector2 spritePivot;

    private static bool Load() {
        if (loaded) return true;
        if (loadFailed) return false;
        try {
            var json = ReadResource(Res + "cuddle.json");
            bodyTex = Tex(ReadResource(Res + "cuddle_body.png"));
            handTex = Tex(ReadResource(Res + "cuddle_hands.png"));
            if (json == null || bodyTex == null || handTex == null) throw new InvalidOperationException("resources missing");
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            cellW = e.GetProperty("cell")[0].GetInt32();
            cellH = e.GetProperty("cell")[1].GetInt32();
            cols = e.GetProperty("cols").GetInt32();
            fps = Mathf.Clamp(e.GetProperty("fps").GetSingle(), 2f, 30f);
            pickup = e.GetProperty("pickup").GetInt32();
            putdown = e.GetProperty("putdown").GetInt32();
            var hl = new List<Vector2?>();
            foreach (var h in e.GetProperty("hands").EnumerateArray())
                hl.Add(h.ValueKind == JsonValueKind.Array ? new Vector2(h[0].GetSingle(), h[1].GetSingle()) : (Vector2?)null);
            hands = hl.ToArray();
            var hd = new List<Vector2>();
            foreach (var h in e.GetProperty("head").EnumerateArray()) hd.Add(new Vector2(h[0].GetSingle(), h[1].GetSingle()));
            head = hd.ToArray();
            frameCount = hands.Length;
            // kneel, pick up, hug (the hug part twice for about three seconds), put down, stand up
            int loopFrom = Math.Min(pickup + 3, frameCount - 1), loopTo = Math.Max(loopFrom, putdown - 2);
            var seq = new List<int>();
            for (int i = 0; i <= loopTo; i++) seq.Add(i);
            for (int i = loopFrom; i <= loopTo; i++) seq.Add(i);
            for (int i = loopTo + 1; i < frameCount; i++) seq.Add(i);
            sequence = seq.ToArray();
            loaded = true;
            UsefulTORStuffPlugin.Logger.LogInfo($"[UTS/PetCuddle] {frameCount} frame(s), {sequence.Length / fps:0.0} s, pick up at {pickup}, put down at {putdown}");
            return true;
        } catch (Exception ex) {
            loadFailed = true;
            UsefulTORStuffPlugin.Logger.LogWarning($"[UTS/PetCuddle] off: {ex.Message}");
            return false;
        }
    }

    private static Texture2D Tex(byte[] png) {
        if (png == null) return null;
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(t, png, false)) return null;
        t.filterMode = FilterMode.Bilinear;
        t.wrapMode = TextureWrapMode.Clamp;
        t.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
        return t;
    }

    /// <summary>Sprites with the hat calibration (TOR's CreateHatSprite, Halloween's LoadHatSprite).</summary>
    private static void MakeSprites() {
        if (bodySprites != null) return;
        spritePivot = new Vector2(0.53f, 0.575f);
        spritePpu = cellW * 0.375f;
        Sprite[] Cut(Texture2D tex, string name) {
            var list = new Sprite[frameCount];
            for (int i = 0; i < frameCount; i++) {
                int cx = i % cols, cy = i / cols;
                var s = Sprite.Create(tex, new Rect(cx * cellW, tex.height - (cy + 1) * cellH, cellW, cellH), spritePivot, spritePpu);
                s.name = $"uts_cuddle_{name}_{i}";
                s.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
                list[i] = s;
            }
            return list;
        }
        bodySprites = Cut(bodyTex, "body");
        handSprites = Cut(handTex, "hands");
    }

    /// <summary>A point of the picture (0..1, origin bottom left) in the local space of the animation root.</summary>
    private static Vector3 Local(Vector2 uv, bool flip) {
        float x = (uv.x - spritePivot.x) * cellW / spritePpu, y = (uv.y - spritePivot.y) * cellH / spritePpu;
        return new Vector3(flip ? -x : x, y, 0f);
    }

    // ---------------------------------------------------------------- playing

    private sealed class Play {
        public PlayerControl P;
        public float Start;
        public Vector2 StartPos;
        public GameObject Root, PetGo;
        public SpriteRenderer Body, Hands, Pet, PetAccent, BodySrc;
        public Transform BodyT, SkinT, VisorT, HatT;
        public Vector3 BodyScale, SkinScale, VisorScale, HatPos;
        public bool Held;                       // pet between the hands (last frame)
        public float HeldSince = -1f;           // when Held last changed (for the glide)
        public Vector3 FromPos;                 // the pet's position and size when Held last changed
        public float FromSize = 1f;
        public float Size = 1f;
        public readonly List<Renderer> PetHidden = new();
        public readonly List<Renderer> HandHidden = new();
    }

    private static readonly Dictionary<byte, Play> plays = new();
    private static readonly List<byte> gone = new();

    internal static void Tick() {
        if (loadFailed) return;
        if ((Enabled != null && !Enabled.Value) || MeetingHud.Instance != null || ExileController.Instance != null
            || PlayerControl.AllPlayerControls == null) { StopAll(); return; }
        foreach (var p in PlayerControl.AllPlayerControls) {
            if (p == null) continue;
            if (plays.TryGetValue(p.PlayerId, out var pl)) {
                bool go = false;
                try { go = Step(pl); } catch (Exception e) { Note($"step failed: {e.Message}"); }
                if (!go) Stop(pl);
                continue;
            }
            try { if (ShouldStart(p)) Begin(p); } catch (Exception e) { Note($"start failed: {e.Message}"); }
        }
        if (plays.Count == 0) return;
        gone.Clear();
        foreach (var kv in plays) if (kv.Value.P == null) gone.Add(kv.Key);
        foreach (var id in gone) if (plays.TryGetValue(id, out var pl)) Stop(pl);
    }

    private static bool ShouldStart(PlayerControl p) {
        if (p.Data == null || p.Data.IsDead || p.inVent || !p.petting) return false;
        var co = p.cosmetics;
        var pet = co != null ? co.currentPet : null;
        if (pet == null || !pet.beingPet || co.CurrentBodyType != PlayerBodyTypes.Normal) return false;
        try { if (p.MyPhysics != null && p.MyPhysics.body != null && p.MyPhysics.body.velocity.sqrMagnitude > 0.01f) return false; } catch { }
        return Load();
    }

    private static void Begin(PlayerControl p) {
        var co = p.cosmetics;
        var body = co.currentBodySprite != null ? co.currentBodySprite.BodySprite : null;
        if (body == null || body.sprite == null || body.transform.localScale == Vector3.zero) return;   // somebody else hides the body
        Transform hatT = null;
        try { if (co.hat != null) hatT = co.hat.transform; } catch { }
        if (hatT == null || hatT.parent == null) return;                       // the pictures are placed like a hat
        MakeSprites();
        var pl = new Play { P = p, Start = Time.time, StartPos = p.GetTruePosition(), BodySrc = body };
        pl.BodyT = body.transform; pl.BodyScale = pl.BodyT.localScale;
        try { if (co.skin != null) { pl.SkinT = co.skin.transform; pl.SkinScale = pl.SkinT.localScale; } } catch { }
        try { if (co.visor != null) { pl.VisorT = co.visor.transform; pl.VisorScale = pl.VisorT.localScale; } } catch { }
        pl.HatT = hatT; pl.HatPos = hatT.localPosition;

        // at the hat's own spot, which stays put: the hat itself moves with the head below
        pl.Root = new GameObject("UTSPetCuddle") { layer = body.gameObject.layer };
        pl.Root.transform.SetParent(hatT.parent, false);
        pl.Body = Layer(pl, "body", body, 0);
        pl.Hands = Layer(pl, "hands", body, 2);
        int colorId = 0;
        try { colorId = p.CurrentOutfit.ColorId; } catch { try { colorId = p.Data.DefaultOutfit.ColorId; } catch { } }
        PlayerMaterial.SetColors(colorId, pl.Body);
        PlayerMaterial.SetColors(colorId, pl.Hands);

        // the pet as a copy of its current picture (its colour mask too, UTSPets)
        var pet = co.currentPet;
        var r0 = pet.renderers != null && pet.renderers.Length > 0 ? pet.renderers[0] : null;
        if (r0 != null) {
            pl.PetGo = new GameObject("UTSPetCuddlePet") { layer = r0.gameObject.layer };
            pl.Pet = pl.PetGo.AddComponent<SpriteRenderer>();
            pl.Pet.sharedMaterial = r0.material;
            pl.Pet.sortingLayerID = body.sortingLayerID;
            pl.Pet.sortingOrder = body.sortingOrder + 1;
            var acc = r0.transform.Find("uts_pet_accent");
            var accSr = acc != null ? acc.GetComponent<SpriteRenderer>() : null;
            if (accSr != null) {
                var ag = new GameObject("accent") { layer = r0.gameObject.layer };
                ag.transform.SetParent(pl.PetGo.transform, false);
                ag.transform.localPosition = new Vector3(0f, 0f, -0.0002f);
                pl.PetAccent = ag.AddComponent<SpriteRenderer>();
                pl.PetAccent.sharedMaterial = accSr.sharedMaterial;
                pl.PetAccent.sortingLayerID = body.sortingLayerID;
                pl.PetAccent.sortingOrder = body.sortingOrder + 1;
            }
            foreach (var r in pet.renderers) if (r != null && r.enabled) pl.PetHidden.Add(r);
            if (pet.shadows != null) foreach (var r in pet.shadows) if (r != null && r.enabled) pl.PetHidden.Add(r);
            if (accSr != null && accSr.enabled) pl.PetHidden.Add(accSr);
        }
        try {
            var hand = co.PettingHand;
            if (hand != null) foreach (var r in hand.GetComponentsInChildren<SpriteRenderer>(true)) if (r != null) pl.HandHidden.Add(r);
        } catch { }
        plays[p.PlayerId] = pl;
        Step(pl);
    }

    private static SpriteRenderer Layer(Play pl, string name, SpriteRenderer body, int order) {
        var go = new GameObject(name) { layer = body.gameObject.layer };
        go.transform.SetParent(pl.Root.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sharedMaterial = body.sharedMaterial;
        sr.sortingLayerID = body.sortingLayerID;
        sr.sortingOrder = body.sortingOrder + order;
        sr.maskInteraction = body.maskInteraction;
        return sr;
    }

    private static bool Step(Play pl) {
        var p = pl.P;
        if (p == null || p.Data == null || p.Data.IsDead || p.inVent || pl.BodySrc == null) return false;
        int idx = (int)((Time.time - pl.Start) * fps);
        if (idx >= sequence.Length) return false;
        if (Vector2.Distance(p.GetTruePosition(), pl.StartPos) > 0.12f) return false;        // walked off: end at once
        int f = sequence[idx];

        // the body stays hidden (the game or other mods may set the scale back)
        if (pl.BodyT != null) pl.BodyT.localScale = Vector3.zero;
        if (pl.SkinT != null) pl.SkinT.localScale = Vector3.zero;
        if (pl.VisorT != null) pl.VisorT.localScale = Vector3.zero;
        foreach (var r in pl.HandHidden) if (r != null && r.enabled) r.enabled = false;

        var rt = pl.Root.transform;
        rt.localPosition = pl.HatPos + new Vector3(0f, 0f, 0.0005f);           // a hair behind the hat
        rt.localRotation = pl.HatT.localRotation;
        rt.localScale = pl.HatT.localScale;
        bool flip = pl.BodySrc.flipX;
        pl.Body.sprite = bodySprites[f]; pl.Body.flipX = flip;
        pl.Hands.sprite = handSprites[f]; pl.Hands.flipX = flip;

        // hat follows the head
        if (pl.HatT != null && f < head.Length) {
            var world = rt.TransformVector(Local(head[f] + spritePivot, flip) - Local(spritePivot, flip));
            pl.HatT.localPosition = pl.HatPos + (pl.HatT.parent != null ? pl.HatT.parent.InverseTransformVector(world) : world);
        }

        // the pet: on the ground until it is picked up, then between the hands
        var pet = p.cosmetics != null ? p.cosmetics.currentPet : null;
        var r0 = pet != null && pet.renderers != null && pet.renderers.Length > 0 ? pet.renderers[0] : null;
        if (pl.Pet != null && r0 != null) {
            foreach (var r in pl.PetHidden) if (r != null && r.enabled) r.enabled = false;
            pl.Pet.sprite = r0.sprite;
            pl.Pet.flipX = r0.flipX;
            pl.Pet.color = r0.color;
            if (pl.PetAccent != null) {
                var acc = r0.transform.Find("uts_pet_accent");
                var accSr = acc != null ? acc.GetComponent<SpriteRenderer>() : null;
                if (accSr != null) { pl.PetAccent.sprite = accSr.sprite; pl.PetAccent.flipX = accSr.flipX; pl.PetAccent.color = accSr.color; }
            }
            bool held = f > pickup && f < putdown && f < hands.Length && hands[f].HasValue;
            Vector3 pos;
            float size = 1f;
            if (held) {
                var bounds = r0.bounds;
                float beanH = BeanShare * cellH / spritePpu * Mathf.Abs(rt.lossyScale.y);
                if (bounds.size.y > 0.0001f) size = Mathf.Min(1f, PetHeldShare * beanH / bounds.size.y);
                var centre = (bounds.center - r0.transform.position) * size;
                var at = rt.TransformPoint(Local(hands[f].Value, flip));
                pos = at - centre + new Vector3(0f, bounds.size.y * size * PetAboveHands, 0f);
                pos.z = rt.position.z - 0.001f;
            } else pos = r0.transform.position;
            if (held != pl.Held) {
                pl.Held = held;
                pl.HeldSince = Time.time;
                pl.FromPos = pl.PetGo.transform.position;
                pl.FromSize = pl.Size;
            }
            if (pl.HeldSince >= 0f) {
                float t = Mathf.Clamp01((Time.time - pl.HeldSince) / BlendSeconds);
                t = t * t * (3f - 2f * t);
                pos = Vector3.Lerp(pl.FromPos, pos, t);
                size = Mathf.Lerp(pl.FromSize, size, t);
            }
            pl.Size = size;
            pl.PetGo.transform.localScale = r0.transform.lossyScale * size;
            pl.PetGo.transform.position = pos;
        }
        return true;
    }

    private static void Stop(Play pl) {
        plays.Remove(pl.P != null ? pl.P.PlayerId : FindKey(pl));
        try { if (pl.BodyT != null) pl.BodyT.localScale = pl.BodyScale; } catch { }
        try { if (pl.SkinT != null) pl.SkinT.localScale = pl.SkinScale; } catch { }
        try { if (pl.VisorT != null) pl.VisorT.localScale = pl.VisorScale; } catch { }
        try { if (pl.HatT != null) pl.HatT.localPosition = pl.HatPos; } catch { }
        foreach (var r in pl.PetHidden) try { if (r != null) r.enabled = true; } catch { }
        foreach (var r in pl.HandHidden) try { if (r != null) r.enabled = true; } catch { }
        try { if (pl.Root != null) UnityEngine.Object.Destroy(pl.Root); } catch { }
        try { if (pl.PetGo != null) UnityEngine.Object.Destroy(pl.PetGo); } catch { }
    }

    private static byte FindKey(Play pl) {
        foreach (var kv in plays) if (kv.Value == pl) return kv.Key;
        return 255;
    }

    internal static void StopAll() {
        if (plays.Count == 0) return;
        foreach (var pl in new List<Play>(plays.Values)) Stop(pl);
        plays.Clear();
    }

    private static int notes;
    private static void Note(string what) {
        if (notes++ < 5) UsefulTORStuffPlugin.Logger.LogWarning("[UTS/PetCuddle] " + what);
    }

    private static byte[] ReadResource(string name) {
        using var s = typeof(PetCuddle).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        var data = new byte[s.Length];
        int read = 0;
        while (read < data.Length) { int n = s.Read(data, read, data.Length - read); if (n <= 0) break; read += n; }
        return read == data.Length ? data : null;
    }
}
