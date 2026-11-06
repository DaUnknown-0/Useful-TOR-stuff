"""Ein Online-Haustier fuer Forgotten Fixes veroeffentlichen (UTSPets, 06.11.2026): Sprite-Blatt ins Repo UTS-Music, Liste anpassen.

Schreibt das Blatt als pets/pet_<key>_<sha8>.png nach ../UTS-Music/pets und traegt das Haustier in pets/pets.json ein (Format 1).
Altes Blatt desselben Haustiers wird entfernt. Danach UTS-Music committen und auf main pushen: jeder Spieler bekommt es beim
naechsten Lobby-Beitritt (oder sofort per /loadpets des Autors), ohne Mod-Update. Gleicher Schluessel wie ein eingebautes
Haustier (z. B. "bit") = dessen Bilder werden live ersetzt.

Blatt: Zellen gleicher Groesse, zeilenweise von links oben gezaehlt, Bit-Vorlage: Assets/Branding/bit_pet/make_sheet.py
(Drehpunkt jeder Zelle = Mitte unten = Boden unter dem Haustier; Schwebehoehe als leerer Rand unten).

Sperre: --lock nimmt Freundescodes (name#1234) oder PUIDs und speichert nur deren SHA-256; --lock-hash nimmt fertige Hashes.
Ohne Sperre darf jeder das Haustier tragen. --owner sperrt auf den Mod-Autor (dieselben Hashes wie EasterEggCoding).

Aufruf: python tools/build_online_pet.py <key> "<Name>" <blatt.png> --cell 74 176 [--cols 6] [--idle 0-11] [--walk 12-23]
        [--fps 12] [--pet 13-20] [--pet-fps 10] [--height 0.72] [--flip] [--drop 0.2] [--accent N] [--carrier pet_UFO] [--lock CODE ...] [--lock-hash HASH ...] [--owner]
        [--author NAME] [--min-version 1.6.3] [--repo PFAD]
        python tools/build_online_pet.py --remove <key>
"""
import argparse
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OWNER = ["d10f0e308524211c45d93ec3300efca28e35488ea35598196c67b1ababb7fc19",
         "cf2eab42fba217e7af4237f7a3bcd35402f2790602e07d92c6344096701480c6"]


def frames(spec):
    out = []
    for part in spec.split(","):
        part = part.strip()
        if "-" in part:
            a, b = part.split("-")
            out += list(range(int(a), int(b) + 1))
        elif part:
            out.append(int(part))
    return out


def load(d):
    f = d / "pets.json"
    return json.loads(f.read_text(encoding="utf-8")) if f.exists() else {"format": 1, "pets": []}


def save(d, data):
    (d / "pets.json").write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("key", nargs="?")
    ap.add_argument("name", nargs="?")
    ap.add_argument("sheet", nargs="?")
    ap.add_argument("--cell", nargs=2, type=int)
    ap.add_argument("--cols", type=int, default=6)
    ap.add_argument("--idle", default="0-11")
    ap.add_argument("--walk")
    ap.add_argument("--fps", type=float, default=12)
    ap.add_argument("--pet", help="Bilder beim Streicheln (ab Forgotten Fixes 1.7.4, aeltere ignorieren es)")
    ap.add_argument("--pet-fps", type=float, default=0)
    ap.add_argument("--height", type=float, default=0.72)
    ap.add_argument("--flip", action="store_true")
    ap.add_argument("--drop", type=float, default=0.0)
    ap.add_argument("--accent", type=int, default=0)
    ap.add_argument("--carrier")
    ap.add_argument("--lock", nargs="*", default=[])
    ap.add_argument("--lock-hash", nargs="*", default=[])
    ap.add_argument("--owner", action="store_true")
    ap.add_argument("--author", default="DaUnknown-0")
    ap.add_argument("--min-version")
    ap.add_argument("--remove")
    ap.add_argument("--repo", default=str(ROOT.parent / "UTS-Music"))
    a = ap.parse_args()

    d = Path(a.repo) / "pets"
    d.mkdir(parents=True, exist_ok=True)
    data = load(d)
    if a.remove:
        data["pets"] = [p for p in data["pets"] if p["key"] != a.remove]
        for f in d.glob(f"pet_{a.remove}_*.png"):
            f.unlink()
        save(d, data)
        print(f"{a.remove} entfernt")
        return
    if not (a.key and a.name and a.sheet and a.cell):
        ap.error("key, name, sheet und --cell sind noetig")
    if not re.fullmatch(r"[a-z][a-z0-9]{1,23}", a.key):
        ap.error("key: Kleinbuchstaben/Ziffern, 2-24 Zeichen, beginnt mit Buchstabe")
    raw = Path(a.sheet).read_bytes()
    h = hashlib.sha256(raw).hexdigest()
    for f in d.glob(f"pet_{a.key}_*.png"):
        f.unlink()
    name = f"pet_{a.key}_{h[:8]}.png"
    (d / name).write_bytes(raw)
    locks = [hashlib.sha256(x.strip().encode("utf-8")).hexdigest() for x in a.lock if x.strip()]
    locks += [x.strip().lower() for x in a.lock_hash if re.fullmatch(r"[0-9a-fA-F]{64}", x.strip())]
    if a.owner:
        locks += OWNER
    entry = {"key": a.key, "name": a.name, "author": a.author,
             "sheet": {"file": name, "size": len(raw), "sha256": h},
             "cell": a.cell, "cols": a.cols, "idle": frames(a.idle), "walk": frames(a.walk or a.idle),
             "fps": a.fps, "height": a.height}
    if a.pet:
        entry["pet"] = frames(a.pet)
        if a.pet_fps:
            entry["petFps"] = a.pet_fps
    if a.flip:
        entry["flip"] = True
    if a.drop:
        entry["drop"] = a.drop
    if a.accent:
        entry["accent"] = a.accent
    if a.carrier:
        entry["carrier"] = a.carrier
    if locks:
        entry["lockedTo"] = sorted(set(locks))
    if a.min_version:
        entry["minVersion"] = a.min_version
    data["pets"] = [p for p in data["pets"] if p["key"] != a.key] + [entry]
    save(d, data)
    print(f"{a.key}: {name} ({len(raw)} B), {len(set(locks))} Sperre(n) -> {d / 'pets.json'}")


if __name__ == "__main__":
    main()
