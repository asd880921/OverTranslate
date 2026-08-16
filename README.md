<div align="center">
  <p>
    <strong>Language : </strong>
    <strong><a href="README.en.md">English</a></strong>
    &nbsp;｜&nbsp;
    <strong>繁體中文 ✓</strong>
  </p>

  <img src="src/OverTranslate/icons/icon.svg" width="250" alt="OverTranslate Icon"/>
  <h1>OverTranslate</h1>
  <p>截圖翻譯、即時翻譯、譯文原位覆蓋的 Windows 螢幕翻譯工具</p>

  <p>
    <img src="https://img.shields.io/github/v/release/asd880921/OverTranslate?style=for-the-badge&label=latest%20release" alt="Latest release" />
    <img src="https://img.shields.io/badge/license-GPL--3.0-22C55E?style=for-the-badge" alt="License GPL-3.0" />
    <img src="https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/asd880921/github-badges/main/badges/overtranslate-downloads.json" alt="Total downloads" />
  </p>

  <p>
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Setup.exe">
      <strong>➡️ Windows 安裝版（推薦）</strong>
    </a>
    &nbsp;｜&nbsp;
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Portable.zip">
      <strong>➡️ 免安裝版（Portable）</strong>
    </a>
  </p>

</div>

## 這是什麼？

**OverTranslate** 是一款專為 Windows 打造的即時螢幕翻譯工具。

支援 **截圖翻譯** 與 **即時翻譯**，可將畫面中的文字辨識翻譯後，直接顯示在原本的位置。  
無論是遊戲、PDF、影片字幕或其他無法直接選取的文字，都能快速翻譯，減少閱讀時來回切換視窗的干擾。

> 用於網頁、PDF、圖片、影音、遊戲介面等各種沒辦法直接選取文字的畫面，可支援多語言混合場景。

![翻譯比對圖.png](docs/images/翻譯比對圖.png)

### 特色

- 🆓 **免費、免 API Key** — 內建多個可直接使用的翻譯服務，安裝後即可開始翻譯
- 🎯 **截圖翻譯** — 框選畫面即可翻譯，譯文直接顯示在原本的位置
- 🎬 **即時翻譯** — 畫面內容變化時自動更新翻譯，適合影片字幕與遊戲
- 📸 **一鍵截圖** — 框選後可直接複製原始畫面或翻譯結果，也能作為一般截圖工具使用
- 🤖 **本地 LLM** — 可搭配 Ollama 使用本地 AI 模型翻譯，也能自訂翻譯提示詞
- 🔒 **資料安全** — OCR 全程在本機執行；使用線上翻譯服務時，僅文字辨識結果會傳送至翻譯服務
- 💾 **記憶體管理** — 依使用狀態自動載入與釋放所需模型，降低閒置時的記憶體佔用

---

## 截圖翻譯

程式可以直接關閉主視窗並常駐在系統匣，平常不需要一直把視窗開著，  
需要翻譯時按下快捷鍵（預設 Ctrl + Alt + A），框選想翻譯的畫面即可。

| 原文 | 翻譯結果 |
|------|----------|
| ![截圖翻譯1-前.png](docs/images/截圖翻譯1-前.png) | ![截圖翻譯1-後.png](docs/images/截圖翻譯1-後.png) |
| ![截圖翻譯2-前.png](docs/images/截圖翻譯2-前.png) | ![截圖翻譯2-後.png](docs/images/截圖翻譯2-後.png) |
| ![截圖翻譯3-前.png](docs/images/截圖翻譯3-前.png) | ![截圖翻譯3-後.png](docs/images/截圖翻譯3-後.png) |

---

## 即時翻譯

適合 **影片字幕、遊戲畫面** 等需要持續翻譯的情境。框選需要翻譯的區域後，會持續辨識畫面內容，  
文字變動時自動更新譯文並顯示在原本的位置。

> 目前只建議使用 Microsoft、DeepL、OpenAI 進行翻譯 (延遲較低)。  
> 譯文的文字顏色、背景色與背景不透明度都可依需求調整。

![即時翻譯視窗預覽.png](docs/images/即時翻譯視窗預覽.png)

### 翻譯區塊模式 (框選)

> 快捷鍵（預設 `Ctrl + Alt + S`）可直接進入翻譯區塊選取模式。  
> 即時翻譯目前只支援單一螢幕，最多可同時建立 3 個翻譯區塊。  

每個翻譯區塊都可以個別選擇 **字幕 / 對話** 或 **遊戲 / UI** 模式，並依不同畫面類型使用對應的辨識方式。  
**字幕 / 對話**：適合影音字幕、遊戲對話等文字位置較集中且固定的場景 (建議使用 1 個)。

| 框選 | 翻譯結果 |
|------|----------|
| ![即時翻譯-影片框.png](docs/images/即時翻譯-影片框.png) | ![即時翻譯-影片翻譯.png](docs/images/即時翻譯-影片翻譯.png) |
| ![即時翻譯1-對話遊戲框.png](docs/images/即時翻譯1-對話遊戲框.png) | ![即時翻譯1-對話遊戲翻譯.png](docs/images/即時翻譯1-對話遊戲翻譯.png) |
| ![即時翻譯2-對話遊戲框.png](docs/images/即時翻譯2-對話遊戲框.png) | ![即時翻譯2-對話遊戲翻譯.png](docs/images/即時翻譯2-對話遊戲翻譯.png) |

**遊戲 / UI**：適合遊戲中的介面、提示或文字位置較分散、不固定的場景 (建議使用 1 ~ 2 個)。
> 遊戲中可使用快捷鍵（預設 Ctrl + Alt + A）暫停 / 繼續翻譯，  
> 遇到不需要翻譯的畫面時可先暫停，之後再直接恢復，不必關閉即時翻譯。

| 框選 | 翻譯結果 |
|------|----------|
| ![即時翻譯-遊戲翻譯框.png](docs/images/即時翻譯-遊戲翻譯框.png) | ![即時翻譯-遊戲翻譯.png](docs/images/即時翻譯-遊戲翻譯.png) |

## 文字翻譯
> 快捷鍵（預設 `Ctrl + Alt + W`）可快速開啟主視窗。

輸入文字後會即時翻譯，來源與目標語言可以快速互換，也有內建文字轉語音（TTS），  
可以朗讀原文或翻譯結果。

![翻譯視窗預覽.png](docs/images/翻譯視窗預覽.png)

---

## 設定

從左側導覽列點擊 **設定**，或於系統匣圖示上按右鍵 → **設定**。所有設定變更都會自動儲存。
| 設定項目 | 說明 |
|----------|------|
| 介面語言 | 繁體中文 / English，切換後立即生效（首次啟動時依 Windows 顯示語言決定） |
| 截圖翻譯 (快捷鍵) | 用於 **截圖翻譯** 功能的快捷鍵 (可自訂修改，預設 `Ctrl + Alt + A`)；即時翻譯進行中時，改為暫停 / 繼續目前的即時翻譯 |
| 開啟翻譯視窗 (快捷鍵) | 呼叫主視窗的快捷鍵 (預設 `Ctrl + Alt + W`)；即時翻譯進行中時，改為將浮動視窗列移至最上層 |
| 即時翻譯區塊 (快捷鍵) | 直接進入 **即時翻譯** 的區塊選取模式，不需開啟主視窗 (預設 `Ctrl + Alt + S`) |
| 自動翻譯 | **截圖翻譯** 框選完成後 **立即翻譯**，不需要再手動點擊 (預設為關閉) |
| 開機啟動 | 開機時自動啟動 |
| 儲存截圖 | 截圖時自動儲存至本機，可自訂儲存位置（預設為關閉） |
| 來源語言 | **截圖翻譯** 與 **文字翻譯** 的原文語言 (預設 Auto)；即時翻譯的來源語言另外設定 |
| 翻譯服務 | 選擇使用的翻譯服務；使用 OpenAI 時，可設定 API 位址、模型名稱、翻譯提示詞與 Temperature |
| 主題 | 淺色 / 深色 |
| 應用紀錄 | 記錄較完整的應用程式資訊，建議僅在問題排查時開啟（預設為關閉） |

> Log 僅會保留在本機，不會自動上傳，即使開啟 **應用紀錄** 紀錄更詳細的資訊，也只會在本地儲存，需由使用者主動提供 Log 給開發者協助確認。

---

## 翻譯 API

> 除了 DeepL 與 OpenAI 以外，其他都是下載後就可以直接使用的功能。

| 服務 | 說明 |
|------|------|
| Google 翻譯（RPC） | 新版 RPC 介面 |
| Google 翻譯（Web） | 傳統 Web 介面 |
| Bing 翻譯 | 翻譯品質佳 |
| Microsoft 翻譯 | **(預設)** 穩定性佳、回應速度快 |
| DeepL | 需至 DeepL 官方註冊並取得 API Key |
| OpenAI | 支援 OpenAI API 格式，建議使用本地 LLM，可透過 [Ollama](OLLAMA_GUIDE.md) 快速安裝與使用；提示詞與 Temperature 可自訂 |
  
提供「自動備援」機制（備援機制適用於 **截圖翻譯** 與 **即時翻譯**）：  
當某個翻譯無法使用或回應過慢時，會自動切換到其他可用的翻譯 API，實際使用的引擎顯示於工具列。
![備援.png](docs/images/備援.png)

> 使用 **OpenAI** 時，不會觸發備援機制。

### OpenAI 設定

| 項目 | 說明 |
|------|------|
| API 位址 | 留空時使用 `http://localhost:11434/v1`（Ollama 的本機預設位址） |
| 模型名稱 | 留空時使用 `translategemma:4b` |
| 提示詞 | 留空時使用內建提示詞；可用下方參數代入實際使用的語言 |
| Temperature | 位於 **進階** 區，影響輸出的隨機程度，範圍 0.0 ~ 2.0（預設 0） |

提示詞可用參數（設定頁的 **可用參數** 區塊也會列出說明與範例）：

| 參數 | 說明 | 範例 |
|------|------|------|
| `{source_name}` | 來源語言名稱 | 英語 |
| `{source_code}` | 來源語言代碼 | en |
| `{target_name}` | 目標語言名稱 | 日語 |
| `{target_code}` | 目標語言代碼 | ja |

> 內建提示詞僅使用語言名稱；語言代碼參數可依模型需求自行搭配使用，例如 `{target_name} (target_code)` -> `Japanese (ja)`。
> 
> Temperature 取消勾選時 **不會傳送此參數**。不同模型或 API 對 Temperature 的支援與建議值可能不同；若模型文件建議不要傳送此參數，請取消勾選。若在 0 時輸出表現異常，可依模型建議嘗試調高，建議從 0.1 開始逐步調整。

## 多語言 OCR 辨識

OCR 辨識由 RapidOcrNet 搭配 ONNX 模型處理，會依語言自動使用對應模型，不需手動選擇 OCR 引擎。

| 辨識語言 | 辨識模型（rec） |
|----------|----------------|
| **英文 / 中文（簡繁）/ 日文** | PP-OCRv6 通用辨識模型（`PP-OCRv6_small_rec`），單一模型支援中、英、日及多種拉丁語系，也能處理常見的中英混排 |
| **韓文** | PP-OCRv5 韓文辨識模型（`korean_PP-OCRv5_rec`），用於補足 PP-OCRv6 通用模型未涵蓋的韓文字元 |

所有語言共用同一套**文字偵測模型**（`PP-OCRv6_det_tiny`）與**方向分類模型**（`cls`），僅辨識模型會依語言切換。  
OCR 全程於本機 CPU 執行，不會將圖片上傳至外部服務。

---

## 系統需求

- **作業系統**：Windows 10 / 11
- **執行環境**：安裝檔已內含所需環境，不需另外安裝 .NET Runtime

使用以下翻譯服務時，需另外準備：

- **DeepL**：需至 [DeepL 官網](https://www.deepl.com/pro-api) 申請 API Key
- **OpenAI**：需自備 OpenAI API 相容服務，本地 LLM 架設使用方式可參考 [Ollama 安裝教學](OLLAMA_GUIDE.md)

---

## 支持

本軟體若對你日常或工作使用上有幫助，歡迎透過 [Ko-fi](https://ko-fi.com/honlu) 請我喝杯咖啡 ~ ☕

---

## 授權

本專案採用 [GNU General Public License v3.0（GPL-3.0）](https://www.gnu.org/licenses/gpl-3.0.html) 授權。  
你可以自由使用、修改與散布本軟體；若散布修改後的版本，需依 GPL-3.0 授權條款公開相應的原始碼。  
完整授權條款請參閱 [LICENSE](LICENSE)。

