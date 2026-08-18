# WgcProbe

即時翻譯**擷取後端**的離線量測工具。用來回答那些「只能在真機上量、不能靠文件推論」的問題：這台機器的 Windows.Graphics.Capture 能做什麼、視窗擷取的座標對不對、讀回一幀多少錢，以及最關鍵的——**OverTranslate 自己的字幕層會不會出現在擷取到的畫面裡**。

## 為什麼需要它

#94 卡住的原因不是程式難寫，是那個問題沒辦法直接問：答案同時取決於 Windows build、視窗的透明實作方式、以及擷取來源是什麼，而開發機剛好落在舊做法有效的那一側。所以這裡把它變成一個可以重跑的量測，**用數字回答，在你手上這台機器上**。

它走的是主專案 `OverTranslate.Services.Realtime.Capture` 的真實程式碼，不是另一份複製品。

## 使用方式

```bash
dotnet build tools/WgcProbe/WgcProbe.csproj -c Debug
cd tools/WgcProbe/bin/Debug/net8.0-windows10.0.26100.0/win-x64
```

### `caps` — 這台機器有什麼

```bash
WgcProbe.exe caps
```

```
OS                     10.0.26200.0
capture supported      True     WGC 本身（1903 起，且系統願意提供）
borderless property    True     IsBorderRequired 存在（Win11 21H2 起）
window exclusion list  True     2026 年的 SetWindowExclusionList
display capture session True    IDisplayGraphicsCaptureSession
```

**不要只用 build number 判斷。** exclusion list 掛在 `IDisplayGraphicsCaptureSession` 上而不是 `GraphicsCaptureSession`——只問後者會得到 False 而誤判成「這台機器沒有」。

### `overlay` — go/no-go

```bash
WgcProbe.exe overlay [x y w h] [輸出目錄]
```

自己建一個來源視窗，再蓋一個與 `RealtimeBlockWindow` 同配方的字幕層（`AllowsTransparency` / topmost / click-through / 每像素 alpha），填成螢幕上不會出現的洋紅色，然後兩種擷取各跑一次同一塊區域，報告疊圖佔比。

```
overlay in desktop grab   98.5%
overlay in window capture  0.0%
PASS: the overlay is absent from the window capture
```

兩個視窗都是 probe 自己的，這是刻意的：第一版拿桌面上的 Edge 當來源，跑到一半視窗被拖到另一個螢幕，量測直接失效。要對真實應用程式測請用 `region`。

### `exclusion` — 整個螢幕的 go/no-go

```bash
WgcProbe.exe exclusion [x y w h] [輸出目錄]
```

跟 `overlay` 同一組視窗，改問另一半的問題：**擷取整個螢幕、並把字幕層放進 session 的 window exclusion list，那塊區域讀回來的是什麼？**

這一題沒有文件可查，而整條「整個螢幕」的路成不成立全看它。字幕層本來就蓋在原文上，如果排除之後那塊變成純黑，OCR 讀到的就是黑色，等於什麼都沒解決。

```
overlay on screen      98.5%
overlay in capture      0.0%
black in capture        0.6%
source content         98.1%
backend                hmonitor=10073 received=5 read=1 avgReadback=24.7ms discardedBeforeExclusion=0 exclusionUpdates=1 excluded=1 rebuilds=0
GO: the excluded region shows the source window underneath the overlay
```

`source content` 是關鍵那一行：**露出來的是底下來源視窗的內容，不是黑洞**（`black` 那 0.6% 是來源視窗自己的黑字）。

走的是 `WgcMonitorCaptureBackend` 本人，不是這裡另外搭的鏈路——所以連同「不讀 exclusion 生效前的那幾幀」與「從螢幕原點裁切」一起被量到。後者在主螢幕左邊的顯示器上會安靜地錯掉，自足測試看不出來。

### `list` / `window` / `region` — 對真實應用程式

```bash
WgcProbe.exe list
WgcProbe.exe window <標題片段 | handle> [輸出目錄]
WgcProbe.exe region <x> <y> <w> <h> [輸出目錄]
```

**先 `list` 拿 handle，再用 handle 擷取。** 用標題片段找很容易中錯：終端機視窗的標題含有你剛打的命令列，所以搜尋 `"Crab Champions"` 會先命中你自己那個終端機，然後很順利地擷取它自己、印出看起來合理的幾何、寫出一張終端機的截圖。`list` 已經把本行程的主控台排掉，但用 handle 仍然是最不會出錯的方式。

`window` 直接指定視窗；`region` 照 session 的方式（`SourceWindowResolver` 的九點投票）從一塊螢幕矩形反推來源視窗。兩者都會印出幾何比對、以 250ms 節奏量 20 次 `GrabRegion` 的耗時，並寫出一張 PNG。

幾何那三行是重點：

```
GetWindowRect              -9,-9  2578x1458
extended frame bounds        0,0  2560x1440
itemSize                          2560x1440   ← 跟 extended frame bounds 一致
```

差的 9px 是 Vista 以後的隱形調整邊框。猜錯就是整區固定偏移，足以把字幕上緣切掉。

**遊戲、影片、多螢幕、mixed DPI、Windows 10 就是靠這兩個指令去測的**——自足測試涵蓋不到，只能到真實環境跑。

### `border` — 擷取指示邊框

```bash
WgcProbe.exe border [x y w h] [輸出目錄]
```

擷取開始前後各抓一次視窗邊緣，量變動比例，再嘗試 `RequestAccessAsync(Borderless)` + `IsBorderRequired = false` 並重測。

```
ring changed on capture 24.8%    → 邊框確實會畫出來
borderless request      allowed
ring changed after that  0.0%    → 邊框消失
```

數字只說「有東西變了」，**判定請看寫出來的 `border-*.png`**：指示邊框是一兩個像素寬的線，在幾個像素深的比對帶裡推不動這個數字多少。這也是為什麼圖要存下來。

比對帶刻意往視窗內縮 4px：指示邊框畫在視窗自己的邊緣上、不是外面，只比對視窗外圍會量到 0.0%，而圖上明明有一圈黃線。

## 備註

- 需要 Windows 10 1903 以上才有 `capture supported`；`caps` 在更舊的系統上會回 False 並以 exit code 2 結束。
- 所有指令都會把主專案的 NLog 輸出接到 console，所以擷取層自己記的東西（接上了哪個 HWND、丟掉了哪一幀）看得到。
- 這是開發/除錯工具，不隨 app 發佈。
