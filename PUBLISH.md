# 打包與發布

本專案以 **自封式（self-contained）** 發布，使用者**不需另外安裝 .NET 8 Runtime**。
更新透過 Velopack，分成兩條獨立管線（channel）：

| Channel | 用途 | 誰會收到 |
|---------|------|---------|
| `win`（穩定版）| 正式發布 | 所有使用者 |
| `beta`（先行版）| 自己／測試者先試 | 只有設了環境變數 `OVERTRANSLATE_CHANNEL=beta` 的人 |

兩條線的更新 feed 與 delta 鏈各自獨立，互不干擾。

> (設置 / 移除) 環境變數指令 (Powershell)：  
> ```powershell
> [Environment]::SetEnvironmentVariable("OVERTRANSLATE_CHANNEL", "beta", "User")
> ```
> ```powershell
> [Environment]::SetEnvironmentVariable("OVERTRANSLATE_CHANNEL", $null, "User")
> ```
---

## 共通事項

- 請先用 `cmd` 切到專案根目錄再執行指令。
- 腳本會先清空 `src\OverTranslate\bin\Publish`，再 `dotnet publish`（自封式）到該資料夾，最後呼叫 Velopack 打包。
- `appsettings.json` 不會被放進 publish 輸出，避免更新時覆蓋既有設定。
- **不要刪除 `artifacts\releases`**：Velopack 靠裡面的舊 full 包產生 delta，刪掉會導致每次更新都得下載整包。
- 已手動 publish、只想重新打包時，加 `-SkipPublish`。

---

## 一、發布穩定版（channel = win）

1. 把 `src\OverTranslate\OverTranslate.csproj` 的 `<Version>` 改成正式版號（例如 `1.6.1`，**不要帶 `-beta` 後綴**）。
2. 執行（不帶 `-Channel` 時預設就是 `win`）：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1
   ```

3. 把 `artifacts\releases` 內的 **win 管線檔案**上傳到 GitHub Release（**正式 release，不要勾 pre-release**）：
   - `releases.win.json`
   - `OverTranslate-win-Setup.exe`
   - `OverTranslate-<版本>-full.nupkg`（及 `-delta.nupkg`，若有）

所有使用者（含先前的 beta 測試者）都會更新到這個版本。

---

## 二、發布先行版（channel = beta）

1. **不必改 csproj**，直接用 `-Version` 指定預發行版號（semver 後綴格式，如 `1.6.1-beta.1`）：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 -Channel beta -Version 1.6.1-beta.1
   ```

   > 腳本會檢查 channel 與版號是否匹配（beta 應帶 `-beta.x` 後綴），不符會跳警告。

2. 把 `artifacts\releases` 內的 **beta 管線檔案**上傳到 GitHub Release（**勾選 Set as a pre-release**）：
   - `releases.beta.json`
   - `OverTranslate-beta-Setup.exe`
   - `OverTranslate-<版本>-beta.x-full.nupkg`（及 `-delta.nupkg`，若有）

### 怎麼讓自己／測試者收到 beta

在測試機設環境變數，然後重開 App：

```powershell
[Environment]::SetEnvironmentVariable("OVERTRANSLATE_CHANNEL", "beta", "User")
```

- 設了 `beta` → App 訂閱 beta 管線，會收到先行版。
- 沒設（一般使用者）→ 訂閱 `win`，只收穩定版，**完全看不到 beta**。
- 想退出 beta：刪掉該環境變數（或設成 `win`），重開 App 即可。

### 版號排序須知（semver）

```
1.6.0  <  1.6.1-beta.1  <  1.6.1  <  1.6.2
```

- 連續發多個 beta：`1.6.1-beta.1` → `1.6.1-beta.2`（後綴遞增）。
- beta 測試 OK 後發正式 `1.6.1`：版號比所有 `1.6.1-beta.x` 高，beta 測試者會順利升上正式版。
- ⚠️ beta 千萬別直接用 `1.6.1`（不帶後綴），否則之後就無法再發「更新的正式 1.6.1」給測試者。

---

## 三、本機重新打包（不重新 publish）

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 -SkipPublish
```
