# 本地 LLM（Ollama）安裝教學

> **Language：** **繁體中文 ✓** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate 的 **OpenAI** 翻譯服務支援 OpenAI API 相容格式，可搭配 [Ollama](https://ollama.com/) 在本機執行 LLM 模型。使用本地模型不需要額外支付 API 費用，翻譯內容也不會傳送至外部伺服器。

以下以 `hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M`（騰訊推出的翻譯專用模型，支援 33 種語言互譯）作為範例。

> **硬體需求：** 本地 LLM 會占用電腦的 CPU / GPU 資源。建議使用具備獨立顯示卡的電腦，以獲得較好的翻譯速度。
>
> 本教學使用的模型檔案約 4.6 GB，載入後約占用 5.5 GB，建議顯示卡具備 **至少 6 GB VRAM，8 GB 以上較佳**。實際記憶體占用仍會依 Ollama、輸入內容及其他程式的 GPU 使用量而有所不同。
>
> 顯示卡記憶體只差一點就不夠時，速度會大幅下降（模型會有一部分改由 CPU 執行），此時建議改用較小的量化版本，見下方說明。

## 1. 安裝 Ollama

1. 前往 [Ollama 官網](https://ollama.com/download) 下載對應作業系統的安裝程式
2. 執行安裝檔，依照預設選項完成安裝即可

安裝完成後，開啟 Ollama 應用。Ollama 的 API 位址預設為 `http://localhost:11434` (若曾修改過相關設定，請以實際的 API 位址為準)

## 2. 下載模型

打開「命令提示字元」或「PowerShell」，輸入：

```
ollama pull hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M
```

等待下載完成即可（模型大小約 4.6 GB，依網路狀況需要幾分鐘）。

> **顯示卡記憶體不足時**，可改用同一個模型的較小版本，把 `ollama pull` 後面的名稱換成下列其中一個：
>
> | 模型名稱 | 檔案大小 | 適用 |
> |----------|----------|------|
> | `hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M` | 約 4.6 GB | 6 GB 以上 VRAM（本教學使用） |
> | `hf.co/unsloth/Hy-MT2-7B-GGUF:IQ4_XS` | 約 4.2 GB | 5～6 GB VRAM |
> | `hf.co/unsloth/Hy-MT2-7B-GGUF:Q3_K_M` | 約 3.8 GB | 4 GB VRAM |
>
> 後兩個由社群重新打包，模型本身相同。數字越小檔案越小、速度越快，翻譯品質則會略為下降。

> 也可以在 [Ollama Models](https://ollama.com/search) 搜尋其他模型，並替換成想使用的模型名稱。
> 需選擇不啟用思考模式的模型；如果不確定該選哪個，可直接依照本教學使用推薦的模型。

## 3. 在 OverTranslate 設定

1. 開啟 OverTranslate 設定頁 → 翻譯服務選擇 **OpenAI Compatible**
2. **API 位址** 與 **API Key** 都可以留空：位址的預設值就是 Ollama 的本機位址，本機執行也不需要金鑰
3. **模型設定** 保持在 **系統預設** 即可 —— 它用的正是本教學推薦的模型 `hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M`，提示詞也是為它寫的
4. 關閉設定頁後即可開始使用本機 LLM 進行翻譯

> **換成其他模型時**，在 **設定清單** 按 **新增設定**，填入模型名稱（包含 `hf.co/` 開頭的完整名稱）與該模型建議的提示詞，儲存後選取即可。一份設定就是一整組：模型名稱、Temperature，以及 **自動** 與 **指定語言** 各一組 System / User 提示詞，切換設定會整組一起換。

> **提示詞與 Temperature**：系統預設的提示詞會依照 OverTranslate 的介面語言自動切換，並已針對本教學推薦的模型調整過。它只送 User 提示詞、不送 System 提示詞，這是依照該模型官方文件的格式；不同模型的要求不同，兩者至少填寫一項即可。進階參數（Temperature `0.7`、Top P `0.6`、Seed `42`）預設全部啟用，數值為推薦模型官方文件的建議值；Seed 固定的情況下，相同的原文每次仍會得到一致的譯文。
