#include <stddef.h>
#include <stdint.h>
#include <string.h>

#ifndef DXM_PLUGIN_ABI
#define DXM_PLUGIN_ABI 38801
#endif

#define DXM_EXPORT __attribute__((visibility("default")))

typedef struct {
    int32_t producer;
    int32_t sequence;
    int64_t value;
} dxm_payload;

_Static_assert(sizeof(dxm_payload) == 16, "payload stride");
_Static_assert(offsetof(dxm_payload, producer) == 0, "producer offset");
_Static_assert(offsetof(dxm_payload, sequence) == 4, "sequence offset");
_Static_assert(offsetof(dxm_payload, value) == 8, "value offset");

DXM_EXPORT int32_t dxm_plugin_abi(void) { return DXM_PLUGIN_ABI; }

DXM_EXPORT int32_t dxm_plugin_layout(int32_t field) {
    switch (field) {
        case 0: return (int32_t)sizeof(dxm_payload);
        case 1: return (int32_t)offsetof(dxm_payload, producer);
        case 2: return (int32_t)offsetof(dxm_payload, sequence);
        case 3: return (int32_t)offsetof(dxm_payload, value);
        default: return -1;
    }
}

#ifndef DXM_PLUGIN_NO_PROCESS
static int overlap(uintptr_t a, uintptr_t a_end, uintptr_t b, uintptr_t b_end) {
    return a < b_end && b < a_end;
}

DXM_EXPORT int32_t dxm_plugin_process(int32_t count, const dxm_payload *input,
    int64_t *output, int32_t *mode, int32_t rounds,
    int32_t input_capacity, int32_t output_capacity) {
    if (count < 0 || 4096 < count || rounds < 0 || 64 < rounds ||
        input_capacity < count || output_capacity < count) return 1;
    if (count == 0) return 0;
    uintptr_t in = (uintptr_t)input, out = (uintptr_t)output, marker = (uintptr_t)mode;
    if (in == 0 || out == 0 || marker == 0 || in % 8 != 0 || out % 8 != 0 ||
        marker % 4 != 0) return 1;
    uintptr_t in_size = (uintptr_t)count * sizeof(dxm_payload);
    uintptr_t out_size = (uintptr_t)count * sizeof(int64_t);
    if (UINTPTR_MAX - in < in_size || UINTPTR_MAX - out < out_size ||
        UINTPTR_MAX - marker < sizeof(int32_t)) return 1;
    if (overlap(in, in + in_size, out, out + out_size) ||
        overlap(in, in + in_size, marker, marker + sizeof(int32_t)) ||
        overlap(out, out + out_size, marker, marker + sizeof(int32_t))) return 1;
    for (int32_t index = 0; index < count; ++index) {
        uint64_t value = (uint64_t)input[index].value +
            (uint64_t)((int64_t)input[index].producer * 97) +
            (uint64_t)(int64_t)input[index].sequence;
        for (int32_t round = 0; round < rounds; ++round) {
            value = (value ^ (value >> 17)) * UINT64_C(6364136223846793005) +
                UINT64_C(1442695040888963407);
        }
        memcpy(&output[index], &value, sizeof(value));
    }
    *mode = 0;
    return 0;
}
#endif
