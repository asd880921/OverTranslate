<div align="center">
  <img src="src/OverTranslate/icons/icon.svg" width="200" alt="OverTranslate Icon"/>
  <h1>OverTranslate</h1>
  <p>一款適合你的 Windows 螢幕翻譯工具</p>
</div>

  ![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4?style=flat-square&logo=windows)
  ![Framework](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet)
  ![License](https://img.shields.io/badge/license-MIT-22C55E?style=flat-square)

---

## 這是什麼？

OverTranslate 是一款 Windows 螢幕翻譯工具。

按下快捷鍵、框選任意畫面區域，翻譯結果就會直接浮現在原位——無需切換視窗，無需複製貼上，同時也支援多種翻譯功能，也可作為翻譯軟體使用。

於遊戲介面、PDF 閱讀器、影片字幕、任何無法選取的文字，都能直接翻譯。

---

## 功能特色

### 截圖 OCR 翻譯疊加
按下全域快捷鍵後，用滑鼠框選畫面上的任意區域，OverTranslate 會自動辨識文字並將翻譯結果以彩色氣泡疊加在原位，背景顏色會根據截圖內容自動取樣配色。

### 多引擎 OCR 辨識
| 引擎 | 說明 |
|------|------|
| **Windows OCR** | 系統內建，支援自動偵測中（簡/繁）、英、日、韓同時辨識 |
| **Tesseract OCR** | (預設) 離線引擎，內建繁中、簡中、日、韓、英語言模型，無需安裝語言包 |

### 多平台翻譯 API
| 服務 | 說明 |
|------|------|
| Google 翻譯（新版） | (預設) 首選，穩定性佳 |
| Google 翻譯（舊版） | 備援 |
| Bing 翻譯 | 備援 |
| Yandex 翻譯 | 備援 |
| DeepL | 高品質翻譯，需 API Key |

### 文字轉語音（TTS）
翻譯視窗中可直接朗讀原文或譯文，同樣支援數個服務自動備援（Google、Microsoft、Bing、Yandex）。

### 翻譯視窗
除疊加模式外，也提供獨立的翻譯視窗，可自由編輯原文並重新翻譯，支援交換來源與目標語言。

### 深色 / 淺色主題
完整支援系統風格，可在設定中手動切換。

### 其他
- 可自訂全域快捷鍵
- 系統匣圖示常駐，快速存取
- 支援多螢幕環境
- 支援 30+ 種語言

---

## 系統需求

- **作業系統**：Windows 10 (1903+) 或 Windows 11
- **執行環境**：[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 使用 Windows OCR 時，需在「Windows 語言設定」中安裝對應語言包
- 使用 DeepL 時，需申請 [DeepL API Key](https://www.deepl.com/pro-api)（提供免費方案）

---

## 使用方式

1. 啟動程式後，工具會常駐於系統匣（右下角）。
2. 按下快捷鍵（預設 `Ctrl + Alt + T`）啟動截圖模式。
3. 用滑鼠框選畫面上含有文字的區域。
4. 翻譯結果自動疊加顯示在原位。
5. 工具列提供重新翻譯、切換語言、開啟翻譯視窗等功能。
6. 按下快捷鍵或點擊空白處關閉疊加視窗。

---

## 設定

點擊系統匣圖示右鍵 → **設定**，或在翻譯視窗中點擊右上角的「設定」按鈕。

| 設定項目 | 說明 |
|----------|------|
| 快捷鍵 | 自訂啟動截圖的全域快捷鍵 |
| 來源語言 | 預設為自動偵測 |
| OCR 引擎 | Windows OCR 或 Tesseract |
| 翻譯服務 | 選擇慣用的翻譯服務 |
| API Key | DeepL 使用者填入 |
| 主題 | 淺色 / 深色 |

---

## 授權

本專案採用 [AGPL-3.0](https://www.gnu.org/licenses/agpl-3.0.html) 授權。  
允許個人與公司內部自由使用及修改，修改後的版本須以相同授權開源。
