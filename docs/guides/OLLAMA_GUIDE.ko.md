# 로컬 LLM (Ollama) 설치 안내

> **Language:** **[繁體中文](OLLAMA_GUIDE.md)** ｜ **[English](OLLAMA_GUIDE.en.md)** ｜ **[简体中文](OLLAMA_GUIDE.zh-Hans.md)** ｜ **[日本語](OLLAMA_GUIDE.ja.md)** ｜ **한국어 ✓**

OverTranslate의 **OpenAI** 번역 서비스는 OpenAI API 호환 형식을 지원하므로, [Ollama](https://ollama.com/)로 LLM을 내 PC에서 실행할 수 있습니다. 로컬 모델을 쓰면 API 비용이 따로 들지 않고, 번역하는 내용이 외부 서버로 전송되지도 않습니다.

아래에서는 `translategemma:4b`(Google이 공개한, 번역 작업에 최적화된 모델)를 예로 설명합니다.

> **필요 사양:** 로컬 LLM은 PC의 CPU / GPU 자원을 사용합니다. 번역 속도를 생각하면 외장 그래픽 카드가 있는 PC를 권장합니다.
>
> 이 안내에서 사용하는 `translategemma:4b`의 크기는 약 3.3 GB입니다. 그래픽 카드의 VRAM은 **최소 4 GB, 가능하면 6 GB 이상**을 권장합니다. 실제 사용량은 Ollama와 입력 내용, 다른 프로그램의 GPU 사용량에 따라 달라집니다.

## 1. Ollama 설치

1. [Ollama 공식 사이트](https://ollama.com/download)에서 사용 중인 운영체제용 설치 파일을 내려받습니다
2. 설치 파일을 실행하고 기본 옵션 그대로 설치를 마칩니다

설치가 끝나면 Ollama를 실행합니다. Ollama의 API 주소는 기본값이 `http://localhost:11434`입니다(관련 설정을 바꾼 적이 있다면 실제 주소를 사용하세요).

## 2. 모델 내려받기

‘명령 프롬프트’ 또는 ‘PowerShell’을 열고 다음을 입력합니다.

```
ollama pull translategemma:4b
```

내려받기가 끝날 때까지 기다립니다(모델이 몇 GB이므로 회선에 따라 몇 분 걸립니다).

> [Ollama Models](https://ollama.com/search)에서 다른 모델을 찾아 모델 이름을 바꿔도 됩니다.
> 사고 모드를 쓰지 않는 모델을 고르세요. 무엇을 골라야 할지 모르겠다면 이 안내대로 translategemma:4b를 그대로 쓰면 됩니다.

## 3. OverTranslate에서 설정

1. OverTranslate 설정 페이지를 열고 번역 서비스에서 **OpenAI Compatible**을 선택합니다
2. 다음 항목을 입력합니다.

   | 항목 | 입력할 값 |
   |------|-----------|
   | API 주소 | `http://localhost:11434/v1` |
   | API 키 | 아무 문자열이나 가능합니다(로컬 실행이면 비워 두거나 `ollama` 입력) |
   | 모델 이름 | `translategemma:4b` |

   > API 주소와 모델 이름은 앱의 기본값 그대로이므로 **비워 두어도 됩니다**. 실제로 적용될 값이 흐린 글자로 표시됩니다.

3. 저장하면 로컬 LLM으로 번역을 시작할 수 있습니다
