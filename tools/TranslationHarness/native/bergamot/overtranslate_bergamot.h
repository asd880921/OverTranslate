#pragma once

#include <stddef.h>

#ifdef _WIN32
#define OT_BERGAMOT_API __declspec(dllexport)
#else
#define OT_BERGAMOT_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

OT_BERGAMOT_API void *ot_bergamot_create(const char *config_path, char **error_utf8);

OT_BERGAMOT_API void *ot_bergamot_create_pivot(
    const char *source_to_pivot_config_path,
    const char *pivot_to_target_config_path,
    char **error_utf8);

OT_BERGAMOT_API int ot_bergamot_translate(
    void *handle,
    const char *const *inputs_utf8,
    size_t count,
    char **outputs_utf8,
    char **error_utf8);

OT_BERGAMOT_API void ot_bergamot_free(void *memory);

OT_BERGAMOT_API void ot_bergamot_destroy(void *handle);

#ifdef __cplusplus
}
#endif
