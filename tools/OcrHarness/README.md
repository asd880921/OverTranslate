# OcrHarness

OverTranslate 的 **OCR + 分組 + 翻譯** 離線測試工具。給人或 AI 在改動 OCR / 文字分組邏輯後，**不必啟動 WPF app、不必手動框選**，就能在真實截圖上重現並驗證結果。

## 它做什麼

把一張（或多張）截圖 PNG 餵進**真實 pipeline**：

```
OnnxOcrEngine（真 ONNX OCR）
  → OcrTextBlockGrouper（同行/換行分組，多數 bug 出在這）
  → GTranslateProvider(MicrosoftTranslator)（EN → 繁中，免金鑰、走網路）
```

然後印出：
- 每個**分組後的區塊**（送去翻譯的單位）的 `bounds`、行數、原文 —— 直接看出「同一行有沒有被切開」或「不同元件有沒有被誤併」。
- 每個區塊的 **Microsoft 翻譯結果**。

> 為什麼用螢幕截圖？OverTranslate 本身就是擷取**螢幕像素**做 OCR（不是讀網頁 DOM），所以截圖是與正式流程**完全相同**的輸入。

## 使用方式

前置：**先關閉正在執行的 OverTranslate app**，否則 `OverTranslate.exe` 被鎖、無法建置。

```bash
# 1) 建置（會一併把 ocrmodels 複製到輸出）
dotnet build tools/OcrHarness/OcrHarness.csproj -c Debug

# 2) 對截圖跑 OCR + 分組 + 翻譯
tools/OcrHarness/bin/Debug/net8.0-windows10.0.26100.0/win-x64/OcrHarness.exe 圖1.png 圖2.png
```

目前固定以來源語言 `EN`、目標 `ZH-HANT`、Microsoft provider 執行（測英翻中用）。要換語言/provider 直接改 `Program.cs`。

### 量測模式（不翻譯、不連網）

改動 OCR 前後要有數字才知道有沒有變好，這兩個模式就是拿來產生那些數字的：

```bash
# 同一張圖掃過每個偵測尺寸（0.30–1.00，步進 0.05），看哪些尺寸讀得到、讀成什麼
OcrHarness.exe --scale-sweep 圖.png [更多.png ...]

# 同一張圖、同一個尺寸，只換辨識模型（cjk vs korean）
OcrHarness.exe --compare-models 圖.png [更多.png ...]

# 同一張圖、app 會用的尺寸，只換送進偵測器前加的白邊（0–96）
OcrHarness.exe --pad-sweep 圖.png [更多.png ...]
```

`--pad-sweep` 的白邊不是「多一圈留白」而已：它參與 `AlignForDetector` 的對齊算式，也算進
`ImgResize` 的長邊上限，所以白邊越寬、偵測器看到的文字越小。實測 0/8/16/24/32/50/64/96，
**50 在字幕條帶、遊戲面板、小截圖三類上都是最高分，而且兩側都比它差** —— 是峰值不是地板，
調大不會比較保險。細節與數字記在 `OnnxOcrEngine.DetectorPaddingOverride`。

`--scale-sweep` 會一併印出 `RealtimeDetectorSize` 對該尺寸區塊會挑的 primary 與 fallback，
所以掃描結果可以直接對照 app 真正會用的尺寸來讀。它走的是主專案的 `OnnxOcrEngine`，
量到的就是 app 實際在跑的東西。每一列都帶 `chars=` 與耗時：命中率與成本要一起看，
只挑命中率最高的尺寸會付出不成比例的代價（#22 量到 0.95 的成本是 0.5 的四倍）。

列上的框數是**即時翻譯真的會採用的**：塌陷框（高度達區塊 90%）與過短讀取在
`RealtimeTranslationSession` 被丟掉，掃描一併套用，丟掉幾個記在 `dropped=`。
不套用的話，掃描會把 app 其實判定為「讀不到」的尺寸算成成功 —— 實測 v5 有 5 張幀
正好落在這個差別上。

### 拆開「比例」與「絕對尺寸」

```bash
# CSV：同一段字幕切成多種留白，每種留白再掃過每個偵測尺度
OcrHarness.exe --margin-scale-grid 全螢幕.png [更多.png ...]
```

`--margin-series` 是「固定尺寸、變留白」，`--scale-sweep` 是「固定留白、變尺寸」，兩個都拆不開變因 ——
而且 `region-` 那幾集全是約 1820 寬，比例與絕對尺寸在資料裡本來就綁死。把整張螢幕依不同留白裁切，
同一組字就能在同一個比例下對應到不同的絕對尺寸，這樣才拆得開（issue #89）。

每一列帶 `detect`、`glyphDetectorPx`（字在偵測器空間裡的高度）、`occupancyPct`（文字聯集佔 ROI 的比例），
就是為了讓「哪個變因決定準確度」由資料回答。輸出**刻意用 CSV**：其他掃描為了對齊而補空白，
`chars= 78` 這種右對齊欄位在 #84 曾經讓解析器安靜地漏掉所有低分列，而那正是最關鍵的一群。

判讀這個模式踩過三個坑，都會給出漂亮但錯誤的曲線，接手前務必看：

**1. 只試一種成功判準。** 用「字數等於該圖最大值」這種最嚴的判準，會量出一條單調上升到 40–50px
的漂亮曲線；放寬到 0.85 之後，同一批資料在 40px 以上反而下降。多試幾種。

**2. 只用一個來源尺度。** 在同一批原圖上，`glyphDetectorPx` 與**實際縮放比**是綁死的——
降低 fraction 會同時讓字變小、讓圖變糊。這樣量出來會誤以為「字高就是那個變因、地板在 15px」。
把來源圖先縮到 70/50/35% 再跑（字真的變小，但重採樣很溫和）就會拆開：同樣 0–12px，
原尺寸只有 37%，縮到 35% 的卻有 76%。**兩個變因都重要而且會疊加，沒有單一變因的規則。**

**3. 每個 set 用自己的參考值。** 縮小版如果整體讀得差，它的最大值就低，等於用被降低的標準評分。
跨尺度比較時，全部要拿**原尺寸**讀到的最好結果當共同標準。

結論記在 `RealtimeDetectorSize` 的註解，不要沿用 PP-OCRv5 時代「目標約 30px」的說法。

### 稽核信心過濾丟掉了什麼

```bash
# 每一張圖：信心過濾丟掉哪些碎片，其中哪些「本來會被併進一個夠長的行」
OcrHarness.exe --reject-audit 圖.png [更多.png ...]
```

`RejectUnconvincingBlocks` 跑在 `OcrTextBlockGrouper` **之前**，所以一行字被偵測器橫向切開時，
低信心的尾段會在合併前就消失（issue #85）。這個模式拿**未過濾**的框跑一次真正的分組器，
逐筆報告被丟掉的碎片會不會落進一個 10 字以上的行 —— 那正好就是「窄化規則」會放行的集合，
可以在寫規則**之前**先讀，而不是寫完才發現。

判讀時兩類要分開看：**真的尾巴**（併進去那行才完整）與**同排雜訊**（風景框剛好落在字幕那一列，
併進去會污染句子，而且合併後的框可能被判成 collapse 把整句帶走 —— 那正是目前這個順序存在的原因）。

實測 163 張讀到內容的字幕圖：丟掉 54 筆，只有 **1** 筆會被併進真實行（信心 0.79），其餘 53 筆
都是孤立雜訊。0.6%，與 #85 現場量到的 2/307 吻合。**不足以動那段規則**，細節記在
`OcrService.RejectUnconvincingBlocks`。

### 換偵測模型來掃

```bash
# 用別的偵測模型跑同一份掃描（省略 --det 就是出貨的那顆）
OcrHarness.exe --scale-sweep --det 別的det.onnx:half 圖.png [更多.png ...]
```

`:imagenet`（PP-OCRv5 的匯出設定，省略時的預設）與 `:half`（RapidOcrNet 的 PP-OCRv6 預設，
127.5/127.5）指的是模型訓練時的像素正規化。**這不是可調參數**：用錯的統計量餵模型，量到的
就不是那顆模型，而且失敗方式是安靜的 —— 它只是讀得比較少。換模型時務必連正規化一起換。

只換偵測模型、辨識模型維持不動，是 #22 訂下的規矩：兩顆一起換，效果就分不開了。

判讀時務必先把「畫面上根本沒有文字」的圖挑掉 —— 在那些圖上讀不到是正確行為，計入會把
誤報算成成功。實務做法是只採計讀到 10 個字元以上的結果。

升級模型或函式庫時，先用同一批圖跑一次存起來當基準，改完再跑一次比對；
PP-OCRv6 那次就是靠這個確認 `RapidOcrNet 1.0.1 → 3.0.0` 的輸出逐字元相同。

## 產生測試截圖

`capture.ps1` — 用 .NET `CopyFromScreen` 把螢幕區域存成 PNG（模擬使用者框選）：

```powershell
# 全螢幕
./capture.ps1 -Out full.png
# 指定區域 X Y W H（可做「單行＋上下殘缺」這類情境）
./capture.ps1 -Out strip.png -X 795 -Y 632 -W 960 -H 30
```

`testpage.html` — 一頁可控的英文版面（大標題、多行內文、相鄰按鈕、等寬單行），方便重現特定情境。用瀏覽器開啟後再 `capture.ps1` 擷取。

典型流程：`Start-Process msedge <url>` → 等載入 → `capture.ps1` 擷取 → `OcrHarness.exe` 跑。

## 判讀重點

- **同一句被拆成多塊** → 同行合併失敗（看 `CanJoinSameLine` / `MergeSameLineFragments`）。
- **兩個獨立元件被併成一塊** → 合併過頭（多半是垂直交疊判斷）。
- `grouped blocks: 0` 通常代表截圖太薄/對比太低，OCR 沒辨識到 —— 重抓、給足垂直邊距即可。

## 備註

- 需要 ONNX 模型（建置時從 `src/OverTranslate/ocrmodels` 連結複製到輸出）。
- 翻譯步驟需連網；OCR 步驟純離線。
- 這是開發/除錯工具，不隨 app 發佈。
