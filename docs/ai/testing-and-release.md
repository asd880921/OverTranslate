# 測試與發布

本文件描述目前測試入口、常用驗證與發布流程。

## 測試專案

測試專案位於 `tests/OverTranslate.Tests/OverTranslate.Tests.csproj`，使用 xUnit，引用主專案。

目前測試重點：

- `OcrServiceTests.cs`：OCR 語言支援、router 行為、CJK model key。
- `OcrTextBlockGrouperTests.cs`：文字區塊聚合規則。
- `TesseractBlockSegmenterTests.cs`：Tesseract word segmentation。
- `SingleLineOverlayLayoutTests.cs`：單行 overlay layout 計算。

## 常用驗證指令

```powershell
dotnet build .\src\OverTranslate.slnx
dotnet test .\tests\OverTranslate.Tests\OverTranslate.Tests.csproj
```

修改 OCR grouping、segmentation 或 overlay layout 時，優先跑相關測試。修改 WPF UI 行為時，現有自動測試可能不足，需要人工啟動應用驗證。

## 發布流程

發布入口：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1
```

只重新打包已 publish 內容：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 -SkipPublish
```

`publish-velopack.ps1` 會：

- 讀取 csproj 的 `<Version>`，除非呼叫時指定 `-Version`。
- 清空 publish 目錄。
- 執行 `dotnet publish`。
- 從 publish 輸出移除 `appsettings.json`。
- 呼叫 Velopack CLI `vpk pack`。
- 將產物輸出到 `artifacts/releases`。

## 發布注意事項

`appsettings.json` 不應放進 publish 輸出，避免更新時覆蓋使用者既有設定。修改 csproj 或發布腳本時要保留這個行為。

`artifacts/` 是打包產物，不應作為程式架構理解來源。
