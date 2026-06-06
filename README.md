# PocketReader

### Pocket is gone. Your reading list shouldn't be.

**PocketReader is a portable Windows app that turns your Raindrop.io bookmarks into a fast, private, fully-offline reading library — one you actually own.** Save articles in Raindrop like always. PocketReader pulls them down, archives the full text on your disk, and gives you a calm place to read, search, highlight, and revisit them — online or off, with no subscription and no server.

---

## Why this exists

For a decade, "read it later" meant **Pocket**. Save now, read later, trust it'll be there. Then Mozilla shut Pocket down for good — and that reliable little home for the things you meant to read vanished with it.

**Raindrop.io** is the natural place to save now: unlimited bookmarks, free. But saving is only half the job, and the reading half has walls:

- 🔒 **The reading features cost extra.** Full-text search, *forever copies* of your articles, and annotations are all locked behind Raindrop **Pro**.
- 🌐 **It's online-only.** Raindrop opens your saves in the browser, on the live web. No connection — or a dead link, or a new paywall — and the article you saved is simply gone.

PocketReader fills that gap. Point it at your Raindrop account once, and it **fetches and extracts every article's full text and images, and stores them locally.** From that moment your library is yours: it reads beautifully offline, it's searchable to the last word, and it outlives link-rot, paywalls, and disappearing sites.

> **Raindrop is where you save. PocketReader is where you read, keep, and come back.**

---

## What makes it different

**📦 Portable — no server, no cloud, no setup.**
Other self-hosted readers want Docker and a server you babysit. PocketReader is one self-contained folder. Install it to any drive — even a USB stick — and carry your whole library with you.

**🔒 One-way and private by design.**
It only ever *reads* from Raindrop and never writes back. Your ratings, notes, and highlights stay on your machine. No telemetry. No external AI. Even tag suggestions are computed locally.

**📴 Offline-first, and genuinely fast.**
Clean, readable articles with images baked in, opening instantly with no connection. Full-text search runs across tens of thousands of articles in milliseconds.

**🪶 A real native app, not a browser tab.**
Built with WinUI 3 — Mica material, light/dark, a proper distraction-free reading window.

---

## Raindrop free + PocketReader

| Locked behind Raindrop **Pro** | In PocketReader (local, free) |
|---|---|
| Full-text search | ✅ Built-in, **offline**, across your cached articles |
| "Forever copies" of articles | ✅ Your own local copies — full text **and** images |
| Annotations | ✅ Per-article notes **and** in-text highlights |
| Suggested tags | ✅ Smart tag suggestions + auto-tag, computed locally |

**…and things Raindrop doesn't do at all:**
🔊 Offline text-to-speech · 📄 Export to PDF · 📈 Reading statistics · ⭐ Star ratings · ⏯ Reading progress & resume · 🗑 Recycle Bin

---

## Features

**Read**
- Distraction-free reader — Light / Sepia / Dark, adjustable text size
- Reading-progress bar and **resume where you left off**
- **Listen** — built-in offline text-to-speech
- **Export to PDF** — save any article exactly as it reads

**Organize**
- Tags with a drag-and-drop Tag Manager and local tag suggestions
- 1–5 star **ratings**, per-article **notes**, and **in-text highlights**
- Browse by Favorites, Unread, Notes, Highlights, Ratings, or any tag
- Card or list view; sort and density to taste

**Keep & review**
- Lightning-fast **full-text search** across your offline library
- **Recycle Bin** — delete safely, restore anytime; deleted items never re-sync back
- **Statistics** — articles read, words read, time spent, day streaks, 30-day activity

**Manage**
- One-click sync (incremental or full), batch tagging, batch offline caching
- JSON export/import of your entire library

---

## How it works

1. **Install** — run the setup, pick any folder or drive (no admin needed).
2. **Connect Raindrop** — the simplest way is **Sign in with a token** (Raindrop → Settings → Integrations → create a test token). Browser OAuth is also supported if you supply your own Raindrop app credentials.
3. **Sync** — pull your bookmarks in.
4. **Download offline** — cache the full articles, then read anywhere, anytime.

---

## Requirements
- Windows 10/11 (64-bit)
- A free [Raindrop.io](https://raindrop.io) account
- That's it — no server, no subscription, no second device required.

---

## Privacy
PocketReader talks to two places only: **Raindrop**, to read your bookmarks, and the **websites of the articles you saved**, to fetch their text. Nothing else leaves your computer. Your reading, tags, ratings, notes, and highlights are stored locally and never sent anywhere.

---

## Build from source
Requires the .NET 8 SDK and the Windows App SDK workload.

```
dotnet build -c Release -p:Platform=x64
```

Browser OAuth credentials are intentionally **not** included in this repository. To enable one‑click browser sign‑in, create your own app at Raindrop → Settings → Integrations (redirect URI `http://localhost:8080/callback`) and set the `RaindropClientId` / `RaindropClientSecret` constants in `MainWindow.xaml.cs`. Otherwise, use **Sign in with a token** — it needs no app credentials.

## License
Released under the [MIT License](LICENSE).

---

*PocketReader isn't a Raindrop replacement — it's the reading half Raindrop leaves out, and the Pocket-shaped hole it left behind.*
