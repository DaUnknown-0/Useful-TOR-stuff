using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace UsefulTORStuff {
    // Audit 16.11. UTS-3-06/07/08, TOR-038: erst vollständig schreiben, dann atomar ersetzen.
    internal static class AuditFixD_PluginInstall {
        private static readonly ConcurrentDictionary<string, object> locks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Version> prepared = new(StringComparer.OrdinalIgnoreCase);

        internal static Version PreparedVersion(string path, Version fallback) =>
            prepared.TryGetValue(Path.GetFullPath(path), out var v) ? v : fallback;

        internal static Task Install(string path, byte[] bytes, Version version = null) => Task.Run(() => {
            path = Path.GetFullPath(path);
            lock (locks.GetOrAdd(path, _ => new object())) {
                string token = Guid.NewGuid().ToString("N");
                string temp = path + ".uts-" + token + ".part";
                string backup = path + ".uts-" + token + ".old";
                try {
                    if (!UnknownsNet.IsPluginImage(bytes)) throw new InvalidDataException("Download ist keine DLL.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    // Verhindert auch, dass ein beschädigtes verwaltetes PE die laufende DLL ersetzt.
                    var assemblyVersion = System.Reflection.AssemblyName.GetAssemblyName(temp).Version;
                    if (File.Exists(path)) File.Replace(temp, path, backup);
                    else File.Move(temp, path);
                    prepared[path] = version ?? assemblyVersion;
                    ModManagerDelete.CancelPending(path);
                    // Die jüngste Sicherung bleibt erhalten. Fremde .old-Dateien werden nie angefasst.
                    try {
                        foreach (var old in Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".uts-*.old"))
                            if (old != backup) { try { File.Delete(old); } catch { } }
                    } catch { }
                } finally {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
        });
    }
}
