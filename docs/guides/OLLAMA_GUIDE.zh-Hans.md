# 本地 LLM（Ollama）安装教程

> **Language：** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **简体中文 ✓** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate 的 **OpenAI** 翻译服务支持 OpenAI API 兼容格式，可搭配 [Ollama](https://ollama.com/) 在本机运行 LLM 模型。使用本地模型不需要额外支付 API 费用，翻译内容也不会发送至外部服务器。

以下以 `hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M`（腾讯推出的翻译专用模型，支持 33 种语言互译）作为示例。

> **硬件需求：** 本地 LLM 会占用电脑的 CPU / GPU 资源。建议使用具备独立显卡的电脑，以获得较好的翻译速度。
>
> 本教程使用的模型文件约 4.6 GB，载入后约占用 5.5 GB，建议显卡具备 **至少 6 GB 显存，8 GB 以上更佳**。实际内存占用仍会依 Ollama、输入内容及其他程序的 GPU 使用量而有所不同。
>
> 显存只差一点就不够时，速度会大幅下降（模型会有一部分改由 CPU 运行），此时建议改用较小的量化版本，见下方说明。

## 1. 安装 Ollama

1. 前往 [Ollama 官网](https://ollama.com/download) 下载对应操作系统的安装程序
2. 运行安装文件，按照默认选项完成安装即可

安装完成后，打开 Ollama 应用。Ollama 的 API 地址默认为 `http://localhost:11434`（若曾修改过相关设置，请以实际的 API 地址为准）

## 2. 下载模型

打开「命令提示符」或「PowerShell」，输入：

```
ollama pull hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M
```

等待下载完成即可（模型大小约 4.6 GB，依网络状况需要几分钟）。

> **显存不足时**，可改用同一个模型的较小版本，把 `ollama pull` 后面的名称换成下列其中一个：
>
> | 模型名称 | 文件大小 | 适用 |
> |----------|----------|------|
> | `hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M` | 约 4.6 GB | 6 GB 以上显存（本教程使用） |
> | `hf.co/unsloth/Hy-MT2-7B-GGUF:IQ4_XS` | 约 4.2 GB | 5～6 GB 显存 |
> | `hf.co/unsloth/Hy-MT2-7B-GGUF:Q3_K_M` | 约 3.8 GB | 4 GB 显存 |
>
> 后两个由社区重新打包，模型本身相同。数字越小文件越小、速度越快，翻译质量则会略为下降。

> 也可以在 [Ollama Models](https://ollama.com/search) 搜索其他模型，并替换成想使用的模型名称。
> 需选择不启用思考模式的模型；如果不确定该选哪个，可直接按照本教程使用推荐的模型。

## 3. 在 OverTranslate 中设置

1. 打开 OverTranslate 设置页 → 翻译服务选择 **OpenAI Compatible**
2. **API 地址** 与 **API Key** 都可以留空：地址的默认值就是 Ollama 的本机地址，本机运行也不需要密钥
3. **模型设置** 保持在 **推荐设置** 即可 —— 它用的正是本教程推荐的模型 `hf.co/tencent/Hy-MT2-7B-GGUF:Q4_K_M`，提示词也是为它写的
4. 关闭设置页后即可开始使用本机 LLM 进行翻译

> **换成其他模型时**，在 **设置清单** 按 **新增设置**，填入模型名称（包含 `hf.co/` 开头的完整名称）与该模型建议的提示词，保存后选取即可。一份设置就是一整组：模型名称、Temperature，以及 **自动** 与 **指定语言** 各一组 System / User 提示词，切换设置会整组一起换。

> **提示词与 Temperature**：默认提示词会依照 OverTranslate 的界面语言自动切换，并已针对本教程推荐的模型调整过。它只发送 User 提示词、不发送 System 提示词，这是依照该模型官方文档的格式；不同模型的要求不同，两者至少填写一项即可。高级参数（Temperature `0.7`、Top P `0.6`、Seed `42`）默认全部启用，数值为推荐模型官方文档的建议值；Seed 固定的情况下，相同的原文每次仍会得到一致的译文。
