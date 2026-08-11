# TranslationHarness

Issue #47 的 L0／Phase 0 可重播翻譯量測工具。它直接呼叫指定 provider，不啟動 WPF、OCR、updater，
也不建立或發布安裝包。工具提供 Microsoft baseline 與 Bergamot native PoC provider，兩者使用
相同 corpus 與報告格式作公平比較。本機 provider 只供技術評估，不代表模型授權或正式部署已通過。

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

## Bergamot native PoC

`native/bergamot` 是最小 C ABI wrapper；介面不跨 DLL 暴露 C++ ABI、STL 或物件 ownership。
`BergamotTranslationProvider` 會載入指定 DLL 與 model config 一次，後續 batch 沿用同一模型，並將
provider 初始化時間獨立寫入 report。原生推論開始後目前無法硬中止；取消可阻止尚未開始的工作，
過時結果仍由呼叫端丟棄。若後續需要可靠硬 timeout／native crash 隔離，應比較獨立 worker process。
`bergamot-pivot` 會在同一 native service 載入兩個 model，完整呼叫 Bergamot `pivotMultiple`；初始化、
CPU、working set 與延遲均包含兩段，不可和 direct 結果混合平均。

wrapper 的 CMake 專案需要另外提供 Bergamot checkout：

```powershell
cmake -S tools/TranslationHarness/native/bergamot `
  -B artifacts/nmt-poc/overtranslate-bergamot-build `
  -DBERGAMOT_SOURCE_DIR=C:/path/to/bergamot-translator
cmake --build artifacts/nmt-poc/overtranslate-bergamot-build --config Release `
  --target overtranslate_bergamot
```

Bergamot checkout、Marian、BLAS 與 Windows compiler flags 必須另外固定並記錄；不要把未確認授權的
模型或 native binaries 提交、放入 installer 或上傳至 Release。模型 config 內的相對路徑由目前工作
目錄解析，因此執行 benchmark 時應切到模型目錄，或使用全部為絕對路徑的 config。

```powershell
dotnet run --project C:/path/to/OverTranslate/tools/TranslationHarness/TranslationHarness.csproj -- `
  --corpus C:/path/to/OverTranslate/tools/TranslationHarness/corpora/en-zh-hant.v1.json `
  --hardware-profile dev-machine `
  --provider bergamot `
  --native-library C:/path/to/overtranslate_bergamot.dll `
  --model-config config.yml `
  --runs 10 `
  --warmup-runs 1 `
  --batch-sizes 1,2,4,8
```

英文 pivot：

```powershell
dotnet run --project C:/path/to/OverTranslate/tools/TranslationHarness/TranslationHarness.csproj -- `
  --corpus C:/path/to/corpus.json `
  --hardware-profile dev-machine `
  --provider bergamot-pivot `
  --native-library C:/path/to/overtranslate_bergamot.dll `
  --model-config C:/path/to/source-to-en.yml `
  --pivot-model-config C:/path/to/en-to-target.yml
```

這個 harness 量到 provider 呼叫的延遲與自身程序資源，還沒有同時執行 OCR，因此不能單獨證明
OCR cadence 或遊戲／影片沒有退化；那些必須在後續干擾實驗以同一 corpus 另行記錄。
