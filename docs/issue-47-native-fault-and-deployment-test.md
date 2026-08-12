# Issue #47 native 故障與部署保護測試

測試日期：2026-08-12

硬體：AMD Ryzen 7 3700X、32 GiB RAM、Windows x64

runtime：Bergamot `9271618e…`、Marian `2781d735…`、OpenBLAS 0.3.33、beam=1

## 結論

Phase 1 的最小 native 故障／部署保護可判定通過。Bergamot 不再載入 harness 主程序；父程序先做
部署 preflight，native 初始化與推論固定由 worker process 執行。缺檔與不支援 CPU 在進入 native 前
失敗，損壞模型與 native fast-fail 則只終止 worker，父程序可回報並在修復檔案後建立新 worker 重試。

這是 Phase 1 PoC 的故障邊界，不代表已完成正式產品 UI、模型下載器或 installer。

## 基線發現

原本 in-process provider 能以 managed exception 處理缺 wrapper DLL 與缺 OpenBLAS，但以下兩種故障
會讓 Marian 直接終止整個 harness，C++ `catch` 與 .NET `catch` 都無法可靠隔離：

- config 引用不存在的 model：程序 exit 1，無可用錯誤訊息。
- config 引用非 Bergamot 格式的 model：程序 exit 1，無可用錯誤訊息。

因此採用 worker process 不是預防性的架構擴張，而是故障注入後的必要結果。

## 已完成保護

- 啟動前要求 Windows x64 build 所需的 AVX2；不支援時回報 `PlatformNotSupportedException`。
- 啟動前驗證 wrapper DLL、同目錄 `libopenblas.dll`、model config 與 config 引用的 model、vocab、
  lexical shortlist、sentence-splitting prefix 檔案，錯誤包含實際絕對路徑。
- worker 以 redirect stdin/stdout 的 JSON-lines protocol 收發 batch，不讓 C++ ABI 或 native ownership
  跨程序。
- worker 初始化失敗、非零退出、stdout 中斷與無效 response 都轉成可診斷的 managed error。
- request timeout／取消會終止整個 worker process tree；已終止的 provider 不會被誤用，必須重建。
- benchmark CPU 與 working set 合計父程序與 worker，隔離後仍保留可比較的資源數據。

## 故障注入結果

| 情境 | 結果 | 父程序安全 |
|---|---|---|
| wrapper DLL 不存在 | preflight 指出缺少 native library 與絕對路徑 | 是 |
| `libopenblas.dll` 不存在 | preflight 指出缺少 runtime dependency 與絕對路徑 | 是 |
| model config 不存在 | preflight 指出缺少 config 與絕對路徑 | 是 |
| config 引用的 model 不存在 | preflight 指出缺少 `models artifact` 與絕對路徑 | 是 |
| model 是非 Bergamot 文字檔 | worker fast-fail `-1073740791`；父程序回報 model/runtime 損壞或不相容 | 是 |
| 模擬 AVX2 不支援 | 進 native 前回報此 build 需要 AVX2 x64 CPU | 是 |
| request timeout = 1 ms | 第一個 request timeout 後終止 worker；後續 request 明確要求重建 provider | 是 |
| 故障後重新載入正確 model | 新 worker 正常初始化並完成 20/20 EN→ZH-HANT | 是 |

不支援 CPU 測試使用 deterministic override，因 Ryzen 7 3700X 本身支援 AVX2；測試覆蓋的是正式
preflight 會走的同一分支，沒有在支援 AVX2 的實機上執行非法指令。

## 四方向 worker smoke

所有方向均使用 batch=4、20 個固定 corpus cases；direct 方向 5/5 requests，pivot 方向 5/5 requests，
全部成功且第一筆翻譯成功。

| 方向 | 初始化 | p50 | p95 | 結果 |
|---|---:|---:|---:|---|
| EN→ZH-HANT | 259 ms | 38 ms | 42 ms | 5/5 |
| JA→EN→ZH-HANT | 512 ms | 67 ms | 89 ms | 5/5 |
| KO→EN→ZH-HANT | 486 ms | 64 ms | 78 ms | 5/5 |
| ZH-HANT→EN | 332 ms | 36 ms | 46 ms | 5/5 |

修正跨程序資源計數後的重播結果：EN→ZH-HANT working set 369.7→370.8 MiB、10/10；
JA pivot working set 785.8→787.8 MiB、10/10。數量級與先前 in-process 穩定性測試一致，證明報告
沒有因 worker 隔離而漏算模型記憶體。

## 自動測試

`TranslationHarness.Tests` Release：14 passed、0 failed、0 skipped。新增測試涵蓋缺 wrapper、缺
OpenBLAS、缺 config、缺 model／vocab artifact 與不支援 AVX2；既有 corpus 與 benchmark 測試全部
維持通過。產品 `OverTranslate.Tests` Release：318 passed、0 failed、2 skipped；略過的是既有 OCR
長時間 concurrency tests，與本次 native worker 變更無關。

## Phase 1 判定

- `[x]` 缺 DLL 可安全診斷。
- `[x]` 缺模型／supporting artifact 可安全診斷。
- `[x]` 損壞模型／native crash 不會終止父程序。
- `[x]` 不支援 CPU 在載入 native 前可安全診斷。
- `[x]` in-process 無法隔離的故障已改用 worker process。

若授權回覆沒有新增限制，Phase 1 技術條件已全部完成，可進入 Phase 2 正式產品整合。
