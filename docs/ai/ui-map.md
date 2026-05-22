# UI Map

本文件描述主要 WPF 視窗與事件關係。修改視窗互動、顯示順序或關閉流程時先讀這份。

## 視窗職責

- `MainWindow`：背景協調器，初始化系統匣與快捷鍵，串接截圖、overlay、toolbar、OCR、翻譯。
- `ScreenCaptureWindow`：顯示全螢幕截圖，取得使用者框選範圍，提供裁切 bitmap。
- `OverlayWindow`：在選取區域上顯示處理狀態與翻譯泡泡。
- `ToolbarWindow`：框選後的工具列，送出翻譯、開啟翻譯視窗、切換泡泡可見性、關閉流程。
- `TranslationWindow`：獨立文字翻譯視窗，可編輯文字、重新翻譯、播放 TTS。
- `SettingsWindow`：設定快捷鍵、語言、provider、API key、theme。
- `TrayMenuWindow`：系統匣右鍵選單。
- `ToastWindow`：短暫提示錯誤或狀態。
- `UpdateWindow`：新版本提示與套用更新。
- `AboutWindow`：關於視窗。

## 主要事件關係

`MainWindow` 建立 `ToolbarWindow` 後訂閱：

- `TranslateRequested`：執行 OCR 與翻譯。
- `OpenWindowRequested`：把目前 OCR/翻譯內容帶到 `TranslationWindow`。
- `CloseAllRequested`：關閉 overlay、toolbar、capture window。
- `BubblesVisibilityChanged`：切換 overlay 翻譯泡泡顯示。

`TrayMenuWindow` 由 `MainWindow` 建立，事件會導向開翻譯視窗、開設定、開關於、退出。

`App` 監聽第二次啟動請求，會呼叫 `ShowOrActivateTranslationWindow()`，重用或建立 `TranslationWindow`。

## Overlay 相關注意事項

`OverlayWindow` 和 `ScreenCaptureWindow` 都涉及 topmost 與 z-order。`MainWindow.ShowOverlay()` 在重翻譯時會盡量更新既有 overlay，而不是關掉重開，以避免 z-order race。

修改 overlay 顯示策略時，要同時檢查 toolbar owner、capture window owner、closed handler 與 `CloseAll()` teardown。

## 獨立翻譯視窗

從 toolbar 開啟 `TranslationWindow` 時，`MainWindow` 會把 `_lastOcrBlocks` 與 `_lastColoredBlocks` 串成文字，呼叫 `SetContent()` 或建立新視窗，然後關閉 overlay 流程。

`TranslationWindow` 自己持有翻譯與 TTS service。它的操作不依賴目前截圖 overlay 是否仍存在。
