# AI Context 入口

你是正在處理 OverTranslate 的 AI coding agent。把本文件當作專案 context 的第一層路由。先只讀本文件；只有在已經取得明確使用者任務後，才依照下方路由選讀 `docs/ai/` 內的其他文件。

## Context 載入規則

1. 先讀本文件。
2. 如果目前還沒有使用者任務，停在這裡並等待任務。
3. 如果已經有使用者任務，使用下方任務路由，只讀與任務直接相關的文件。
4. 如果使用者是在你讀完本文件後才補充任務，回到任務路由再選讀相關文件。
5. 不要預先讀完整個 `docs/ai/`。
6. 開始編輯前讀 `agent-rules.md`；即使任務很小，也要遵守其中的修改邊界。

## 任務路由

- 啟動、單例、系統匣、全域快捷鍵、截圖、overlay 或 toolbar 流程：讀 `runtime-flow.md` 和 `ui-map.md`。
- OCR、文字區塊聚合、Tesseract、RapidOcrNet、翻譯 provider、TTS、設定、主題或更新：讀 `subsystems.md`。
- WPF 視窗、事件串接、視窗顯示順序或關閉/teardown 行為：讀 `ui-map.md`。
- 測試、建置、發布或 Velopack：讀 `testing-and-release.md`。
- 如果任務不明確命中任何路由：讀 `project-overview.md`，再回到本路由表。
- 如果同時命中多個路由，只讀命中的文件。不要因為涉及多個區域就讀完整個 `docs/ai/`。

## 排除的 Context

不要把以下路徑作為主要架構 context：

- `.omx/`
- `artifacts/`
- `src/**/bin/`
- `src/**/obj/`
- `tests/**/bin/`
- `tests/**/obj/`

## 文件維護規則

- 文件只記錄目前存在的架構，不記錄尚未實作的計畫。
- 修改重要流程或資料流時，同步更新對應的 `docs/ai/` 文件。
- 保持 context 依主題拆分，不要把所有內容合併成單一大型 Markdown。
