# 專案總覽

OverTranslate 是 Windows 桌面即時翻譯工具。核心使用情境是使用者按下全域快捷鍵後框選螢幕區域，程式對框選影像做 OCR，將翻譯結果覆蓋回原畫面位置。另有獨立翻譯視窗、文字轉語音、系統匣常駐、設定頁與 Velopack 更新。

## 技術棧

- .NET 8 WPF：主要 UI 與視窗。
- Windows Forms `NotifyIcon`：系統匣常駐。
- Tesseract：英文 OCR。
- RapidOcrNet + ONNX models：中文、日文、韓文 OCR。
- GTranslate + DeepL HTTP API：翻譯 provider。
- NLog：記錄。
- Velopack：安裝包與更新。
- xUnit：測試。

## 主要目錄

- `src/OverTranslate/`：主 WPF 應用程式。
- `src/OverTranslate/Services/`：OCR、翻譯、設定、主題、TTS、更新、全域快捷鍵等服務。
- `src/OverTranslate/Services/Ocr/`：OCR engine、語言分流、文字區塊聚合與 Tesseract segmentation。
- `src/OverTranslate/Services/Providers/`：翻譯 provider 介面與實作。
- `src/OverTranslate/Models/`：設定、語言與 provider 清單。
- `src/OverTranslate/ocrmodels/`：ONNX OCR 模型與字典。
- `src/OverTranslate/tessdata/`：Tesseract traineddata，目前主要是英文。
- `tests/OverTranslate.Tests/`：xUnit 測試。

## 重要根目錄文件

- `README.md`：面向使用者的產品說明。
- `PRIVACY.md`：隱私說明。
- `PUBLISH.md`：發布指令簡介。
- `publish-velopack.ps1`：實際發布腳本。
- `Plan.md`：AI 文件建置計畫，不是專案架構總覽。

## 目前架構形態

專案目前沒有使用 DI container 或 MVVM 分層框架。主要流程集中在 WPF code-behind 與 service class。`MainWindow.xaml.cs` 是背景常駐協調器，負責串接熱鍵、截圖、overlay、toolbar、OCR 與翻譯。

理解架構時應先看流程邊界，不要先做重構假設。
