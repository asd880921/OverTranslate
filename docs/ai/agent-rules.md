# AI 修改守則

本文件是後續 AI coding agent 開始修改前應讀的守則。目標是避免過度掃描、過度重構與誤讀生成物。

## 基本原則

- 先讀 `docs/ai/README.md`，依任務選讀文件。
- 不要從 `bin/`、`obj/`、`artifacts/`、`.omx/` 推斷主要架構。
- 修改要 surgical，每一行變更都要能追到使用者需求。
- 不要順手格式化、重構或清理無關程式碼。
- 如果發現無關 dead code，先提到，不要直接刪除。

## 高風險區域

`MainWindow.xaml.cs` 集中協調 selection、overlay、toolbar、OCR、translation session。修改前要理解：

- `_selectionSessionId`
- `CloseAll()`
- overlay closed handler
- toolbar event subscription
- capture window ownership

移除或簡化這些機制前，先確認快速關閉、重開、重翻譯時不會有舊 async result 更新新 UI。

## OCR 修改檢查點

修改 OCR 時通常要檢查：

- `OcrService`
- `OcrLanguageRouter`
- `TesseractOcrEngine`
- `CjkOnnxOcrEngine`
- `OcrTextBlockGrouper`
- `TesseractBlockSegmenter`
- `tests/OverTranslate.Tests/*Ocr*`

新增 OCR 語言時，不只要加語言清單，也要確認 engine、模型、測試與 UI source language 選項。

## 翻譯修改檢查點

修改翻譯 provider 或語言 code 時通常要檢查：

- `TranslationService`
- `ITranslationProvider`
- `GTranslateProvider`
- `DeepLProvider`
- `LanguageData`
- toolbar/translation window 的 provider 與語言選單。

DeepL 需要 API key；GTranslate providers 不需要。Yandex 對繁中有特殊 target override。

## 設定修改檢查點

設定由 `SettingsService` 讀寫執行目錄的 `appsettings.json`。改 `AppSettings` 時要保留舊設定可反序列化的能力，並確認發布流程不會覆蓋使用者設定。

## 驗證習慣

- 文件修改：檢查 `git status --short` 即可。
- 純邏輯修改：跑相關 xUnit 測試。
- OCR/overlay/UI 行為修改：跑測試後仍需要人工驗證。
- 發布腳本修改：檢查 `PUBLISH.md` 與 `publish-velopack.ps1` 行為一致。
