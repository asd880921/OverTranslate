# 本地 LLM（Ollama）安裝教學

> **Language：** **繁體中文 ✓** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate 的 **OpenAI** 翻譯服務支援 OpenAI API 相容格式，可搭配 [Ollama](https://ollama.com/) 在本機執行 LLM 模型。使用本地模型不需要額外支付 API 費用，翻譯內容也不會傳送至外部伺服器。

以下以 `translategemma:4b`（Google 推出，專為翻譯任務優化的模型）作為範例。

> **硬體需求：** 本地 LLM 會占用電腦的 CPU / GPU 資源。建議使用具備獨立顯示卡的電腦，以獲得較好的翻譯速度。
>
> 本教學使用的 `translategemma:4b` 模型大小約 3.3 GB，建議顯示卡具備 **至少 4 GB VRAM，6 GB 以上較佳**。實際記憶體占用仍會依 Ollama、輸入內容及其他程式的 GPU 使用量而有所不同。

## 1. 安裝 Ollama

1. 前往 [Ollama 官網](https://ollama.com/download) 下載對應作業系統的安裝程式
2. 執行安裝檔，依照預設選項完成安裝即可

安裝完成後，開啟 Ollama 應用。Ollama 的 API 位址預設為 `http://localhost:11434` (若曾修改過相關設定，請以實際的 API 位址為準)

## 2. 下載模型

打開「命令提示字元」或「PowerShell」，輸入：

```
ollama pull translategemma:4b
```

等待下載完成即可（模型大小約數 GB，依網路狀況需要幾分鐘）。

> 也可以在 [Ollama Models](https://ollama.com/search) 搜尋其他模型，並替換成想使用的模型名稱。
> 需選擇不啟用思考模式的模型；如果不確定該選哪個，可直接依照本教學使用推薦的 translategemma:4b。

## 3. 在 OverTranslate 設定

1. 開啟 OverTranslate 設定頁 → 翻譯服務選擇 **OpenAI Compatible**
2. 依序填入：

   | 欄位 | 填入內容 |
   |------|----------|
   | API 位址 | `http://localhost:11434/v1` |
   | API Key | 任意文字皆可（本機執行可空白 或 輸入 `ollama`） |
   | 模型名稱 | `translategemma:4b` |

   > API 位址與模型名稱就是程式的預設值，**留空即可**，欄位會以淡色顯示實際會套用的內容。

3. 儲存後即可開始使用本機 LLM 進行翻譯
