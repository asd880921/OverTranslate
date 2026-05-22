# 子系統說明

本文件描述 OCR、翻譯、TTS、設定、主題與更新等 service 層。修改子系統時先確認對外入口、資料型別與 UI 呼叫端。

## OCR

對外入口是 `OcrService`。輸入是 `Bitmap` 與 source language code，輸出是 `List<OcrTextBlock>`。

`OcrLanguageRouter` 決定 engine：

- `EN` 使用 `TesseractOcrEngine`。
- `ZH`、`ZH-HANT`、`JA`、`KO` 使用 `CjkOnnxOcrEngine`。
- 其他語言目前不支援 OCR。

所有 engine 結果會經過 `OcrTextBlockGrouper.Group()` 聚合，讓相鄰碎片或多行文字形成較合理翻譯單位。

`TesseractOcrEngine` 使用 `PageSegMode.SparseText` 掃描 UI 截圖，過濾低信心字詞，再交給 `TesseractBlockSegmenter.BuildBlocks()` 組段。

`CjkOnnxOcrEngine` 使用執行目錄下 `ocrmodels/onnx`。`shared` 放 det/cls，`cjk` 或 `korean` 放 rec/dict。

## 翻譯

對外入口是 `TranslationService`。輸入是 OCR 後的 `OcrTextBlock` 清單、來源語言、目標語言與 API key，輸出是 `TranslatedBlock` 清單與偵測語言。

Provider 由 `SettingsService.Instance.Current.Provider` 選擇：

- `GTranslateProvider` 包裝 Google、Google2、Bing、Microsoft、Yandex。
- `DeepLProvider` 直接呼叫 DeepL HTTP API，需要 API key。

`TranslatedBlock` 保留原文、譯文、bounds、line bounds、背景色、文字色。顏色通常由 `MainWindow` 在翻譯後取樣補上。

## TTS

`TtsService` 負責朗讀文字。它使用多個 GTranslate TTS 來源依序 fallback，將音訊寫到 temp mp3，再用 WPF `MediaPlayer` 播放。

新的朗讀會取消前一次朗讀。修改時要保留 cancellation 行為，避免多段聲音重疊或暫存檔被競爭讀寫。

## 設定與語言資料

`SettingsService` 是 singleton，讀寫執行目錄的 `appsettings.json`。`AppSettings` 包含快捷鍵、來源語言、目標語言、翻譯 provider、API key 與 theme。

`LanguageData` 定義 source language、OCR source language、target language、provider 清單，以及 source/target code 映射與 fallback。

設定 schema 改動要考慮既有 `appsettings.json` 反序列化 fallback，不要讓舊使用者設定無法讀取。

## 主題

`ThemeService.Apply()` 依設定套用 light/dark resource dictionary。主題資源在 `src/OverTranslate/Themes/`。

## 更新

`UpdateService` 使用 Velopack `UpdateManager` 與 GitHub source。`CheckAsync()` 只在已安裝的 Velopack build 中回傳更新資訊；開發環境通常回傳 `null`。

`UpdateWindow` 負責提示並呼叫 `DownloadAndApplyAsync()`。
