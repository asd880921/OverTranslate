# Issue #47 beam=1 stability and English model comparison

測試日期：2026-08-12（Asia/Taipei）

硬體：AMD Ryzen 7 3700X、32 GiB RAM、Windows x64、AVX2
正式候選：Mozilla Bergamot production model、beam=1

## 四語向 10 分鐘穩定性

每個語向以 batch=4 連續執行至少 600 秒。四組合計 52,555 次 provider 請求，失敗數為 0。

| 語向 | 請求 | p50 | p95 | max | 工作集（開始→結束） |
|---|---:|---:|---:|---:|---:|
| EN→ZH-HANT | 17,085 | 34.9 ms | 44.9 ms | 114.8 ms | 339.9→398.0 MiB |
| JA→ZH-HANT（經 EN） | 9,170 | 58.7 ms | 88.7 ms | 106.4 ms | 755.2→816.8 MiB |
| KO→ZH-HANT（經 EN） | 9,370 | 65.5 ms | 79.7 ms | 182.9 ms | 755.6→817.5 MiB |
| ZH-HANT→EN | 16,930 | 35.5 ms | 45.2 ms | 68.0 ms | 342.8→354.6 MiB |

工作集會增加 11.8–61.9 MiB。BlockingService 的 cacheSize 預設為 0，因此不是翻譯快取；這次 10 分鐘測試沒有取樣出可證明線性洩漏的斜率。Phase 1 應保留定時測試並加入長時間 checkpoint，確認成長是否在配置器 high-water mark 後停止。

## OCR CPU 干擾

使用 `subtitle-lost-entirely-1226x196.png`、即時模式 primary detector size 640，各量五次。負載為最重的 JA→EN→ZH-HANT batch=4 連續翻譯。

| 狀態 | OCR 樣本（ms） | median | p95 | 辨識正確 |
|---|---|---:|---:|---:|
| 無 NMT 負載 | 93, 85, 83, 78, 74 | 83 ms | 91.4 ms | 5/5 |
| JA pivot NMT 負載中 | 146, 198, 113, 101, 106 | 113 ms | 187.6 ms | 5/5 |

OCR median 增加約 36%，p95 增加約 105%，但五次都完整讀出 `You seem rather dispirited, Minato-san.`。同時 NMT p95 從單獨穩定性測試的 88.7 ms 增至 147 ms。Phase 1 整合需限制 NMT 與 OCR 的 CPU 競爭，並以最新畫面優先，避免堆積過期 OCR 工作。

## 英文→繁中其他模型（beam=1）

全部使用相同 20 句 issue #47 語料。chrF 只作固定參考答案的機械指標，不取代人工品質判斷。

| 模型 | 授權／用途 | 模型大小 | init | batch=1 p50 / p95 | batch=4 p50 / p95 | chrF |
|---|---|---:|---:|---:|---:|---:|
| Microsoft 雲端（參考） | 雲端服務 | — | — | 既有報告 | 既有報告 | 47.69 |
| Mozilla Bergamot production（目前候選） | 需完成逐模型授權確認 | 47.2 MiB | 204 ms | 17.3 / 31.3 ms | 37.0 / 45.0 ms | 37.70 |
| Helsinki-NLP OPUS-MT en-zh，CTranslate2 INT8 | Apache-2.0 | 80.2 MiB | 144 ms | 50.4 / 88.8 ms | 82.6 / 115.1 ms | 22.43 |
| Meta NLLB-200 distilled 600M，CTranslate2 INT8 | CC-BY-NC-4.0，僅比較 | 604.0 MiB | 811 ms | 382.2 / 513.4 ms | 544.7 / 714.0 ms | 16.31 |

OPUS-MT 可輸出繁體，但對遊戲 UI、專名與 OCR noise 的翻譯明顯較弱，速度也較目前候選慢。NLLB 的 CTranslate2 INT8 轉換在這組測試出現截句、符號與短 UI 誤譯；除此之外，非商業授權已足以排除產品採用。結論維持目前 Mozilla Bergamot production model 與 beam=1。

來源：

- Mozilla production model registry: https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json
- OPUS-MT model card: https://huggingface.co/Helsinki-NLP/opus-mt-en-zh
- NLLB-200 model card: https://huggingface.co/facebook/nllb-200-distilled-600M

## Artifact SHA-256

- `stability-en-zh-hant-beam1-10m.json`: `A9D64AE6F65693D8572DDF88CCCF9E4B65D7F70AEC6674B7656D275990C92392`
- `stability-ja-zh-hant-beam1-10m.json`: `C0E34DA67CFDD23980D38D1E7E2B0999A0FF7299E869E63D67C0FCD02A24FC3A`
- `stability-ko-zh-hant-beam1-10m.json`: `4C83043D1838BE34882A9D1A8E9FD638F60317A8C75581325D1B59F46F3585F1`
- `stability-zh-hant-en-beam1-10m.json`: `7688A1B4402897B1FBE0862B16CA0C46063F66FFA15E1F9DB8B90F03785066A4`
- `en-zh-hant-v1-opus-mt-ct2-int8-beam1.json`: `42426C7E73B7BDF9A070CD4D81517DEB3A61A0D370C1A8F24927E128FE88B355`
- `en-zh-hant-v1-nllb-600m-ct2-int8-beam1.json`: `DC3A62A6FBE1073BE8710F04ED1ABF7BE816CD3960AA21D60712D07F6734920F`
