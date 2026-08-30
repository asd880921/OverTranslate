# ローカル LLM (Ollama) の導入ガイド

> **Language:** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **日本語 ✓** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate の **OpenAI** 翻訳サービスは OpenAI API 互換の形式に対応しているため、[Ollama](https://ollama.com/) を使って LLM をローカルで動かせます。ローカルモデルなら API の追加費用はかからず、翻訳する内容が外部のサーバーに送られることもありません。

ここでは `translategemma:4b`（Google が公開している、翻訳向けに最適化されたモデル）を例に説明します。

> **必要な環境：** ローカル LLM はパソコンの CPU / GPU を使います。翻訳の速度を考えると、独立したグラフィックスカードを搭載したマシンをおすすめします。
>
> このガイドで使う `translategemma:4b` のサイズは約 3.3 GB です。グラフィックスカードの VRAM は **4 GB 以上、できれば 6 GB 以上**を目安にしてください。実際の使用量は Ollama や入力内容、ほかのアプリの GPU 使用量によって変わります。

## 1. Ollama をインストールする

1. [Ollama の公式サイト](https://ollama.com/download) から、お使いの OS 向けのインストーラーをダウンロードします
2. インストーラーを実行し、既定のオプションのままインストールを完了します

インストール後、Ollama を起動します。Ollama の API アドレスは既定で `http://localhost:11434` です（設定を変更したことがある場合は、実際のアドレスに読み替えてください）。

## 2. モデルをダウンロードする

「コマンド プロンプト」または「PowerShell」を開き、次を実行します。

```
ollama pull translategemma:4b
```

ダウンロードが終わるまで待ちます（モデルは数 GB あるため、回線によっては数分かかります）。

> [Ollama Models](https://ollama.com/search) でほかのモデルを探し、モデル名を差し替えることもできます。
> 思考モードを使わないモデルを選んでください。迷う場合は、このガイドのとおり translategemma:4b をそのまま使えば問題ありません。

## 3. OverTranslate 側の設定

1. OverTranslate の設定ページを開き、翻訳サービスで **OpenAI Compatible** を選びます
2. 次の項目を入力します。

   | 項目 | 入力する値 |
   |------|------------|
   | API アドレス | `http://localhost:11434/v1` |
   | API キー | 任意の文字列で構いません（ローカル実行なら空欄、または `ollama`） |
   | モデル名 | `translategemma:4b` |

   > API アドレスとモデル名はアプリの既定値そのものなので、**空欄のままで構いません**。実際に使われる値が薄い文字で表示されます。

3. 保存すれば、ローカル LLM での翻訳を始められます
