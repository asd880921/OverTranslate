# TranslationHarness

Issue #47 的 L0／Phase 0 可重播翻譯量測工具。它直接呼叫指定 provider，不啟動 WPF、OCR、updater，
也不建立或發布安裝包。現階段只提供 Microsoft baseline；本機 NMT runtime 要在後續 PoC 以相同
corpus 與報告格式加入，才能作公平比較。

## Corpus

JSON corpus 必須有固定 `corpusId`／`corpusVersion`、人工參考譯文，並明確標記
`deidentified: true`。工具會拒絕未去識別化、缺參考譯文或 case id 重複的輸入。

`corpora/example.v1.json` 只示範格式，不是真實使用 corpus，也不能拿來通過 Phase 0 決策門。
正式 corpus 至少要涵蓋字幕、遊戲對話、UI／提示卡、短句、斷句不完整、專有名詞、OCR 小錯字
與混合語言。建立後不要覆寫既有版本；內容改變時建立新版本，保留舊檔供重播。

先做不連網驗證：

```powershell
dotnet run --project tools/TranslationHarness/TranslationHarness.csproj -- `
  --corpus tools/TranslationHarness/corpora/example.v1.json `
  --validate-only
```

## Microsoft baseline

量測會直接使用 Microsoft provider，不使用 hedging/fallback。每個語言方向與 batch size 分開報告
first translation、暖機後 p50／p90／p95／max、程序 CPU、working set 與成功率。單次失敗會記錄錯誤
並繼續量測，不會讓整份報告消失。報告也保留第一輪
候選譯文，供人工盲測工具或人工檢查使用。

```powershell
dotnet run --project tools/TranslationHarness/TranslationHarness.csproj -- `
  --corpus path/to/corpus.v1.json `
  --hardware-profile dev-machine `
  --provider microsoft `
  --runs 10 `
  --warmup-runs 1 `
  --batch-sizes 1,2,4,8
```

預設報告寫入 git ignored 的 `artifacts/translation-harness/`。要比較不同硬體或 provider，請保留同一
corpus 版本、runs、warmup 與 batch sizes，並為硬體使用穩定名稱。工具會自動記錄 CPU、RAM、OS、.NET、
程序架構及 SSE2／AVX2／AVX-512 支援。

這個 harness 量到 provider 呼叫的延遲與自身程序資源，還沒有同時執行 OCR，因此不能單獨證明
OCR cadence 或遊戲／影片沒有退化；那些必須在後續干擾實驗以同一 corpus 另行記錄。
