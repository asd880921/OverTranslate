# Local LLM (Ollama) Setup Guide

> **Language:** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **English ✓** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate's **OpenAI** translation service supports the OpenAI API-compatible format, so you can run an LLM locally with [Ollama](https://ollama.com/). Using a local model means no extra API costs, and the content you translate is never sent to an external server.

The example below uses `translategemma:4b` (a Google model optimized for translation tasks).

> **Hardware requirements:** A local LLM uses your computer's CPU / GPU resources. A machine with a dedicated graphics card is recommended for better translation speed.
>
> The `translategemma:4b` model used in this guide is about 3.3 GB, so a graphics card with **at least 4 GB of VRAM (6 GB or more preferred)** is recommended. Actual memory usage still varies with Ollama, the input content, and the GPU usage of other programs.

## 1. Install Ollama

1. Go to the [Ollama website](https://ollama.com/download) and download the installer for your operating system
2. Run the installer and complete the installation with the default options

Once installed, open the Ollama app. Ollama's API endpoint defaults to `http://localhost:11434` (if you have changed the related settings before, use your actual API endpoint instead).

## 2. Download the model

Open "Command Prompt" or "PowerShell" and run:

```
ollama pull translategemma:4b
```

Wait for the download to finish (the model is a few GB, so it takes a few minutes depending on your connection).

> You can also search [Ollama Models](https://ollama.com/search) for other models and replace the model name with the one you want to use.
> Pick a model that does not enable thinking mode; if you're not sure which one to choose, just follow this guide and use the recommended translategemma:4b.

## 3. Configure OverTranslate

1. Open the OverTranslate settings page → choose **OpenAI Compatible** as the translation service
2. Fill in the following fields:

   | Field | Value |
   |-------|-------|
   | API endpoint | `http://localhost:11434/v1` |
   | API Key | Any text works (for local use it can be blank, or enter `ollama`) |
   | Model name | `translategemma:4b` |

   > These two are the app's own defaults, so you can **leave both empty** — the field shows the value it will actually use.

3. Save, and you can start translating with your local LLM
