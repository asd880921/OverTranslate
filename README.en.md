<div align="center">
  <p>
    <strong>Language : </strong>
    <strong>English ✓</strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.md">繁體中文</a></strong>
  </p>

  <img src="src/OverTranslate/icons/icon.svg" width="250" alt="OverTranslate Icon"/>
  <h1>OverTranslate</h1>
  <p>A Windows screen translator with screenshot translation, real-time translation, and translations overlaid in place</p>

  <p>
    <img src="https://img.shields.io/github/v/release/asd880921/OverTranslate?style=for-the-badge&label=latest%20release" alt="Latest release" />
    <img src="https://img.shields.io/badge/license-GPL--3.0-22C55E?style=for-the-badge" alt="License GPL-3.0" />
    <img src="https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/asd880921/github-badges/main/badges/overtranslate-downloads.json" alt="Total downloads" />
  </p>

  <p>
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Setup.exe">
      <strong>➡️ Windows Installer (recommended)</strong>
    </a>
    &nbsp;｜&nbsp;
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Portable.zip">
      <strong>➡️ Portable version</strong>
    </a>
  </p>

</div>

## What is this?

**OverTranslate** is a real-time screen translation tool built for Windows.

It supports both **screenshot translation** and **real-time translation**: text on your screen is recognized, translated, and displayed right where the original text is.
Whether it's games, PDFs, video subtitles, or any other text you can't select directly, you can translate it instantly without constantly switching windows while reading.

> Works on web pages, PDFs, images, videos, game interfaces, and any other screen where text can't be selected directly, including mixed-language content.

![Translation comparison](docs/images/翻譯比對圖.png)

### Features

- 🆓 **Free, no API key required** — includes multiple ready-to-use translation services, so you can start translating right after installation.
- 🎯 **Screenshot translation** — select an area to translate it, with the translation shown in the original position
- 🎬 **Real-time translation** — translations update automatically as the screen changes, ideal for video subtitles and games
- 📸 **One-click screenshot** — copy the original capture or the translated result after selecting an area, or just use it as a regular screenshot tool
- 🤖 **Local LLM** — translate with a local AI model through Ollama, with customizable translation prompts
- 🔒 **Data safety** — OCR runs entirely on your machine; when using online translation services, only the recognized text is sent to them
- 💾 **Memory management** — models are loaded and released automatically based on usage, reducing memory footprint when idle

---

## Screenshot Translation
> The main window can be closed (the app stays in the system tray); even while running in the background, screenshot translation can be triggered anytime with a hotkey (default `Ctrl + Alt + A`).

After selecting the area you want to translate, the translation is overlaid on the original position. The toolbar lets you quickly switch languages, translation services, original / translated text, screenshots, and more.

| Source text | Translation |
|------|----------|
| ![截圖翻譯1-前.png](docs/images/截圖翻譯1-前.png) | ![截圖翻譯1-後.png](docs/images/截圖翻譯1-後.png) |
| ![截圖翻譯2-前.png](docs/images/截圖翻譯2-前.png) | ![截圖翻譯2-後.png](docs/images/截圖翻譯2-後.png) |
| ![截圖翻譯3-前.png](docs/images/截圖翻譯3-前.png) | ![截圖翻譯3-後.png](docs/images/截圖翻譯3-後.png) |

---

## Real-time Translation
> Use the hotkey (default `Ctrl + Alt + W`) to bring up the main window quickly.

Ideal for **video subtitles, game screens**, and other situations that need continuous translation. After selecting the area, the screen content is recognized continuously, and the translation updates automatically in the original position whenever the text changes.

> 1. Currently only Microsoft, OpenAI, and DeepL are recommended for this mode (lower latency).
> 2. The text color and background color of the translation can be adjusted as needed.

![Real-time translation window preview](docs/images/即時翻譯視窗預覽_en.png)
### Translation Block Modes (area selection)
> Real-time translation currently supports a single monitor only, allows up to 3 translation blocks at the same time, and cannot be used together with screenshot translation.

Each translation block can be set individually to **Subtitle** or **Game UI** mode, so the recognition method matches the type of content on screen.

**Subtitle**: for video subtitles, game dialogue, and other content where text is concentrated in a fairly fixed position (1 block recommended).

| Selection | Translation result |
|-----------|--------------------|
| ![Real-time translation - video selection](docs/images/即時翻譯-影片框.png) | ![Real-time translation - video result](docs/images/即時翻譯-影片翻譯.png) |
| ![Real-time translation1 - dialogue game selection](docs/images/即時翻譯1-對話遊戲框.png) | ![Real-time translation1 - dialogue game result](docs/images/即時翻譯1-對話遊戲翻譯.png) |
| ![Real-time translation2 - dialogue game selection](docs/images/即時翻譯2-對話遊戲框.png) | ![Real-time translation2 - dialogue game result](docs/images/即時翻譯2-對話遊戲翻譯.png) |

**Game UI**: for game screens where text is scattered across different positions or moves around frequently (1 – 2 blocks recommended).
> While in game, use the hotkey (default Ctrl + Alt + A) to pause / resume translation.
> When a screen doesn't need translating, pause first and resume later — there's no need to shut down real-time translation.

| Selection | Translation result |
|-----------|--------------------|
| ![Real-time translation - game selection](docs/images/即時翻譯-遊戲翻譯框.png) | ![Real-time translation - game result](docs/images/即時翻譯-遊戲翻譯.png) |

### Floating Bar

While translation is running, the floating bar lets you **pause** or **resume translation**, **capture a screenshot of the current translation**, **re-edit the translation blocks**, or **stop translating**.

## Text Translation
> Use the hotkey (default `Ctrl + Alt + W`) to bring up the main window quickly.

Type text to translate it instantly, swap the source and target languages, and have the translation read aloud. Results from screenshot translation also show up here.

![Translation window preview](docs/images/翻譯視窗預覽_en.png)

---

## Settings

Click **Settings** in the left navigation bar, or right-click the tray icon → **Settings**. All changes are saved automatically.
| Setting | Description |
|---------|-------------|
| Interface language | Traditional Chinese / English, applied immediately (on first launch it follows your Windows display language) |
| Screenshot translation | Hotkey for the **screenshot translation** feature (customizable, default `Ctrl + Alt + A`); while real-time translation is running, it pauses / resumes the realtime translation instead |
| Open translation window | Hotkey to bring up the main window (default `Ctrl + Alt + W`); while real-time translation is running, it brings the floating bar to the front |
| Source language | The original language (default Auto) |
| Auto translate | Translates **immediately** after the area is selected, without clicking the translate button (off by default) |
| Run at startup | Launch automatically when Windows starts |
| Translation service | Choose the translation service; when using OpenAI you can set the API endpoint, model name, and translation prompt |
| Save screenshots | Save captures to your machine automatically, with a customizable folder (off by default) |
| Theme | Light / Dark |
| Application logs | Records more complete application information; recommended only while troubleshooting (off by default) |

> Logs stay on your machine and are never uploaded automatically. Even with **Application logs** enabled, the more detailed information is only stored locally, and you have to send the logs to the developer yourself if you want help investigating an issue.

---

## Translation APIs

Several translation sources are built in; all of them are free and require no API key, except DeepL.
| Service | Description |
|---------|-------------|
| Google Translate (RPC) | Newer RPC interface |
| Google Translate (Web) | Traditional web interface |
| Bing Translator | Good translation quality |
| Microsoft Translator | **(default)** Stable and fast |
| DeepL | Requires registering on DeepL's site and obtaining an API key |
| OpenAI | Supports the OpenAI API format; a local LLM is recommended, which you can set up quickly with [Ollama](OLLAMA_GUIDE.en.md); the translation prompt is customizable |

An "automatic fallback" mechanism is provided (it applies to both **screenshot translation** and **real-time translation**):
when a translation service is unavailable or responds too slowly, the app automatically switches to another available translation API, and the engine actually in use is shown in the toolbar.
![Fallback](docs/images/備援.png)

> The fallback mechanism is not triggered when using **OpenAI**.

## Text to Speech (TTS)

The translation window includes text-to-speech, which can read the original or translated text aloud to help you check pronunciation or listen to the content.
The speak button can stop playback at any time, or switch between the original and the translation.
A suitable voice is selected automatically based on the language, and if the current voice is unavailable another available source is used instead.

## Multi-language OCR

OCR is handled by RapidOcrNet with ONNX models. The matching model is selected automatically per language — there's no need to pick an OCR engine manually.

| Language | Recognition model (rec) |
|----------|-------------------------|
| **English / Chinese (Simplified & Traditional) / Japanese** | PP-OCRv6 general recognition model (`PP-OCRv6_small_rec`), a single model covering Chinese, English, Japanese and many Latin-script languages, including common mixed Chinese-English layouts |
| **Korean** | PP-OCRv5 Korean recognition model (`korean_PP-OCRv5_rec`), covering the Korean characters the general PP-OCRv6 model doesn't include |

All languages share the same **text detection model** (`PP-OCRv6_det_tiny`) and **orientation classification model** (`cls`); only the recognition model is switched per language.
OCR runs entirely on your local CPU, and images are never uploaded to any external service.

---

## System Requirements

- **Operating system**: Windows 10 / 11
- **Runtime**: the installer already bundles everything needed — no separate .NET Runtime installation required

The following translation services require some extra setup:

- **DeepL**: apply for an API key on the [DeepL website](https://www.deepl.com/pro-api)
- **OpenAI**: requires your own OpenAI-compatible API service; for setting up a local LLM, see the [Ollama guide](OLLAMA_GUIDE.en.md)

---

## Support

If this software helps you in your daily life or work, feel free to buy me a coffee on [Ko-fi](https://ko-fi.com/honlu) ~ ☕

---

## License

This project is licensed under the [GNU General Public License v3.0 (GPL-3.0)](https://www.gnu.org/licenses/gpl-3.0.html).
You may freely use, modify, and distribute this software; if you distribute a modified version, you must release the corresponding source code under the GPL-3.0 terms.
See [LICENSE](LICENSE) for the full license text.
