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
tools/OcrHarness/bin/Debug/net8.0-windows10.0.17763.0/win-x64/OcrHarness.exe 圖1.png 圖2.png
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
