# Issue #47 Phase 0／1 事前門檻

這份文件在候選 NMT 結果產生前固定量測順序、通過條件與報告格式。若要調整門檻，必須先說明
原因並留下 issue comment，不以已看到的結果倒推標準。

## 語言方向

1. `EN → ZH-HANT`
2. `JA → ZH-HANT`
3. `KO → ZH-HANT`
4. `ZH-HANT → EN`

每個方向依序進行；direct、pivot 與不同硬體分開報告，不混合平均。

## 暫定通過門檻

- direct 單 block 暖機後 p95 不超過 750 ms，4 blocks pass p95 不超過 1,000 ms，正常輸入
  max 不超過 2,000 ms。
- JA／KO 經英文 pivot 時，完整 pivot pass 暖機後 p95 不超過 1,500 ms，max 不超過 3,000 ms。
- 模型 cold load 不超過 5 秒，並可在 session 開始前預載；每句不得重新載入模型。
- 量測區段平均 CPU 不超過全機 50%；direct 增量 working set 不超過 512 MiB，雙模型 pivot
  不超過 768 MiB。
- 候選成功率至少 99%；損壞模型、缺檔、取消、load/unload 與 shutdown 不得造成 process crash。
- 與 OCR 同時執行時，OCR p95／實際 cadence 退化不超過 15%，被翻譯程式不可有可觀察卡頓。
- 人工盲測不得出現反義、否定詞翻反或關鍵資訊漏譯；整體可接受比例至少 85%，且不得比
  Microsoft baseline 低超過 10 個百分點。
- runtime、模型、下載／再散布授權或 Windows x64／CPU 部署任一無法得到可保存的明確結論，
  即使效能通過也停止正式整合。

## 提供維護者的結果

每輪除 JSON artifact 外，另提供可直接閱讀的 Markdown 表格：

- 每個語言方向分列 first translation、warm p50／p90／p95／max。
- 1／2／4／8 blocks batch 分開列出。
- 同表列出 CPU、working set、成功率，以及相對 Microsoft baseline 的毫秒與百分比差異。
- 人工品質檢查附上候選譯文及該組實際耗時，讓準確度與等待體感一起判斷。

## 發布安全

目前只允許 L0：不使用 PackId、channel 或 update source；不建立 package／GitHub Release；不接觸
`win`／`beta` feed 或正式使用者資料。
