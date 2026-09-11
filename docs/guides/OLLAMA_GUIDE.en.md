# Local LLM (Ollama) Setup Guide

> **Language:** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **English ✓** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate's **OpenAI** translation service supports the OpenAI API-compatible format, so you can run an LLM locally with [Ollama](https://ollama.com/). Using a local model means no extra API costs, and the content you translate is never sent to an external server.

The example below uses `hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M` (a Tencent model built for translation, covering 33 languages).

> **Hardware requirements:** A local LLM uses your computer's CPU / GPU resources. A machine with a dedicated graphics card is recommended for better translation speed.
>
> The model file used in this guide is about 4.6 GB and takes roughly 5.5 GB once loaded, so a graphics card with **at least 6 GB of VRAM (8 GB or more preferred)** is recommended. Actual memory usage still varies with Ollama, the input content, and the GPU usage of other programs.
>
> When the model is only slightly too large for your VRAM, speed drops sharply — part of it runs on the CPU instead. Switch to a smaller quantization if that happens; see below.

## 1. Install Ollama

1. Go to the [Ollama website](https://ollama.com/download) and download the installer for your operating system
2. Run the installer and complete the installation with the default options

Once installed, open the Ollama app. Ollama's API endpoint defaults to `http://localhost:11434` (if you have changed the related settings before, use your actual API endpoint instead).

## 2. Download the model

Open "Command Prompt" or "PowerShell" and run:

```
ollama pull hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M
```

Wait for the download to finish (the model is about 4.6 GB, so it takes a few minutes depending on your connection).

> **If you don't have enough VRAM**, use a smaller build of the same model by replacing the name after `ollama pull`:
>
> | Model name | File size | Suits |
> |------------|-----------|-------|
> | `hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M` | ~4.6 GB | 6 GB VRAM or more (used in this guide) |
> | `hf.co/unsloth/Hy-MT2-7B-GGUF:IQ4_XS` | ~4.2 GB | 5-6 GB VRAM |
> | `hf.co/unsloth/Hy-MT2-7B-GGUF:Q3_K_M` | ~3.8 GB | 4 GB VRAM |
>
> The last two are community repackages of the same model. A smaller build downloads faster and runs faster, at some cost to translation quality.

> You can also search [Ollama Models](https://ollama.com/search) for other models and replace the model name with the one you want to use.
> Pick a model that does not enable thinking mode; if you're not sure which one to choose, just follow this guide and use the recommended model.

## 3. Configure OverTranslate

1. Open the OverTranslate settings page → choose **OpenAI Compatible** as the translation service
2. Leave both **API endpoint** and **API Key** empty: the endpoint already defaults to Ollama's local address, and a local server needs no key
3. Leave the model setting on **Built-in** — it uses the model this guide recommends, `hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M`, and its prompt is written for it
4. Close the settings page and start translating with your local LLM

> **For a different model**, press **Add** in the list and fill in the model name (the whole thing, including the leading `hf.co/`) along with the prompt that model asks for, then save and select it. One setting is the whole set: the model name, the temperature, and a System / User prompt pair for each of **automatic** and **a chosen source language**. Switching settings switches all of it at once.

> **Prompt and temperature:** the built-in prompt follows OverTranslate's interface language and is tuned for the model recommended here. It sends a user prompt and no system prompt, which is the format that model's own documentation publishes; other models ask for other things, and one of the two halves is enough. The advanced parameters — temperature `0.7`, top P `0.6`, seed `42` — are all enabled by default, at the values the recommended model's own documentation suggests. With the seed fixed, the same source text still translates the same way every time.
