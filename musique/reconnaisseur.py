# Reconnaissance musicale via shazamio (venv isolé).
# Usage : reconnaisseur.py <fichier.wav> → JSON sur stdout {ok, titre, artiste, album, annee, genre, url}
import asyncio
import json
import sys

from shazamio import Shazam


async def identifier(chemin):
    shazam = Shazam()
    out = await shazam.recognize(chemin)
    track = out.get("track") or {}
    section = None
    for meta in track.get("sections", []):
        if meta.get("type") == "SONG":
            section = meta
            break
    metadata = (section or {}).get("metadata", [])
    champ = {}
    for m in metadata:
        champ[m.get("title", "").lower()] = m.get("text", "")
    return {
        "ok": bool(track),
        "titre": track.get("title") or "",
        "artiste": track.get("subtitle") or "",
        "album": champ.get("album", ""),
        "annee": champ.get("releasedate", ""),
        "genre": (track.get("genres") or {}).get("primary", ""),
        "url": (track.get("hub") or {}).get("actions", [{}])[0].get("uri", "") if track.get("hub") else "",
    }


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(json.dumps({"ok": False, "erreur": "chemin wav manquant"}))
        sys.exit(1)
    try:
        resultat = asyncio.run(identifier(sys.argv[1]))
    except Exception as e:
        resultat = {"ok": False, "erreur": str(e)}
    print(json.dumps(resultat, ensure_ascii=False))
