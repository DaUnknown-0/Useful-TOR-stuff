HostGuard: Argon2id-Passwortprüfwerte

Die Passwortprüfung in UsefulTORStuff und im eigenständigen ChanceMod-Gate unterstützt jetzt Argon2id im PHC-Format. Neue Prüfwerte verwenden einen kryptografisch zufälligen Salt von 16 Byte, 19 MiB Speicher, zwei Durchläufe und eine Lane. Der Salt und die Parameter sind öffentlich und gehören zum Prüfwert; sie sind kein zusätzliches Passwort.

Erzeugen eines neuen Prüfwerts:

```powershell
.\set_password.ps1
```

Das Skript startet ein lokales Konsolenwerkzeug. Das Passwort zweimal verdeckt eingeben. Es wird nicht aus einer .env-Datei gelesen und weder Passwort noch Prüfwert werden auf der Konsole ausgegeben. Das Werkzeug schreibt nur den Prüfwert nach password_hash.txt und lädt nichts hoch. Beim ersten Bauen benötigt es .NET 8 und Zugriff auf die angegebenen NuGet-Abhängigkeiten.

Vor der Umstellung des veröffentlichten Prüfwerts alle verwendeten Gate-Clients aktualisieren. Alte Clients erwarten ausschließlich 64 Hexzeichen und verstehen das neue PHC-Format nicht. Die neuen Clients akzeptieren alte SHA-256-Prüfwerte weiterhin als Übergang. Deshalb stellt allein der neue Mod-Build das aktive Passwort noch nicht auf Argon2id um. Erst den neuen Prüfwert lokal erzeugen, dann in einem separat freigegebenen Veröffentlichungsschritt bereitstellen. Ein vorhandener SHA-256-Prüfwert kann ohne erneute Eingabe beziehungsweise Neufestlegung des Passworts nicht in einen normalen Argon2id-Prüfwert desselben Passworts umgewandelt werden.

Die Prüfung läuft außerhalb des Unity-Hauptthreads. Je Gate kann nur eine Passwortprüfung gleichzeitig laufen. Ein Ergebnis für einen inzwischen geänderten Prüfwert schaltet das Gate nicht frei. Ungültige Formate sowie überhöhte Speicher-, Durchlauf- und Parallelitätsanforderungen werden abgewiesen. Vergleiche der abgeleiteten Bytes erfolgen zeitkonstant; die temporären Passwort-Bytepuffer werden anschließend geleert. Das ist keine Garantie, dass verwaltete Zeichenketten im Arbeitsspeicher sofort verschwinden.

Die SHA-256-Prüfung der berechtigten Kennungen bleibt unverändert. Ein Pepper ist nicht in den Client eingebaut: Ein mit ausgelieferter Schlüssel wäre dort auslesbar. Lokale Gate-Logik kann von einem veränderten Client weiterhin umgangen werden; dies ersetzt keine serverseitige Authentifizierung.

Argon2 und Blake2 werden mit ihren MIT-Lizenzhinweisen in die jeweilige Mod-DLL eingebunden. Keine zusätzliche Kryptografie-DLL muss ins Spiel kopiert werden. Laufzeit und Web-Bedienung im Spiel müssen nach einer Installation noch geprüft werden.

Grundlage: [OWASP Password Storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html). Die Bibliothek wurde zusätzlich gegen den Argon2id-Testvektor aus [RFC 9106, Abschnitt 5.3](https://www.rfc-editor.org/rfc/rfc9106.html#section-5.3) geprüft.
