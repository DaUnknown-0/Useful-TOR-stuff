// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace UsefulTORStuff;

/// <summary>
/// Ein Kostümpaket aus UTS-Music/costumes: zuerst aus dem Cache (Größe + SHA-256), sonst im Hintergrund geladen.
/// Das Ergebnis wird im Hauptthread über <see cref="Poll"/> abgeholt, weil Texturen nur dort entstehen dürfen.
/// </summary>
internal sealed class CostumePackage {
    private const string AssetUrl = "https://raw.githubusercontent.com/DaUnknown-0/UTS-Music/main/costumes/";

    private readonly string label, file, hash;
    private readonly int size;
    private readonly Action<byte[]> ready;
    private Task<byte[]> download;

    internal CostumePackage(string label, string file, int size, string hash, Action<byte[]> ready) {
        this.label = label;
        this.file = file;
        this.size = size;
        this.hash = hash;
        this.ready = ready;
    }

    private string CacheFile => Path.Combine(BepInEx.Paths.BepInExRootPath, "uts-costumes", file);

    private void Warn(string message) => UsefulTORStuffPlugin.Logger?.LogWarning($"[{label}] {message}");

    internal void Load() {
        try {
            if (File.Exists(CacheFile) && new FileInfo(CacheFile).Length == size) {
                byte[] cached = File.ReadAllBytes(CacheFile);
                if (Verify(cached)) {
                    Deliver(cached);
                    return;
                }
            }
        } catch (Exception ex) {
            Warn("Der Kostümcache konnte nicht geladen werden. " + ex.Message);
        }
        download = Task.Run(Fetch);
    }

    internal void Poll() {
        if (download == null || !download.IsCompleted) return;
        var complete = download;
        download = null;
        try { Deliver(complete.GetAwaiter().GetResult()); }
        catch (Exception ex) { Warn("Das Kostüm konnte nicht geladen werden. " + ex.Message); }
    }

    private void Deliver(byte[] data) {
        try { ready(data); }
        catch (Exception ex) { Warn("Das Kostümpaket ist unvollständig. " + ex.Message); }
    }

    private bool Verify(byte[] bytes) => bytes.Length == size &&
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase);

    private async Task<byte[]> Fetch() {
        string source = AssetUrl;
        string local = Environment.GetEnvironmentVariable("UTS_COSTUME_SOURCE");
        if (Uri.TryCreate(local, UriKind.Absolute, out var loopback) && loopback.Scheme == "http" &&
            loopback.Host == "127.0.0.1" && local.EndsWith("/", StringComparison.Ordinal))
            source = local;

        using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = new HttpClient();
        using var response = await http.GetAsync(source + file, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var data = new byte[size];
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

    internal static byte[] ReadEntry(ZipArchive archive, string name) {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException(name);
        using var stream = entry.Open();
        using var bytes = new MemoryStream((int)entry.Length);
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
