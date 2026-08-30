# Bandersnatch Interactive — Jellyfin Plugin

A Jellyfin plugin that injects **Netflix-accurate interactive choice UI** directly into the native Jellyfin player for **Black Mirror: Bandersnatch**.

No separate player page. No custom video player. Your normal Jellyfin player — plus choice buttons.

---

## ✨ Features

- 🎬 **Native player integration** — choice overlay injected on top of the standard Jellyfin player (inspired by the intro-skipper pattern)
- 🔍 **Auto-detection** — identifies Bandersnatch by title + duration (~5h 12m interactive cut)
- 🃏 **250 segments, 232 choice points** — complete interactive graph sourced from the original Netflix data
- ⌨️ **Arrow key + hover navigation** — highlight your choice; it commits at the natural segment end (just like Netflix)
- ⏱️ **Countdown bar** — shows time remaining before the default plays
- ⏮️ **Seek detection** — crossing a chapter boundary pauses + shows a dark blur overlay to prevent spoilers
- ⚙️ **Admin settings** — enable/disable, configure lead-in time, manual item ID override
- 🔒 **Seek spoiler protection** — full-screen blur overlay when seeking past a choice point

## 🎮 How Choices Work

1. As the video approaches a branch point, choice buttons **fade in** (configurable lead-in, default 10s before the branch)
2. **Arrow Left/Right** or **hover** to highlight your preferred choice
3. The video plays to its natural end — at that moment, it automatically seeks to your highlighted choice's segment
4. If nothing is highlighted when the timer expires, the **default choice** is selected
5. **Click** only highlights — it does not commit early

## ⏮️ Seek Behavior

| Direction | What was crossed | Response |
|-----------|-----------------|----------|
| Forward | A **choice point** chapter | Pause + blur + *"You skipped a choice. Go back?"* |
| Backward | **Any** chapter boundary | Pause + blur + *"Jump back to [section] to choose again?"* |

The dark blur overlay prevents accidentally seeing content from segments you didn't choose.

## 🛠️ Requirements

- **Jellyfin Server**: 10.9.0 or later
- **Video file**: Black Mirror Bandersnatch — the **full interactive cut** (~5h 12m / 18,734s)
  - The standard ~90-minute version is automatically ignored

## 🚀 Installation

### Option 1: Plugin Repository (Recommended)

1. **Jellyfin Admin** → **Plugins** → **Repositories** → **+**
2. **Name**: `Bandersnatch Interactive`
3. **URL**: `https://raw.githubusercontent.com/williamzafaran/bandersnatch-choice-plugin/master/manifest.json`
4. Save → **Catalog** → find **Bandersnatch Interactive** → **Install** → Restart Jellyfin

### Option 2: Manual Install

1. Download the latest `bandersnatch-choice-plugin.zip` from [Releases](https://github.com/williamzafaran/bandersnatch-choice-plugin/releases)
2. Extract and copy `Jellyfin.Plugin.BandersnatchChoice.dll` + `meta.json` to your Jellyfin plugins directory:
   - **Linux**: `/var/lib/jellyfin/plugins/BandersnatchInteractive/`
   - **Windows**: `%PROGRAMDATA%\Jellyfin\Server\plugins\BandersnatchInteractive\`
3. Restart Jellyfin

## ⚙️ Configuration

After installing, go to **Admin Dashboard** → **Plugins** → **Bandersnatch Interactive**:

| Setting | Default | Description |
|---------|---------|-------------|
| Enable interactive features | On | Master on/off toggle |
| Lead-in time | 10s | How early before the branch point to show choices |
| Auto-detect | On | Match by title + duration |
| Manual Item ID | — | Override if auto-detection picks the wrong item |

## 🏗️ Building from Source

```bash
git clone https://github.com/williamzafaran/bandersnatch-choice-plugin.git
cd bandersnatch-choice-plugin
dotnet build BandersnatchChoice.csproj -c Release --output ./build-output
```

Requires .NET 8 SDK and Jellyfin 10.9.0.

## 📖 How It Works

The plugin:
1. **On Jellyfin startup**: Injects a `<script>` and `<link>` tag into Jellyfin's `index.html`
2. **On playback**: The injected `bandersnatch.js` detects when the Bandersnatch interactive cut is playing
3. **Fetches** the full segment/choice metadata from `/BandersnatchChoice/Metadata`
4. **Monitors** `timeupdate` events — shows choice overlays at the right timestamps
5. **On choice commit**: Seeks `<video>.currentTime` to the start of the chosen segment

## ⚖️ License

Public domain (Unlicense). The interactive segment metadata originates from the original Netflix data, reverse-engineered by the community.

## 🙏 Credits

- Segment data sourced from the community reverse-engineering of Netflix's Bandersnatch data
- Injection pattern inspired by [intro-skipper](https://github.com/intro-skipper/intro-skipper)
