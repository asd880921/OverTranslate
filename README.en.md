<div align="center">
  <p>
    🌐
    <strong>English ✓</strong>
    &nbsp;｜&nbsp;
    <strong><a href="README.md">繁體中文</a></strong>
  </p>

  <h1>
    <img src="src/OverTranslate/icons/app.svg" width="180" alt="OverTranslate Icon"/>
    <br/>
    OverTranslate
  </h1>
  <p>A Windows screen translator with screenshot, real-time, quick lookup and quick translate, showing the results right on the original screen.</p>

  <p>
    <img src="https://img.shields.io/github/v/release/asd880921/OverTranslate?style=for-the-badge&label=latest%20release" alt="Latest release" />
    <img src="https://img.shields.io/badge/license-GPL--3.0-22C55E?style=for-the-badge" alt="License GPL-3.0" />
  </p>

  <p>
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Setup.exe"><img src="docs/images/ui/btn-setup.en.svg" alt="Download the Windows installer (recommended)" /></a>
    &nbsp;
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Portable.zip"><img src="docs/images/ui/btn-portable.en.svg" alt="Download the portable version" /></a>
  </p>

  <p>
    <a href="https://github.com/asd880921/github-statcards"><img src="https://raw.githubusercontent.com/asd880921/github-statcards/main/cards/overtranslate-downloads-history.svg" alt="Total downloads and daily growth" /></a>
  </p>

</div>

---

## Translation Features

OverTranslate currently offers five translation features, so you can pick the one that suits what you are doing:

- **[Screenshot Translation](#screenshot-translation)** — select an area of the screen; the text is recognised and the translation is shown right on it
- **[Real-time Translation](#real-time-translation)** — continuously translates a chosen area of a video or a game, with the translation shown in place
- **[Quick Lookup](#quick-lookup)** — select some text and a compact translation popup opens; it can be pinned to stay on screen
- **[Quick Translate](#quick-translate)** — select some text and press the hotkey to translate it and replace it in place
- **[Text Translation](#text-translation)** — the full translation window, with text input, swapping languages and reading aloud

---

## Screenshot Translation

**The main window can be closed so the app just sits in the system tray**, there's no need to keep the window open all the time.  
When you need a translation, press the hotkey (default Ctrl + Alt + A) and select the area you want to translate.
> Works on web pages, PDFs, images, videos, game interfaces, and any other screen where text can't be selected directly.

![Translation comparison](docs/images/翻譯比對圖.png)

| Source text | Translation |
|------|----------|
| ![截圖翻譯1-前.png](docs/images/截圖翻譯1-前.png) | ![截圖翻譯1-後.png](docs/images/截圖翻譯1-後.png) |
| ![截圖翻譯2-前.png](docs/images/截圖翻譯2-前.png) | ![截圖翻譯2-後.png](docs/images/截圖翻譯2-後.png) |
| ![截圖翻譯3-前.png](docs/images/截圖翻譯3-前.png) | ![截圖翻譯3-後.png](docs/images/截圖翻譯3-後.png) |

---

## Real-time Translation

Ideal for **video subtitles, game screens**, and other situations that need continuous translation. After selecting the area, the screen content is recognized continuously, and the translation updates automatically in the original position whenever the text changes.

There are two capture modes, **screen capture** and **window capture**:  
screen capture needs Windows 11 24H2 or later, window capture needs Windows 10 1903 or later.

> Currently only Microsoft, DeepL, and OpenAI are recommended for this mode (lower latency).  

> The text color, background color, and background opacity of the translation are all yours to adjust, and turning on **Match the original background** and **Keep the original text color** brings the translation closer to the colors and look already on screen.

![Real-time translation window preview](docs/images/即時翻譯視窗預覽_en.png)

### Translation Block Modes (area selection)

> While real-time translation is running, use the hotkey (default Ctrl + Alt + S) to pause / resume translation,  
> When a screen doesn't need translating, or when you want to read the original text, pause first and resume later — there's no need to shut down real-time translation.

Translation blocks come in two modes, **Subtitles / Dialogue** and **Game / UI**:
**Subtitles / Dialogue**: for scenes where the text stays in one place, such as video subtitles and game dialogue (1 block recommended).

| Selection | Translation result |
|-----------|--------------------|
| ![Real-time translation - video selection](docs/images/即時翻譯-影片框.png) | ![Real-time translation - video result](docs/images/即時翻譯-影片翻譯.png) |
| ![Real-time translation1 - dialogue game selection](docs/images/即時翻譯1-對話遊戲框.png) | ![Real-time translation1 - dialogue game result](docs/images/即時翻譯1-對話遊戲翻譯.png) |
| ![Real-time translation2 - dialogue game selection](docs/images/即時翻譯2-對話遊戲框.png) | ![Real-time translation2 - dialogue game result](docs/images/即時翻譯2-對話遊戲翻譯.png) |

**Game / UI**: for game menus and prompts, or scenes where the text is spread out and moves around (1 – 2 blocks recommended).

| Selection | Translation result |
|-----------|--------------------|
| ![Real-time translation - game selection](docs/images/即時翻譯-遊戲翻譯框.png) | ![Real-time translation - game result](docs/images/即時翻譯-遊戲翻譯.png) |

## Quick Lookup
> The hotkey (default `Ctrl + Alt + Q`) opens it on top of whatever is on screen.

Select some text and press the hotkey and it is picked up and translated straight away; with nothing selected, you can type the text in yourself.  
The window closes itself when you switch to another window; pin it if you need it to stay on screen.

![Quick lookup](docs/images/選詞翻譯.png)

---

## Quick Translate
> The hotkey (default `Ctrl + Alt + E`); translating opens no window at all.

Select some text and press the hotkey: the translation is pasted straight over it (only in fields that accept typed text; nothing can be pasted outside an input area).

![Quick translate](docs/images/快速翻譯.png)

---

## Text Translation

**Type text and it is translated right away**, and the source and target languages can be swapped in one click;  
built-in text to speech (TTS) reads both the original text and the translation aloud.

![Translation window preview](docs/images/翻譯視窗預覽_en.png)

---

## Settings

![Settings page](docs/images/設定頁_en.png)

| Setting | Description |
|---------|-------------|
| Interface language | Traditional Chinese / English, applied immediately (on first launch it follows your Windows display language) |
| Screenshot translation (hotkey) | Hotkey for the **screenshot translation** feature (customizable, default `Ctrl + Alt + A`) |
| Open translation window (hotkey) | Hotkey to bring up the main window on the page you left it on (default `Ctrl + Alt + W`); while real-time translation is running, it brings the floating bar to the front |
| Pause / resume (hotkey) | Pauses or resumes **real-time translation** (default `Ctrl + Alt + S`); available only while real-time translation is running, and handy for reading the original text |
| Quick lookup (hotkey) | Opens the **quick lookup** window (default `Ctrl + Alt + Q`); any text you have selected is picked up and translated automatically |
| Quick translate (hotkey) | Replaces the selected text with its translation (default `Ctrl + Alt + E`); nothing happens when nothing is selected |
| Auto translate | **Screenshot translation** translates **immediately** once the area is selected, with nothing left to click (off by default) |
| Run at startup | Launch automatically when Windows starts |
| Save screenshots | Save captures to your machine automatically, with a customizable folder (off by default) |
| Source language | The original language for **screenshot translation** and **text translation** (default Auto); real-time translation has its own source language |
| Service setup | Set up the services that need a key or an endpoint; when using OpenAI you can set the API endpoint, model name, translation prompt, and temperature |
| Theme | Light / Dark |
| Application logs | Records more complete application information; recommended only while troubleshooting (off by default) |

> As well as key combinations, a shortcut can be a single key: F1 – F12, the middle or side mouse buttons, or a gamepad button.

> Logs are stored on your machine only and are never uploaded automatically; the detailed information recorded with **Application logs** enabled also stays on your machine.  
> To report a problem, press **Export and upload diagnostics** on the settings page — the diagnostics are uploaded for you, and you get a report code back (give it to the developer to identify your report quickly).

---

### Translation APIs

> Apart from DeepL and OpenAI, everything else works right after you download the app.

| Service | Description |
|---------|-------------|
| Google Translate (RPC) | Newer RPC interface |
| Google Translate (Web) | Traditional web interface |
| Bing Translator | Good translation quality |
| Microsoft Translator | **(default)** Stable and fast |
| DeepL | Requires registering on DeepL's site and obtaining an API key |
| OpenAI | Supports the OpenAI API format; a local LLM is recommended, which you can set up quickly with [Ollama](OLLAMA_GUIDE.en.md); the prompt and temperature are customizable |
  
An "automatic fallback" mechanism is provided (it applies to both **screenshot translation** and **real-time translation**):
when a translation service is unavailable or responds too slowly, the app automatically switches to another available translation API, and the engine actually in use is shown in the toolbar.
![Fallback](docs/images/備援.png)

> The fallback mechanism is not triggered when using **OpenAI**.

### OpenAI settings

![OpenAI settings](docs/images/OpenAI.png)

| Setting | Description |
|---------|-------------|
| API URL | Empty uses `http://localhost:11434/v1` (Ollama's local default) |
| Model | Empty uses `translategemma:4b` |
| Prompt | Empty uses the built-in prompt; the parameters below are replaced with the languages actually in use |
| Temperature | Under **Advanced**; controls how random the output is, from 0.0 to 2.0 (default 0) |

Prompt parameters (the **Available parameters** block on the settings page lists them with descriptions and examples too):

| Parameter | Description | Example |
|-----------|-------------|---------|
| `{source_name}` | Source language name | English |
| `{source_code}` | Source language code | en |
| `{target_name}` | Target language name | Japanese |
| `{target_code}` | Target language code | ja |

> The built-in prompt uses the language names only; the code parameters can be combined as your model needs, for example `{target_name} (target_code)` -> `Japanese (ja)`.
> 
> Turning temperature off **leaves the parameter out of the request**. Models and APIs differ in whether they accept it and in what they recommend; if the model's documentation says not to send it, turn it off. If the output misbehaves at 0, raise it as the model suggests — start at 0.1 and work up.

### Multi-language OCR

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

## ☕ Support the Project

OverTranslate is a free Windows translation tool.

If you find this project useful, you can support its continued development and maintenance by [buying me a coffee](https://buymeacoffee.com/asd880921g).

---

## License

This project is licensed under the [GNU General Public License v3.0 (GPL-3.0)](https://www.gnu.org/licenses/gpl-3.0.html).
You may freely use, modify, and distribute this software; if you distribute a modified version, you must release the corresponding source code under the GPL-3.0 terms.
See [LICENSE](LICENSE) for the full license text.
