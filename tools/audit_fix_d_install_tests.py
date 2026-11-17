"""Audit 16.11. Paket D: echte Installations-/Queue-Quellen in einem isolierten Dateisystem testen.

Aufruf: python tools/audit_fix_d_install_tests.py
Kein Spielstart, kein Deploy. BepInEx und reine Anzeige-Gegenstellen sind kleine Test-Doubles.
"""
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]

STUBS = r'''
using System;
using System.Collections.Generic;
using System.IO;
namespace BepInEx {
    public static class Paths {
        public static string BepInExRootPath, PluginPath, ConfigPath;
    }
    public class BepInDependency {
        public enum DependencyFlags { HardDependency = 1 }
        public string DependencyGUID;
        public DependencyFlags Flags;
    }
}
namespace BepInEx.Unity.IL2CPP {
    public class IL2CPPChainloader {
        public static readonly IL2CPPChainloader Instance = new();
        public Dictionary<string, PluginInfo> Plugins = new();
    }
    public class PluginInfo {
        public string Location;
        public BepInEx.BepInDependency[] Dependencies;
        public Metadata Metadata;
    }
    public class Metadata { public string Name; }
}
namespace UsefulTORStuff {
    public class ModInfo { public string Guid, Name; public string[] DeletePaths; }
    public static class UsefulTORStuffPlugin {
        public const string PluginGuid = "test.uts";
        public static readonly TestLogger Logger = new();
    }
    public class TestLogger {
        public void LogInfo(string text) { }
        public void LogWarning(string text) { }
        public void LogError(string text) { }
    }
    public static class FastStartPlugin { public const string Guid = "test.faststart"; }
    public static class UTSRestart { public static void Mark() { } }
    public class CatalogEntry { public bool External; public string TargetPath; }
    public static class UTSModCatalog { public static CatalogEntry ByGuid(string guid) => null; }
    public static class UnknownsNet {
        public static bool IsPluginImage(byte[] bytes) => bytes != null && bytes.Length > 2 && bytes[0] == 'M' && bytes[1] == 'Z';
    }
}
'''

PROGRAM = r'''
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UsefulTORStuff;
using BepInEx;

class Program {
    static int checks;
    static void Check(bool condition, string name) {
        if (!condition) throw new Exception("FEHLER: " + name);
        checks++;
        Console.WriteLine("OK: " + name);
    }
    static async Task Fails(Task task) {
        try { await task; } catch { return; }
        throw new Exception("Ein fehlgeschlagener Schreibvorgang wurde als Erfolg gemeldet.");
    }
    static async Task Main(string[] args) {
        Paths.BepInExRootPath = Path.Combine(args[0], "BepInEx");
        Paths.PluginPath = Path.Combine(Paths.BepInExRootPath, "plugins");
        Paths.ConfigPath = Path.Combine(Paths.BepInExRootPath, "config");
        Directory.CreateDirectory(Paths.PluginPath);
        Directory.CreateDirectory(Paths.ConfigPath);
        string target = Path.Combine(Paths.PluginPath, "Renamed.dll");
        byte[] oldBytes = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);
        byte[] newBytes = oldBytes.Concat(new byte[] { 10, 20, 30 }).ToArray();
        File.WriteAllBytes(target, oldBytes);
        File.WriteAllText(target + ".old", "fremde Sicherung");
        string queue = Path.Combine(Paths.ConfigPath, "UTSPendingDelete.txt");
        string config = Path.Combine(Paths.ConfigPath, "reinstalled.cfg");
        string other = Path.Combine(Paths.ConfigPath, "other.cfg");
        long stamp = DateTime.UtcNow.AddMinutes(-10).Ticks;
        File.WriteAllLines(queue, new[] { config + "\t" + target + "\t" + stamp, other + "\tother.dll\t" + stamp });
        await AuditFixD_PluginInstall.Install(target, newBytes, new Version(2, 0));
        Check(File.ReadAllBytes(target).SequenceEqual(newBytes), "vollständige DLL atomar ersetzt");
        string[] backups = Directory.GetFiles(Paths.PluginPath, "Renamed.dll.uts-*.old");
        Check(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(oldBytes), "bisherige DLL als eigene Rückfallsicherung erhalten");
        Check(File.ReadAllText(target + ".old") == "fremde Sicherung", "fremde .old-Datei nicht gelöscht");
        Check(File.ReadAllLines(queue).Length == 1 && File.ReadAllText(queue).StartsWith(other), "Neuinstallation entfernt nur eigene Löschaufträge");

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => AuditFixD_PluginInstall.Install(target, i % 2 == 0 ? oldBytes : newBytes, new Version(3, i))));
        byte[] final = File.ReadAllBytes(target);
        Check(final.SequenceEqual(oldBytes) || final.SequenceEqual(newBytes), "16 parallele Installationen hinterlassen eine vollständige DLL");
        Check(Directory.GetFiles(Paths.PluginPath, "Renamed.dll.uts-*.old").Length == 1, "genau die jüngste Rückfallsicherung bleibt");
        Check(Directory.GetFiles(Paths.PluginPath, "*.part").Length == 0, "keine temporären DLL-Reste nach Erfolg");

        using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            await Fails(AuditFixD_PluginInstall.Install(target, newBytes));
            Check(File.ReadAllBytes(target).SequenceEqual(final), "gesperrtes Ziel behält seine vollständige DLL");
        }
        Check(Directory.GetFiles(Paths.PluginPath, "Renamed.dll.uts-*.old").Length == 1, "Schreibfehler verliert bestehende Sicherung nicht");
        await Fails(AuditFixD_PluginInstall.Install(target, new byte[] { (byte)'M', (byte)'Z', 1, 2 }));
        Check(File.ReadAllBytes(target).SequenceEqual(final), "beschädigtes PE ersetzt keine Installation");
        Check(Directory.GetFiles(Paths.PluginPath, "*.part").Length == 0, "temporäre Dateien auch nach Fehlern beseitigt");
        await AuditFixD_PluginInstall.Install(target, newBytes, new Version(9, 1));
        Check(AuditFixD_PluginInstall.PreparedVersion(target, new Version(1, 0)) == new Version(9, 1), "vorbereitete Version ersetzt laufendes Inventar beim Vergleich");
        string fresh = Path.Combine(Paths.PluginPath, "Fresh.dll");
        await AuditFixD_PluginInstall.Install(fresh, oldBytes);
        Check(File.ReadAllBytes(fresh).SequenceEqual(oldBytes), "Erstinstallation ohne vorhandene DLL");

        string ownerA = Path.Combine(Paths.PluginPath, "A.dll");
        string ownerB = Path.Combine(Paths.PluginPath, "B.dll");
        File.WriteAllText(ownerA, "A"); File.WriteAllText(ownerB, "B");
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(ownerA, now.AddMinutes(-5));
        File.SetLastWriteTimeUtc(ownerB, now.AddMinutes(-20));
        File.WriteAllText(config, "neue Konfiguration"); File.WriteAllText(other, "alte Konfiguration");
        File.WriteAllLines(queue, new[] { config + "\t" + ownerA + "\t" + now.AddMinutes(-10).Ticks, other + "\t" + ownerB + "\t" + now.Ticks });
        File.SetLastWriteTimeUtc(queue, now.AddMinutes(1));
        ModManagerDelete.ProcessPending();
        Check(File.Exists(config) && !File.Exists(other), "Zeit je Eintrag schützt Neuinstallation trotz später ergänzter Queue");

        File.WriteAllText(other, "gesperrte Konfiguration");
        long queued = now.Ticks;
        File.WriteAllText(queue, other + "\t" + ownerB + "\t" + queued);
        using (var held = new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            ModManagerDelete.ProcessPending();
            string[] row = File.ReadAllText(queue).Trim().Split('\t');
            Check(row.Length == 3 && row[1] == ownerB && row[2] == queued.ToString(), "Wiederholung bewahrt Besitzer und ursprünglichen Zeitstempel");
        }
        ModManagerDelete.ProcessPending();
        Check(!File.Exists(other) && !File.Exists(queue), "später entsperrter Rest wird beim nächsten Versuch gelöscht");
        Console.WriteLine($"{checks} Prüfungen erfolgreich.");
    }
}
'''

with tempfile.TemporaryDirectory(prefix="uts_audit_D_") as directory:
    tmp = Path(directory).resolve()
    # TemporaryDirectory besitzt nur dieses frisch erzeugte Verzeichnis und räumt es danach auf.
    assert tmp.parent == Path(tempfile.gettempdir()).resolve()
    sources = [ROOT / "AuditFixD_PluginInstall.cs", ROOT / "ModManagerDelete.cs"]
    links = "".join(f'<Compile Include="{escape(str(source))}" />' for source in sources)
    (tmp / "Tests.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net8.0</TargetFramework><LangVersion>latest</LangVersion>'
        '<CheckEolTargetFramework>false</CheckEolTargetFramework></PropertyGroup>'
        f'<ItemGroup>{links}</ItemGroup></Project>', encoding="utf-8")
    (tmp / "Stubs.cs").write_text(STUBS, encoding="utf-8")
    (tmp / "Program.cs").write_text(PROGRAM, encoding="utf-8")
    result = subprocess.run(["dotnet", "run", "--project", str(tmp / "Tests.csproj"), "-c", "Release", "--", str(tmp)], check=False)
    if result.returncode:
        raise SystemExit(result.returncode)
