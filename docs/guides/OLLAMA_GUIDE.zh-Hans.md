# 本地 LLM（Ollama）安装教程

> **Language：** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **简体中文 ✓** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **[한국어](OLLAMA_GUIDE.ko.md)**

OverTranslate 的 **OpenAI** 翻译服务支持 OpenAI API 兼容格式，可搭配 [Ollama](https://ollama.com/) 在本机运行 LLM 模型。使用本地模型不需要额外支付 API 费用，翻译内容也不会发送至外部服务器。

以下以 `translategemma:4b`（Google 推出、专为翻译任务优化的模型）作为示例。

> **硬件需求：** 本地 LLM 会占用电脑的 CPU / GPU 资源。建议使用具备独立显卡的电脑，以获得较好的翻译速度。
>
> 本教程使用的 `translategemma:4b` 模型大小约 3.3 GB，建议显卡具备 **至少 4 GB 显存，6 GB 以上更佳**。实际内存占用仍会依 Ollama、输入内容及其他程序的 GPU 使用量而有所不同。

## 1. 安装 Ollama

1. 前往 [Ollama 官网](https://ollama.com/download) 下载对应操作系统的安装程序
2. 运行安装文件，按照默认选项完成安装即可

安装完成后，打开 Ollama 应用。Ollama 的 API 地址默认为 `http://localhost:11434`（若曾修改过相关设置，请以实际的 API 地址为准）

## 2. 下载模型

打开「命令提示符」或「PowerShell」，输入：

```
ollama pull translategemma:4b
```

等待下载完成即可（模型大小约数 GB，依网络状况需要几分钟）。

> 也可以在 [Ollama Models](https://ollama.com/search) 搜索其他模型，并替换成想使用的模型名称。
> 需选择不启用思考模式的模型；如果不确定该选哪个，可直接按照本教程使用推荐的 translategemma:4b。

## 3. 在 OverTranslate 中设置

1. 打开 OverTranslate 设置页 → 翻译服务选择 **OpenAI Compatible**
2. 依次填入：

   | 字段 | 填入内容 |
   |------|----------|
   | API 地址 | `http://localhost:11434/v1` |
   | API Key | 任意文字皆可（本机运行可留空 或 输入 `ollama`） |
   | 模型名称 | `translategemma:4b` |

   > API 地址与模型名称就是程序的默认值，**留空即可**，字段会以淡色显示实际会套用的内容。

3. 保存后即可开始使用本机 LLM 进行翻译
