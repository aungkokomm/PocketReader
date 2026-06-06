# PocketReader — User Guide

Welcome! This guide gets you from zero to reading offline in a few minutes, then shows you everything PocketReader can do. No technical knowledge needed.

---

## 1. Install

1. Download **`PocketReader-Setup-1.8.2.exe`** from the [Releases page](https://github.com/aungkokomm/PocketReader/releases).
2. Run it. You don't need administrator rights.
3. Choose where to install — any folder or drive works (even a USB stick). The app and all your data live together in that one folder, so you can move it anywhere later.
4. Launch PocketReader.

---

## 2. Connect your Raindrop account

PocketReader reads the bookmarks from your free [Raindrop.io](https://raindrop.io) account. You connect once, using a **token** (a long password‑like code Raindrop gives you). It takes about a minute.

1. In PocketReader, click **Sign in** (top‑right) → **Sign in with a token…**
2. A window opens with the steps. Click the **link** it shows to open Raindrop's *Integrations* page in your browser, and sign in to Raindrop if asked.
3. On that page, click **+ Create new app**, give it any name (e.g. "PocketReader"), then click the app you just made to open it.
4. Scroll down to **For Developers** and click **Create test token** (confirm if it asks).
5. **Copy** the token it shows, return to PocketReader, **paste** it into the box, and click **Sign in**.

That's it — you're connected. (Your token is stored locally on your computer and never shared.)

> **Tip:** This token only lets PocketReader *read* your bookmarks. PocketReader never changes anything in your Raindrop account.

---

## 3. Sync your bookmarks

Click **Sync** (top bar) → **Sync new bookmarks**.

- The first sync pulls in your whole library. A big library can take a little while — let it finish.
- After that, **Sync new bookmarks** is instant: it only fetches what changed since last time.
- Your articles now appear as a list (or cards — see below).

---

## 4. Download for offline reading

Syncing brings in the *titles and links*. To read **offline** — and to search the full text — download the articles:

1. Pick a view (e.g. **All**, or a tag).
2. Click **Download** (top bar). PocketReader caches a batch of articles (full text + images) to your disk, showing progress at the bottom. You can **Pause** or **Stop** anytime.
3. Click **Download** again for the next batch until the view is done.

Cached articles show a green **✓ Offline** tag and open instantly with no internet.

> You can also cache a single article: **right‑click it → Download offline**.

---

## 5. Read

Click any article to open the distraction‑free **reader**. The toolbar (top of the reader window) gives you:

| Button | What it does |
|---|---|
| ← **Library** | Go back to your list |
| 🎨 **Theme** | Light / Sepia / Dark reading theme |
| **A−  A+** | Smaller / larger text |
| 🖊 **Highlight** | Highlight the text you've selected (see below) |
| 🔊 **Listen** | Read the article aloud (offline text‑to‑speech) — click again to pause |
| 🖨 **Export to PDF** | Save the article as a PDF |
| 🔗 **Copy link** / 🌐 **Open in browser** | Copy the URL, or open the original page |

- A thin **progress bar** at the top fills as you scroll. Close an article and reopen it later — it **resumes where you left off**.
- At the bottom is a **Tags & note** panel (click to expand) for tagging and writing a private note about the article.

### Highlighting
1. **Select** some text in the article.
2. Click the **Highlight** button (🖊). The text turns yellow.
3. To remove a highlight, **click it**.

Highlights are saved and reappear every time you open the article.

---

## 6. Organize your library

- **⭐ Ratings:** click the stars on any card/row to rate it 1–5. Click the same star again to clear it.
- **🏷 Tags:** open an article and add tags in the bottom panel, or select several articles and tag them all at once (see *Bulk actions*). Manage all your tags from **Manage tags** (bottom of the sidebar) — rename, merge (drag one onto another), delete, or create.
- **📝 Notes:** write a private note per article in the reader's bottom panel.
- **⭐ Favorites / Unread / Archive:** articles marked *important* in Raindrop show under **Favorites**. Unread vs read is tracked automatically when you open them. **Archive** (right‑click) tucks finished articles away.

### Bulk actions (multi‑select)
1. Click **Select** in the top bar (or press **Ctrl+A** to select all loaded).
2. Tick the articles you want.
3. Use the bar that appears to **Add tag**, **Remove tag**, **Mark read**, **Archive**, or **Delete** them together.

---

## 7. Find anything

- **🔎 Search:** type in the search box (top‑right). It searches titles and tags instantly — and the **full text** of any article you've downloaded offline.
- **Sidebar filters:** jump to **Favorites**, **Unread**, **Notes**, **Highlights**, **Archive**, **Ratings** (by star), or any **tag**. Each shows a count.
- **View options:** the **View** button lets you sort (Newest / Oldest / Title) and switch density (Comfortable / Compact). The **Cards** button toggles list ↔ card view.

---

## 8. Delete safely — the Recycle Bin

- **Delete** an article (right‑click, or via multi‑select) and it moves to the **Recycle Bin** — it's not gone.
- Open **Recycle Bin** in the sidebar to **Restore** anything, or **Delete permanently** / **Empty Recycle Bin** when you're sure.
- Deleted articles never come back on a sync.

---

## 9. Your reading, in numbers

Click **Stats** (top bar) to see articles read, words read, time spent, your day‑streak, a 30‑day activity chart, and your top sources and tags. The numbers build up as you read.

---

## 10. Settings & backup

Open **Settings** (bottom of the sidebar):

- **App theme** (System / Light / Dark) and **Reading theme** (Light / Sepia / Dark).
- **Offline downloads:** how many articles per batch, and how many download at once.
- **Backup:** **Export** your whole library to a JSON file, or **Import** one back.
- **Data folder:** see (and open) the portable folder where everything is stored.
- **About:** version and project links.

> **Moving to a new PC or drive?** Just copy the whole PocketReader folder. Everything — your articles, tags, notes, highlights, and settings — travels with it.

---

## Frequently asked

**Do I need to be online?**
Only to sync and to download articles. Once they're cached, reading, searching, highlighting — everything works offline.

**Does anything I do here change my Raindrop account?**
No. PocketReader only *reads* from Raindrop. Your ratings, notes, highlights, and deletions stay on your computer.

**An article won't download / looks empty.**
Some sites block automated fetching, or hide content behind a login/paywall. Most articles cache fine; a few won't. You can always **Open in browser** to read the original.

**Where is my data?**
In the `data` folder next to the app — a single SQLite database plus your cached articles. Back it up by copying that folder (or use Settings → Export).

---

Happy reading. 📖
