<div align="center">
  <img src="src/OverTranslate/icons/icon.svg" width="250" alt="OverTranslate Icon"/>
  <h1>OverTranslate</h1>
  <p>一款適合你的 Windows 螢幕翻譯工具</p>

  <p>
<a href="https://github.com/asd880921/OverTranslate/releases/latest">
  <img src="https://img.shields.io/badge/點擊下載 Download-Latest%20Release-2ea44f?style=for-the-badge&logo=github" />
</a>
  </p>
</div>

<p align="center">
  <img src="https://img.shields.io/github/v/release/asd880921/OverTranslate?style=flat-square" />
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4?style=flat-square&logo=windows" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet" />
  <img src="https://img.shields.io/badge/license-AGPL--3.0-22C55E?style=flat-square" />
</p>

---

## 這是什麼？

OverTranslate 是一款 Windows 螢幕翻譯工具。

按下快捷鍵、框選任意畫面區域，翻譯結果就會直接浮現在原位——無需切換視窗，無需複製貼上，同時也支援多種翻譯功能，也可作為翻譯軟體使用。

於遊戲介面、PDF 閱讀器、影片字幕、任何無法選取的文字，都能直接翻譯。

---

## 功能特色

### 截圖 OCR 翻譯覆蓋
按下全域快捷鍵後，用滑鼠框選畫面上的任意區域，OverTranslate 會自動辨識文字並將翻譯結果疊加在原位。

[![overlay.png](https://i.postimg.cc/DyQf5sws/overlay.png)](https://postimg.cc/Yh9wCvgq)

### 翻譯視窗
除覆蓋模式外，也提供獨立的翻譯視窗，可自由編輯原文並重新翻譯，支援交換來源與目標語言。

[![window.png](https://i.postimg.cc/MK0ZtRpm/window.png)](https://postimg.cc/JGnWzsJs)

## 翻譯 API

本專案採用多來源翻譯架構，透過統一整合層管理不同翻譯服務，並提供選項提供使用者自行切換。

| 服務 | 說明 |
|------|------|
| Google 翻譯（新版） | (預設) 首選，穩定性佳 |
| Google 翻譯（舊版） | 備援 |
| Bing 翻譯 | 可選 |
| Yandex 翻譯 | 可選 |
| DeepL | 高品質翻譯，需 API Key |

## 文字轉語音（TTS）

翻譯視窗中提供文字轉語音功能，可朗讀原文或譯文。
目前 TTS 功能透過多語音來源的整合服務實作，並由統一管理層進行語音來源選擇與自動 fallback，以提升穩定性與可用性。

| 語音來源 | 說明 |
|----------|------|
| 線上語音服務 | 提供多語音來源與自動 fallback（依可用性選擇最佳語音） |

## 多引擎 OCR 辨識
| 引擎 | 說明 |
|------|------|
| **Windows OCR** | 系統內建，支援自動偵測中（簡/繁）、英、日、韓同時辨識 |
| **Tesseract OCR** | (預設) 離線引擎，內建繁中、簡中、日、韓、英語言模型，無需安裝語言包 |

## 其他
- 可自訂全域快捷鍵
- 系統匣圖示常駐，快速存取
- 支援多螢幕環境
- 支援 30+ 種語言

---

## 系統需求

- **作業系統**：Windows 10 / 11
- **執行環境**：[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 使用 Windows OCR 時，需在「Windows 語言設定」中安裝對應語言包
- 使用 DeepL 翻譯時，需至官網申請 [DeepL API Key]（官方目前有免費方案）

---

## 使用方式

1. 啟動程式後，工具會常駐於系統匣（右下角）。
2. 按下快捷鍵（預設 `Ctrl + Alt + A`）啟動截圖模式，可在設定頁進行替換。
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
| 來源語言 | 預設為英文 |
| OCR 引擎 | Windows OCR 或 Tesseract |
| 開機啟動 | 開機時自動啟動 |
| 翻譯服務 | 翻譯平台 |
| API Key | DeepL 使用者填入 |
| 主題 | 淺色 / 深色 |

---

## 授權

本專案採用 [AGPL-3.0](https://www.gnu.org/licenses/agpl-3.0.html) 授權。  
允許個人與公司內部自由使用及修改，修改後的版本須以相同授權開源。
