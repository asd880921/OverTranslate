#include "overtranslate_bergamot.h"

#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

#include "translator/parser.h"
#include "translator/response_options.h"
#include "translator/service.h"

namespace {

using marian::bergamot::BlockingService;
using marian::bergamot::ResponseOptions;
using marian::bergamot::TranslationModel;

struct BergamotHandle {
  BergamotHandle()
      : service(BlockingService::Config{}) {}

  BlockingService service;
  std::shared_ptr<TranslationModel> model;
  std::mutex mutex;
};

char *copyString(const std::string &value) {
  auto *copy = static_cast<char *>(std::malloc(value.size() + 1));
  if (copy == nullptr) {
    throw std::bad_alloc();
  }

  std::memcpy(copy, value.data(), value.size());
  copy[value.size()] = '\0';
  return copy;
}

void setError(char **error, const std::string &message) noexcept {
  if (error == nullptr) {
    return;
  }

  try {
    *error = copyString(message);
  } catch (...) {
    *error = nullptr;
  }
}

}  // namespace

void *ot_bergamot_create(const char *config_path, char **error_utf8) {
  if (error_utf8 != nullptr) {
    *error_utf8 = nullptr;
  }

  try {
    if (config_path == nullptr || config_path[0] == '\0') {
      throw std::invalid_argument("A Bergamot model config path is required.");
    }

    auto handle = std::make_unique<BergamotHandle>();
    auto options = marian::bergamot::parseOptionsFromFilePath(config_path);
    handle->model = std::make_shared<TranslationModel>(options, 1);
    return handle.release();
  } catch (const std::exception &exception) {
    setError(error_utf8, exception.what());
  } catch (...) {
    setError(error_utf8, "Unknown native exception while loading Bergamot.");
  }

  return nullptr;
}

int ot_bergamot_translate(
    void *handle_value,
    const char *const *inputs_utf8,
    size_t count,
    char **outputs_utf8,
    char **error_utf8) {
  if (error_utf8 != nullptr) {
    *error_utf8 = nullptr;
  }

  try {
    if (handle_value == nullptr) {
      throw std::invalid_argument("The Bergamot handle is null.");
    }
    if (count > 0 && (inputs_utf8 == nullptr || outputs_utf8 == nullptr)) {
      throw std::invalid_argument("Input and output arrays are required.");
    }

    for (size_t index = 0; index < count; ++index) {
      outputs_utf8[index] = nullptr;
    }

    std::vector<std::string> sources;
    sources.reserve(count);
    for (size_t index = 0; index < count; ++index) {
      if (inputs_utf8[index] == nullptr) {
        throw std::invalid_argument("A translation input is null.");
      }
      sources.emplace_back(inputs_utf8[index]);
    }

    std::vector<ResponseOptions> response_options(count);
    auto *handle = static_cast<BergamotHandle *>(handle_value);
    std::vector<marian::bergamot::Response> responses;
    {
      std::lock_guard<std::mutex> lock(handle->mutex);
      responses = handle->service.translateMultiple(
          handle->model, std::move(sources), response_options);
    }

    if (responses.size() != count) {
      throw std::runtime_error("Bergamot returned an unexpected response count.");
    }

    for (size_t index = 0; index < count; ++index) {
      outputs_utf8[index] = copyString(responses[index].target.text);
    }
    return 0;
  } catch (const std::exception &exception) {
    if (outputs_utf8 != nullptr) {
      for (size_t index = 0; index < count; ++index) {
        std::free(outputs_utf8[index]);
        outputs_utf8[index] = nullptr;
      }
    }
    setError(error_utf8, exception.what());
  } catch (...) {
    setError(error_utf8, "Unknown native exception while translating with Bergamot.");
  }

  return 1;
}

void ot_bergamot_free(void *memory) {
  std::free(memory);
}

void ot_bergamot_destroy(void *handle) {
  delete static_cast<BergamotHandle *>(handle);
}
