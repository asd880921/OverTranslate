# 打包與發布

發版由 CI 負責：**壓一個 tag，GitHub Actions 打包並建立一個 pre-release**。
把它轉成正式版是唯一會推給使用者的動作，而且必須由人手動做。

本專案以 **自封式（self-contained）** 發布，使用者**不需另外安裝 .NET 8 Runtime**。

---

## 一、正常流程

### 1. 調版號並推上 main

```
src\OverTranslate\OverTranslate.csproj  →  <Version>2.0.0</Version>
```

> CI 的版號其實**以 tag 為準**，csproj 不參與（不一致只警告）。這一步是為了讓程式碼裡的版號
> 與發出去的版本一致；純測打包流程時可以跳過，直接壓一個不同的 tag。

### 2. 壓 tag

```powershell
git tag 2.0.0
git push origin 2.0.0
```

tag **不加 `v` 前綴**（沿用本倉慣例）。觸發條件是 `[0-9]*` 或 `v[0-9]*`，
所以標記用的 tag 不會誤觸發一次 370 MB 的打包。

### 3. 等 CI（約 2～10 分鐘）

[`.github/workflows/release.yml`](../../.github/workflows/release.yml) 會：

- 用 `vpk download github` 抓線上最新**正式版**的 full 包當 delta 基準（runner 每次都是全新的）
- `dotnet publish`（自封式）+ `vpk pack`，一併產出 portable
- 建立 **pre-release**，附上 `releases.win.json`、`-full.nupkg`、`-delta.nupkg`、
  `Setup.exe`、`Portable.zip`

這一步失敗最常見的原因是**版號沒有比線上最新正式版高** —— `vpk` 會拒絕打包：

```
[FTL] There is a release in channel win which is equal or greater to the current version
```

### 4. 驗更新

見 [TEST-UPDATE-PRERELEASE.md](TEST-UPDATE-PRERELEASE.md)。一般使用者看不到 pre-release，
要在自己機器上看到得設 `OVERTRANSLATE_UPDATE_PRERELEASE=1`。

### 5. 轉成正式版

到那個 release 按 **Edit release**，取消勾選 *Set as a pre-release*。

**使用者拿到的就是你剛剛驗過的同一批檔案**，不是重打的另一包。

---

## 二、版號規則

```
1.6.0  <  1.6.1-beta.1  <  1.6.1  <  1.6.2
```

- 版號**只能往上**。客戶端的 `AllowVersionDowngrade` 是 `False`，`vpk pack` 也拒絕打包
  比既有版本低的版號。
- 預發行版的 base 版號要**等於你打算發的那個正式版號**。走到 `2.0.0-beta.5` 之後才改主意
  想發 `1.8.0`，停在那個版本的機器就再也升不上去了（`1.8.0 < 2.0.0-beta.5`），
  而且不會報錯，只顯示「已是最新版本」。
- ⚠️ 預發行版千萬別直接用不帶後綴的版號（例如拿 `2.0.0` 當測試版），
  否則之後就無法再發「更新的正式 2.0.0」。

---

## 三、手動發版（CI 掛掉時的退路）

腳本用 `$PSScriptRoot` 解析相對路徑，**不看當前工作目錄**，從哪裡呼叫都可以。

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1
```

- 版號取自 csproj 的 `<Version>`，或用 `-Version` 覆寫
- 會先清空 `src\OverTranslate\bin\Publish` 再自封式 publish，最後 `vpk pack`
- `appsettings.json` 不會被打包進去（會覆蓋使用者既有設定）
- 一併產出 `OverTranslate-<channel>-Portable.zip`。portable 與安裝版**共用同一條更新 feed** ——
  `releases.<channel>.json` 完全不因 portable 而改變 —— 所以它一樣能自動更新，
  前提是 zip 有跟其他檔案一起上傳到同一個 Release
- 已手動 publish、只想重新打包時加 `-SkipPublish`

輸出在 `artifacts\releases\`（未版控）。**不要期待它一直在** —— Velopack 靠裡面的舊 full 包
產生 delta，換機器或誤刪之後要先抓回來：

```powershell
vpk download github --repoUrl https://github.com/asd880921/OverTranslate --channel win --outputDir .\artifacts\releases
```

上傳到 GitHub Release 的檔案（**勾選 Set as a pre-release**，確認後再取消）：

- `releases.win.json`
- `OverTranslate-win-Setup.exe`
- `OverTranslate-win-Portable.zip`
- `OverTranslate-<版本>-full.nupkg`（及 `-delta.nupkg`，若有）

---

## 四、beta channel（現在很少用到）

除了 `win`，還有一條獨立的 `beta` 管線，由環境變數切換：

```powershell
[Environment]::SetEnvironmentVariable("OVERTRANSLATE_CHANNEL", "beta", "User")   # 訂閱
[Environment]::SetEnvironmentVariable("OVERTRANSLATE_CHANNEL", $null, "User")    # 退出
```

打包時加 `-Channel beta` 並用 `-Version` 指定帶後綴的版號：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 -Channel beta -Version 2.0.0-beta.1
```

**但 CI 的 pre-release 流程已經涵蓋了它原本的用途，而且更好** ——
CI 打的是 `win` channel，你測到的就是使用者會拿到的同一批二進位；
走 beta channel 測的是 `-beta-full.nupkg`，內容相同但 SHA 與 delta 基準都不一樣，
嚴格說沒測到同一顆。

> ### ⚠️ 發正式版**不會**讓 beta 訂閱者升上去
>
> 兩條 channel 是各自獨立的 feed：訂在 beta 的機器只讀 `releases.beta.json`，
> 而正式發布只上傳 `releases.win.json`。版號再大也沒用，**它根本看不到那個包**。
>
> 實例：`releases.win.json` 的最新是 `1.7.0`，而 `releases.beta.json` 還停在 `1.6.1-beta.1`
> —— 訂在 beta 的機器從來沒收到過 1.7.0。
>
> 要讓 beta 訂閱者跟上，發正式版時**兩條都打**，兩組檔案上傳到同一個 Release：
>
> ```powershell
> powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1
> powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 `
>     -SkipPublish -Channel beta -Version 2.0.0
> ```
>
> （腳本會警告「beta channel 但版號不含預發行後綴」，這裡是刻意的，忽略即可。）
