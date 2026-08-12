<div align="center">
  <img src="src/OverTranslate/icons/icon.svg" width="250" alt="OverTranslate Icon"/>
  <h1>OverTranslate</h1>
  <p>框選畫面、OCR 辨識、譯文原位覆蓋的 Windows 螢幕即時翻譯工具</p>

  <p>
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Setup.exe">
      <img src="https://shieldcn.dev/github/downloads-asset/asd880921/OverTranslate/OverTranslate-win-Setup.exe.svg?style=for-the-badge&label=installer%20downloads&labelColor=24292f&color=2ea44f" alt="Installer downloads" />
    </a>
  </p>

  <p>
    <img src="https://img.shields.io/github/v/release/asd880921/OverTranslate?style=for-the-badge&label=latest%20release" alt="Latest release" />
    <img src="https://img.shields.io/badge/license-AGPL--3.0-22C55E?style=for-the-badge" alt="License AGPL-3.0" />
  </p>

  <p>
    <a href="https://github.com/asd880921/OverTranslate/releases/latest/download/OverTranslate-win-Setup.exe">
      <strong>📥 點擊下載最新版 Windows 安裝檔</strong>
    </a>
  </p>

</div>

## 這是什麼？

**OverTranslate** 是一款專為 Windows 打造的即時螢幕翻譯工具。

只需按下快捷鍵並框選畫面，即可將翻譯結果直接顯示在原位置。無論是遊戲、PDF、影片字幕或無法選取的文字，都能輕鬆翻譯，讓閱讀不中斷。

![翻譯比對圖.png](docs/images/翻譯比對圖.png)

### 特色

- 🆓 **免費、免 API Key** — 內建多家翻譯服務
- 🎯 **原位覆蓋** — 譯文直接疊加於原文位置，閱讀不中斷
- 🔁 **多引擎自動備援** — 首選引擎未能即時回應時自動切換，提升穩定性
- 📸 **一鍵截圖** — 框選即可複製原圖或譯文，也能當作一般截圖工具使用
- 💾 **記憶體管理** — 依當前語言組合載入所需模型，閒置約 1 分鐘時將自動釋放以降低佔用
- 🔒 **資料安全** — OCR 完全在本機運行，僅辨識結果會傳送至翻譯服務
---

## 翻譯視窗

除截圖翻譯模式外，也提供獨立的文字翻譯視窗：  
即時同步翻譯輸入內容，支援來源與目標語言互換，翻譯後的結果也可以使用語音功能。
![翻譯視窗預覽.png](docs/images/翻譯視窗預覽.png)

---

## 設定

點擊系統匣圖示右鍵 → **設定**，或在翻譯視窗中點擊右上角的「設定」按鈕。
| 設定項目 | 說明 |
|----------|------|
| 快捷鍵 | 用於 **截圖翻譯** 功能 (可自訂修改) |
| 來源語言 | 預設為英文 |
| 自動翻譯 | 框選完成後會**立即自動翻譯**，不用手動點選翻譯按鈕 (預設為關閉) |
| 開機啟動 | 開機時自動啟動 |
| 翻譯服務 | 翻譯平台 |
| 儲存截圖 | 截圖時自動保存至本機，可自訂儲存位置（預設為關閉） |
| 主題 | 淺色 / 深色 |

![設定.png](docs/images/設定預覽.png)

---

## 翻譯 API (使用 GTranslate DLL)

內建多種翻譯來源，均免費、無需 API Key。
| 服務 | 說明 |
|------|------|
| Google 翻譯（RPC） | 新版 RPC 介面 |
| Google 翻譯（Web） | 傳統 Web 介面 |
| Bing 翻譯 | 翻譯品質佳 |
| Microsoft 翻譯 | **(預設)** 穩定性佳、回應速度快 |
| DeepL | 需至 DeepL 官方註冊並取得 API Key |

提供「自動備援」機制（備援機制僅適用於 **截圖翻譯**）：  
當某個翻譯無法使用或回應過慢時，會自動切換到其他可用的翻譯 API，實際使用的引擎顯示於工具列 (翻譯結果可能會是混合組成的)。  
![備援.png](docs/images/備援.png)

## 文字轉語音（TTS）

翻譯視窗中提供文字轉語音功能，可朗讀原文或譯文，協助確認發音或聆聽內容。  
朗讀按鈕可隨時停止，或在原文與譯文間切換。  
程式會依語言自動挑選合適的語音，並在語音無法使用時自動改用其他來源，盡量確保朗讀順暢。  

## 多語言 OCR 辨識

目前 OCR 辨識統一由 RapidOcrNet 搭配 ONNX 模型處理，使用者無需手動選擇 OCR 引擎。
| 辨識語言 | 辨識模型（rec） |
|----------|----------------|
| **英文 / 中文（簡繁）/ 日文** | PP-OCRv5 通用辨識模型（`PP-OCRv5_rec`，搭配 `ppocrv5_dict`；單一模型同時支援中、英、日，英文介面常見的中英混排可一次辨識） |
| **韓文** | PP-OCRv5 韓文辨識模型（`korean_PP-OCRv5_rec`） |

所有語言共用同一套**文字偵測模型**（`PP-OCRv5 det`）與**方向分類模型**（`cls`），僅辨識（rec）模型依語言切換，提升中、日、韓、英等多語系文字的辨識效果與穩定性，並完全在 CPU 端執行。

---

## 使用方式

1. 啟動程式後，工具會常駐於系統匣（右下角）。
2. 按下快捷鍵（預設 `Ctrl + Alt + A`）啟動截圖模式，可在設定頁進行替換。
3. 用滑鼠框選畫面上含有文字的區域。
4. 翻譯結果自動疊加顯示在原位。
5. 工具列提供重新翻譯、切換語言、截圖複製、開啟翻譯視窗等功能。
6. 按下快捷鍵或點擊空白處關閉疊加視窗。

---

## 系統需求

- **作業系統**：Windows 10 / 11
- **執行環境**：[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 使用 DeepL 翻譯時，需至 [DeepL 官網](https://www.deepl.com/pro-api) 註冊並取得 API Key

---

## 授權

本專案採用 [GNU General Public License v3.0 (GPL-3.0)](https://www.gnu.org/licenses/gpl-3.0.html) 授權。
你可以自由使用、修改與散布本軟體，但若散布修改後的版本，必須依照 GPL-3.0 授權條款公開相應原始碼。
詳細條款請參閱 LICENSE。
