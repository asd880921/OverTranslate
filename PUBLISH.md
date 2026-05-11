請先使用 `cmd` 開啟至目前資料夾路徑後，再輸入以下指令：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1
```

此腳本現在會先清空 `src\OverTranslate\bin\Publish`，再執行 `dotnet publish` 到該資料夾，最後呼叫 Velopack 產生安裝程式與更新檔。

`appsettings.json` 不會被放進 publish 輸出，避免更新時覆蓋既有設定。

如果你已經手動 publish，只想重新打包，可加上：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-velopack.ps1 -SkipPublish
```
