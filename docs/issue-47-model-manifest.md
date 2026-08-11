# Issue #47 L0 Bergamot model manifest

這份 manifest 只記錄 L0 技術 PoC 實際使用的 runtime 與模型，不授予或推定任何模型授權。
模型來自 Mozilla production [model registry](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json)，
但 registry 未提供逐模型 license；在權利、attribution、第三方下載與再散布條件得到可保存的明確結論前，
不得把這些 binary 放入 repository、installer、Release 或正式下載 catalog。

## Native runtime

- browsermt/bergamot-translator commit：`9271618ebbdc5d21ac4dc4df9e72beb7ce644774`
- Marian submodule commit：`2781d735d4a10dca876d61be587afdab2726293c`
- compiler：MSVC 19.51；CMake 3.30；Release x64；`BUILD_ARCH=x86-64-v3`；CPU-only
- BLAS：OpenBLAS 0.3.33 x64；`libopenblas.dll` SHA-256
  `C8B6F93012B81EB5775955006A1A363D720BA2013B5262052944763E0A736346`
- PoC wrapper：`overtranslate_bergamot.dll` 6,758,912 bytes；SHA-256
  `9D076FB316E4516142EBA335CFA70C6C28FAE6BB06B05F8EDBB8E85327C539DE`

wrapper 使用一個 `BlockingService`。direct handle 持有一個 model；JA／KO pivot handle 在同一 service
持有兩個 model 並呼叫 `pivotMultiple`。兩個獨立 service 會因 Marian 全域 logger 名稱衝突而失敗，
不可把兩個獨立 process 的結果冒充 in-process pivot。

## Models

| 路徑 | registry status | architecture | model bytes | model SHA-256 |
|---|---|---|---:|---|
| EN→ZH-HANT | Release | base-memory | 43,849,787 | `559AB90D723A58C1F1E2AB7CC12137BC667AF5BA3E325E3EB30B5CDC930DB520` |
| JA→EN | Release Desktop | base | 59,504,955 | `A9BF800679BBA570520E1161D7B4FBFCB957ADD32CA35812134ADD85689752AD` |
| KO→EN | Release Desktop | base | 59,504,955 | `1C902D6F7A8D7E3EFE6FF4F7D4960A369957BCA4CE2CE4A6E8572C231D525090` |
| ZH-HANT→EN | Release | base-memory | 43,849,787 | `0AEE91790894458F5D367551F6EDCD4C9CB97852C34F221BCBF9F4701EBCF0CD` |

實際 routing：

- EN→ZH-HANT：direct，beam=4。
- JA→ZH-HANT：JA→EN→ZH-HANT，兩段皆 beam=4。
- KO→ZH-HANT：KO→EN→ZH-HANT，兩段皆 beam=4。
- ZH-HANT→EN：direct，beam=4。

## Supporting artifacts

| 方向 | 檔案 | bytes | SHA-256 |
|---|---|---:|---|
| JA→EN | `lex.50.50.jaen.s2t.bin` | 9,346,816 | `8F858A72FCBAA476C582577B04D6F5F89D645D2335B0B4A794C2706D4B1F75FF` |
| JA→EN | `vocab.jaen.spm` | 1,443,222 | `5CB217758BAE05877BB3F0C2F612E4E7C1E4CB03C10DB11F4A47098D7AE62919` |
| KO→EN | `lex.50.50.koen.s2t.bin` | 8,617,080 | `471CD980C4BA08C240246F9361F64EB5D627848A135B5731D665F9EFAA1E26AE` |
| KO→EN | `vocab.koen.spm` | 1,410,063 | `1C72B740AB793CDC3A8F16913DD6B4E806C77421077DD2D85EDEB7BE38418598` |
| ZH-HANT→EN | `lex.50.50.zh_hanten.s2t.bin` | 6,385,944 | `AA7DAF6CFC85C0CD2C10E2944D66F6DA55497C9C6408789F3ADFDED4074C2FB1` |
| ZH-HANT→EN | `srcvocab.zh_hanten.spm` | 769,669 | `5CC6A76611DBF86219F109141533606B15ECB34EEE83673BB86B2C16B14734DB` |
| ZH-HANT→EN | `trgvocab.zh_hanten.spm` | 812,572 | `7BF002DB37C10D3B114CC5588D7FDCB16C57D0FD1E2C34354C22CC9F0B6C3C29` |

下載後必須先驗證 registry 提供的 uncompressed model SHA-256，再建立 config 或執行翻譯。
