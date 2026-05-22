# Runtime Flow

本文件描述 OverTranslate 啟動與截圖翻譯流程。若修改熱鍵、截圖、overlay、toolbar 或非同步 session 行為，先讀這份。

## 啟動流程

`App.xaml.cs` 是應用入口。`Main()` 先初始化 Velopack，再建立 WPF `App` 並進入 `Run()`。

`OnStartup()` 主要負責：

- 註冊 UI、domain、task 例外記錄。
- 使用 named `EventWaitHandle` 保證單例執行。
- 如果已有執行個體，通知既有個體顯示翻譯視窗後退出。
- 啟動背景 thread 監聽後續啟動請求。
- 套用目前 theme。
- 建立 `MainWindow` 並呼叫 `InitializeApp()`。
- 非同步檢查更新。

`ShutdownMode` 是 `OnExplicitShutdown`，應用主要以系統匣背景常駐。

## MainWindow 職責

`MainWindow.xaml.cs` 是核心協調器，不是一般使用者會操作的主畫面。它持有：

- `NotifyIcon`
- `GlobalHotkey`
- `ScreenCaptureWindow`
- `OverlayWindow`
- `ToolbarWindow`
- `OcrService`
- `TranslationService`

它也保存最近一次 OCR/翻譯結果與 selection bounds，供重翻譯和開啟獨立翻譯視窗使用。

## 截圖翻譯流程

1. `InitializeApp()` 初始化系統匣、註冊快捷鍵、顯示啟動 balloon。
2. `RegisterHotkey()` 依 `SettingsService.Instance.Current` 註冊全域快捷鍵。
3. 快捷鍵觸發 `OnHotkeyPressed()`。
4. 如果 overlay、toolbar 或 capture window 已存在，快捷鍵會關閉目前流程。
5. 否則擷取所有螢幕合併範圍，建立 `ScreenCaptureWindow`。
6. 使用者框選後，`EnterOverlayState()` 建立空 overlay 與 toolbar。
7. 使用者在 toolbar 按翻譯後，`OnTranslateRequested()` 開始 OCR/翻譯。
8. `ScreenCaptureWindow.PrepareForTranslation()` 準備裁切圖。
9. `OcrService.RecognizeAsync()` 回傳 `OcrTextBlock` 清單。
10. `TranslationService.TranslateAsync()` 回傳 `TranslatedBlock` 清單。
11. `MainWindow` 從裁切圖取樣背景色與文字色。
12. `OverlayWindow.UpdateBlocks()` 將翻譯結果覆蓋回原位。

## 非同步 session guard

`_selectionSessionId` 用來避免過期非同步結果更新錯誤視窗。每次關閉或重建 selection session 時會遞增。`IsCurrentSelectionSession()` 會同時檢查 session id、toolbar instance、capture window instance。

修改 OCR/翻譯非同步流程時，不要移除這個概念。否則使用者快速關閉、重開或連續翻譯時，舊結果可能更新到新 overlay。

## 關閉流程

`CloseAll()` 會遞增 session id，解除 overlay closed handler，依序關閉 overlay、toolbar、capture window。這個順序避免 overlay closed handler 和主動 teardown 互相重複處理。
