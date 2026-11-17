# Paket-B-Regressionstests

Dieses Harness verknüpft die Produktionsdateien aus Paket B und führt ihre HarmonyX-Patches mit isolierten TOR- und Spiel-Stubs aus. Among Us wird nicht geladen oder gestartet. Zusätzliche Cecil-Prüfungen lesen ausschließlich die echte lokale TOR-DLL und bestätigen die Transpiler-Anker.

Im Worktree ausführen:

```powershell
dotnet publish tools/AuditFixB.Tests/AuditFixB.Tests.csproj -c Release -r win-x86 --self-contained true -p:AmongUsLatest=
$env:DOTNET_TieredCompilation='0'
& './tools/AuditFixB.Tests/bin/Release/net6.0/win-x86/publish/AuditFixB.Tests.exe'
```

Die eigenständige .NET-6-Testanwendung nutzt das lokal vorhandene win-x86-Runtimepaket. Sie installiert keine Runtime und kopiert nichts in den Spielordner. Das eigentliche Plugin bleibt AnyCPU. HarmonyX 2.10.2 benötigt für dieses Harness .NET 6, der Versuch mit der systemweit vorhandenen .NET-8-Runtime war nicht kompatibel. Tiering ist nur für den Testprozess abgeschaltet. Il2Cpp, Server, Paketverlust und tatsächlich geladene Detours müssen anschließend mit DuoTest geprüft werden.

Geprüft werden Codecgrenzen, alte Clipboard-Bytes, beschädigte und doppelte Datensätze, bekannte und unbekannte IDs, Importspeicherung, Vanilla-Validierung, Presetschlüssel-Migration, Besitzertrennung einschließlich globaler Aliase und Queue-Kopien, TOR-Empfänger mit Abbruch beim ersten unbekannten Eintrag, Livewerte beim Versand, Anzahl-/Bytebudget, Paketabstände, Join-Wellen, Lobby-/Hostwechsel, Options-Gate, Fungle/Submerged sowie verzögerte, abgelehnte und abgebrochene Starts.
