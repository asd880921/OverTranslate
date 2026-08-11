# Issue #47 Bergamot runtime 與模型授權確認

查核日期：2026-08-12

> 本文件是依官方一手資料進行的工程合規盤點，不是法律意見。沒有明確授權的項目不推定為可用。

## 結論

目前只能讓 **native runtime 授權門檻通過**；四個已選 Mozilla production model 的商業使用、第三方直接下載與再散布／鏡像授權，**仍不能勾選通過**。

- `browsermt/bergamot-translator` 與目前 native 相依套件的授權容許商業使用及散布，但正式發行必須附上完整第三方授權聲明，並依 MPL-2.0 提供 Bergamot 對應原始碼及修改。
- Mozilla 官方文件明確將現行模型放在公開 GCS bucket，公開 registry 也能識別四個測試 artifact；但 registry、逐模型 `metadata.json` 及各模型 run 目錄都沒有 `license`、`NOTICE` 或其他明確授權文件。
- 舊的 `mozilla/firefox-translations-models` repository 有根目錄 MPL-2.0，但四個目前選用的精確 GCS artifacts 不在該 Git tree。新的 `mozilla/translations` repository 雖然也是 MPL-2.0，根目錄授權並未明文把 repository 之外的 GCS model weights 納入 Covered Software。不可自行推定這些 weights 為 MPL-2.0。
- 「任何人可無驗證下載」只證明存取機制，不是模型著作權的商用、再散布或鏡像授權。因此目前不可把模型放入 repository、installer、GitHub Release、自有 CDN 或正式下載 catalog。

要解除 blocker，需由 Mozilla 針對下列四個 SHA-256 artifact 提供可保存的書面確認，或在 registry／artifact 旁補上逐模型授權：允許的商業使用、直接下載、鏡像／再散布範圍，以及必要 attribution。

## 1. Native runtime

### Bergamot

PoC 固定的 `bergamot-translator` commit `9271618ebbdc5d21ac4dc4df9e72beb7ce644774` 採 [MPL-2.0](https://github.com/browsermt/bergamot-translator/blob/9271618ebbdc5d21ac4dc4df9e72beb7ce644774/LICENSE)。MPL-2.0 明確授予使用、修改與散布權；可把 Covered Software 與其他程式組成 Larger Work，並讓其他檔案維持自己的授權。Mozilla 的 [MPL 2.0 FAQ（Q8、Q11）](https://www.mozilla.org/en-US/MPL/2.0/FAQ/) 也說明 MPL 是 file-level copyleft，包含靜態連結情境時，不會因此要求 Larger Work 中的其他獨立檔案改採 MPL。

正式散布 DLL／executable 時仍須遵守 MPL 3.1–3.4：

- 保留既有 copyright、license、warranty 與 liability notices。
- 讓收件者能以合理、及時的方式取得該 binary 對應的 Bergamot Source Code Form。
- 公開 OverTranslate 對 Bergamot MPL-covered files 的所有修改／patch；只提供未修改 upstream 不足以覆蓋實際有修改的版本。
- 清楚告知使用者原始碼取得位置。固定 commit 的公開 fork 或可重現的 source archive 比只連到 upstream `main` 安全。
- MPL 不授予 Mozilla 或其他 contributor 的商標權；不可暗示背書。參見 [Mozilla Licensing Policies](https://www.mozilla.org/en-US/foundation/licensing/)。

目前 upstream 預設 `USE_STATIC_LIBS=ON`，即對 non-system libraries 採靜態連結；見固定 commit 的 [CMakeLists.txt](https://github.com/browsermt/bergamot-translator/blob/9271618ebbdc5d21ac4dc4df9e72beb7ce644774/CMakeLists.txt#L73-L77)。靜態或動態連結不會免除各套件的 notice 義務。

### 目前 native dependency 盤點

| 元件 | PoC 固定版本 | 官方授權來源 | 發行義務摘要 |
|---|---|---|---|
| Bergamot Translator | `9271618e…` | [MPL-2.0](https://github.com/browsermt/bergamot-translator/blob/9271618ebbdc5d21ac4dc4df9e72beb7ce644774/LICENSE) | 保留 notices；提供精確對應 source 與修改 |
| Marian | `2781d735…` | [MIT](https://github.com/browsermt/marian-dev/blob/2781d735d4a10dca876d61be587afdab2726293c/LICENSE.md) | binary／substantial portions 保留 copyright 與 permission notice |
| ssplit-cpp | `a311f986…` | [Apache-2.0；data 另為 LGPL-2.1](https://github.com/browsermt/ssplit-cpp/blob/a311f9865ade34db1e8e080e6cc146f55dafb067/LICENSE.md) | 附 Apache license、保留 notices；目前未散布 `nonbreaking_prefixes` data |
| SentencePiece | `ae41b774…` | [Apache-2.0](https://github.com/browsermt/sentencepiece/blob/ae41b7740d7006596bb9257e83340b2620db9d00/LICENSE) | 附 license、保留 notices；一併保留其 vendored dependency notices |
| intgemm | `f7401513…` | [MIT](https://github.com/kpu/intgemm/blob/f7401513da71758dacce52fed1c7855549abee59/LICENSE) | 保留 copyright 與 permission notice |
| OpenBLAS | `0.3.33` | [BSD-3-Clause](https://github.com/OpenMathLib/OpenBLAS/blob/v0.3.33/LICENSE) | binary distribution 的文件／其他材料重現 copyright、條款與 disclaimer；不得暗示背書 |
| PCRE2 | `10.39` | [BSD-style licence](https://github.com/PCRE2Project/pcre2/blob/pcre2-10.39/LICENCE) | 在目前自製 DLL 靜態包含 PCRE2 的情況下附帶其 notice |

Marian／SentencePiece 的 vendored code 還包含 MIT、BSD、Apache-2.0、zlib 等授權元件，例如 CLI11、cnpy、FAISS、phf、pathie-cpp、spdlog、yaml-cpp、protobuf-lite 與 Abseil；固定 Marian tree 的官方清單可由 [`src/3rd_party`](https://github.com/browsermt/marian-dev/tree/2781d735d4a10dca876d61be587afdab2726293c/src/3rd_party) 稽核。正式 package 應從最終 build graph／binary 產生完整 SBOM 與 `ThirdPartyNotices`，不要只手抄上述 direct dependencies。

[Apache License 2.0 §4](https://www.apache.org/licenses/LICENSE-2.0.txt) 要求附授權、標示修改及保留適用 notices；若 upstream distribution 含 `NOTICE`，也須傳遞適用內容。本次固定的 Bergamot、Marian、ssplit-cpp 與 SentencePiece root trees 未找到獨立 `NOTICE`，但這不會取消 LICENSE／copyright notices 的義務。

### MSVC runtime

PoC 使用 MSVC `/MT`，即把 CRT 靜態連入 binary；Microsoft 的 [`/MT` 說明](https://learn.microsoft.com/en-us/cpp/c-runtime-library/global-state?view=msvc-170) 可確認此行為。Microsoft 只允許持有有效 Visual Studio／Build Tools 授權者依 Distributable Code 條款散布對應元件；參見官方 [Redistributing Visual C++ Files](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution)。正式 build 應保存 compiler、edition、版本與 CI 授權 provenance。若使用 Visual Studio Community，還須符合其 [個人、開源與組織使用限制](https://visualstudio.microsoft.com/vs/community/)。

### Runtime 決策

Runtime 的商業散布是可行的。Phase 2 在發行 native binary 前至少要完成：

1. 建立可由最終 binary 重播的 SBOM／第三方授權清單。
2. installer／package 內附 MPL、MIT、Apache、BSD、zlib、protobuf-lite、PCRE2、OpenBLAS 等實際用到的完整 notices。
3. 提供固定 Bergamot source snapshot、所有 MPL-covered 修改及清楚的取得連結。
4. 若未來開始封裝 ssplit `nonbreaking_prefixes`，另行處理其 LGPL-2.1 data 義務。

## 2. 四個選用模型

Mozilla 的 [`models.json`](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json) 是目前官方 registry。下列 hash、architecture 與 release status 均可在該 registry 直接核對；但完整 registry 中沒有 `license` 欄位。

| 方向 | Registry artifact | 狀態 | SHA-256（解壓後 model） | 明確模型授權 |
|---|---|---|---|---|
| EN→ZH-HANT | [`model.enzh_hant.intgemm.alphas.bin.gz`](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/models/en-zh_hant/zh_hant_llmaat_finetune10M_qe8_f2_aQ8azdOMQOSBVjBDOVDIZQ/exported/model.enzh_hant.intgemm.alphas.bin.gz) | Release / base-memory | `559ab90d723a58c1f1e2ab7cc12137bc667af5ba3e325e3eb30b5cdc930db520` | 未提供 |
| JA→EN | [`model.jaen.intgemm.alphas.bin.gz`](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/models/ja-en/cjk_icu_base_U4VUAW3STh-bF0Sr-dX69g/exported/model.jaen.intgemm.alphas.bin.gz) | Release Desktop / base | `a9bf800679bba570520e1161d7b4fbfcb957add32ca35812134add85689752ad` | 未提供 |
| KO→EN | [`model.koen.intgemm.alphas.bin.gz`](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/models/ko-en/cjk_icu_base_BnKgBdd0Rzq87oUYN3L9-A/exported/model.koen.intgemm.alphas.bin.gz) | Release Desktop / base | `1c902d6f7a8d7e3efe6ff4f7d4960a369957bca4ce2ce4a6e8572c231d525090` | 未提供 |
| ZH-HANT→EN | [`model.zh_hanten.intgemm.alphas.bin.gz`](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/models/zh_hant-en/zh_hant_openlid_zh_tw_lr0002_WJi5Ozi7SZWC6hgfD5GhTA/exported/model.zh_hanten.intgemm.alphas.bin.gz) | Release / base-memory | `0aee91790894458f5d367551f6edcd4c9cb97852c34f221bcbf9f4701ebcf0cd` | 未提供 |

四個 run 的 `exported/metadata.json` 只記載語言、architecture、byte size、hash、model config 與參數統計；run prefix 的公開物件清單也沒有 `LICENSE` 或 `NOTICE`。例如 [EN→ZH-HANT metadata](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/models/en-zh_hant/zh_hant_llmaat_finetune10M_qe8_f2_aQ8azdOMQOSBVjBDOVDIZQ/exported/metadata.json)、[JA→EN metadata](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/models/ja-en/cjk_icu_base_U4VUAW3STh-bF0Sr-dX69g/exported/metadata.json)、[KO→EN metadata](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/models/ko-en/cjk_icu_base_BnKgBdd0Rzq87oUYN3L9-A/exported/metadata.json) 與 [ZH-HANT→EN metadata](https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/models/zh_hant-en/zh_hant_openlid_zh_tw_lr0002_WJi5Ozi7SZWC6hgfD5GhTA/exported/metadata.json) 均沒有 license 或 attribution 欄位。

官方 [`mozilla/translations` README](https://github.com/mozilla/translations/blob/main/README.md) 說明其訓練 pipeline 與 inference engine 採 MPL-2.0，並說 trained models 存放在公開 GCS bucket。這證明 artifact 的官方來源及 Firefox 使用情境，但沒有明文宣告所有 bucket weights 採 MPL-2.0。舊 [`mozilla/firefox-translations-models` LICENSE](https://github.com/mozilla/firefox-translations-models/blob/main/LICENSE) 也只能可靠地套用到該 repository 內的 Covered Software；本表四個精確 GCS artifact 不在該 Git tree。

因此四個方向目前都無法得到「exact model license」，也無法確認 supporting lexicon／vocabulary files 的商用再散布及 attribution 條件。

## 3. 直接下載與鏡像

Google 官方文件說明 [public Cloud Storage data 可在不驗證身分下下載](https://docs.cloud.google.com/storage/docs/access-public-data)，Mozilla README 也把 registry 與 bucket 當成官方模型發布位置。這支持以下技術事實：OverTranslate 可以用 HTTPS 直接取得目前公開的物件。

但官方資料沒有找到以下承諾：

- 第三方桌面程式可為商業用途長期 hotlink 該 bucket。
- Mozilla 對第三方 downloader 提供可用性、頻寬或相容性保證。
- 第三方可以把這些 artifacts 複製到 GitHub Release、自有 CDN 或 installer。
- 四個模型與 supporting files 的 attribution、衍生修改及再散布條件。

所以目前分類為：

| 方式 | 技術狀態 | 授權狀態 | 決策 |
|---|---|---|---|
| 使用者由 Mozilla GCS 直接下載 | 公開、無驗證即可下載 | 未見逐模型 copyright grant；hotlink／商用條件未明 | PoC 可用；正式功能不可標記為已完成授權確認 |
| OverTranslate mirror／CDN／GitHub Release | 技術上可複製 | 未見明確再散布授權 | 禁止，直到取得書面確認 |
| 模型隨 installer 發行 | 技術上可封裝 | 未見明確再散布授權 | 禁止，直到取得書面確認 |

Mozilla 的 [Licensing Policies](https://www.mozilla.org/en-US/foundation/licensing/) 提醒應以個別作品附帶的授權為準，未解決的 reuse 問題可聯絡 `licensing@mozilla.org`。應在詢問中列出四個完整 hash，並明確區分：商業推論使用、終端使用者從 Mozilla GCS 直接下載、OverTranslate 自行 mirror，以及 installer bundling。

## 4. Attribution 與供應風險

### 發行時應準備

- `ThirdPartyNotices`：列出實際 linked／bundled runtime 及完整 license text／copyright notices。
- Bergamot source offer/link：固定到實際 build commit，包含本專案 patch 與 build instructions。
- Model manifest：固定每個 model、lexicon、vocabulary 的 URL、compressed／uncompressed hash、size、registry snapshot 日期及最終取得的 license／attribution 文件。
- UI 或 About／Licenses 頁：讓使用者能找到 runtime notices、source link 與日後確認的 model notices。

### 尚未解除的風險

1. **權利風險（blocking）**：四個 weights 與 supporting files 無逐 artifact license；不可用 repository MPL 或 bucket public ACL 代替明確授權。
2. **資料 provenance 風險**：公開的 training configs 只指向 build worker 上的 generic corpus path，沒有足以對四個成品完成資料來源／授權稽核的 manifest；不能從「Mozilla 訓練」推定所有訓練資料條件。
3. **供應風險**：registry 與 bucket path 是外部可變服務，沒有找到第三方 SLA 或長期 hotlink 承諾；而在授權未清前又不能合法確定可自行 mirror，會形成單一來源風險。
4. **完整性風險**：正式 downloader 必須以 pinned manifest 驗證所有 model／lexicon／vocab hashes，不能只依 `Release` status 或永遠抓最新 registry。
5. **授權漂移**：每次換模型 path／hash、runtime commit、OpenBLAS 或 compiler toolchain，都必須重新產生 SBOM 並重做授權 gate。

## Phase 1 決策

- `[x]` native runtime 授權：可進入正式工程，但發行前必須落實 source availability 與 ThirdPartyNotices。
- `[ ]` 四個 Mozilla production model 的商業使用、直接下載與再散布授權：**未通過**。
- `[ ]` model attribution／training provenance：**未取得足夠的一手資料**。

在 Mozilla 提供可保存的明確回覆前，僅保留目前 L0 PoC 與本機測試 artifacts；不要實作會對外發布模型的 installer、mirror 或正式下載 catalog。
