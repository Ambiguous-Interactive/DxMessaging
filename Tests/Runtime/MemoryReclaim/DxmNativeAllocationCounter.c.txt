/* Test-only Windows x64 allocation-event counter. No managed callback or heap allocation. */
typedef void (__cdecl *DxmProcedure)(void);
typedef void (__cdecl *DxmAllocationCallback)(void *, void *, void *);
typedef void (__cdecl *DxmShutdownCallback)(void *);
typedef void (__cdecl *DxmInstall)(void *, DxmShutdownCallback);
typedef void (__cdecl *DxmInstallAllocation)(DxmAllocationCallback);
typedef void (__cdecl *DxmSetEvents)(int);

__declspec(dllimport) void * __stdcall GetModuleHandleA(const char *name);
__declspec(dllimport) DxmProcedure __stdcall GetProcAddress(void *module, const char *name);
__declspec(dllimport) unsigned long __stdcall GetCurrentThreadId(void);
__int64 __cdecl _InterlockedIncrement64(volatile __int64 *value);
__int64 __cdecl _InterlockedExchange64(volatile __int64 *value, __int64 replacement);
__int64 __cdecl _InterlockedCompareExchange64(volatile __int64 *value, __int64 replacement, __int64 comparand);
#pragma intrinsic(_InterlockedIncrement64)
#pragma intrinsic(_InterlockedExchange64)
#pragma intrinsic(_InterlockedCompareExchange64)

typedef union DxmBinding {
    DxmProcedure procedure;
    DxmInstall install;
    DxmInstallAllocation installAllocation;
    DxmSetEvents setEvents;
} DxmBinding;

typedef struct DxmState {
    volatile __int64 total;
    volatile __int64 owner;
    volatile __int64 foreign;
    volatile __int64 badContext;
    volatile __int64 recording;
    unsigned long thread;
    int installed;
    int detached;
} DxmState;

__declspec(align(8)) static DxmState state;
static DxmInstallAllocation installAllocation;
static DxmSetEvents setEvents;

static __int64 Read(volatile __int64 *value)
{
    return _InterlockedCompareExchange64(value, 0, 0);
}

static void __cdecl OnAllocation(void *context, void *object, void *objectClass)
{
    (void)object;
    (void)objectClass;
    _InterlockedIncrement64(&state.total);
    if (context != &state) {
        _InterlockedIncrement64(&state.badContext);
    }
    if (Read(&state.recording) != 0) {
        if (GetCurrentThreadId() == state.thread) {
            _InterlockedIncrement64(&state.owner);
        } else {
            _InterlockedIncrement64(&state.foreign);
        }
    }
}

__declspec(dllexport) int __cdecl DxmCounterInstall(void)
{
    DxmBinding first;
    DxmBinding second;
    DxmBinding third;
    void *module;
    if (state.installed) {
        return 1;
    }
    module = GetModuleHandleA("GameAssembly.dll");
    if (module == 0) {
        return 2;
    }
    first.procedure = GetProcAddress(module, "il2cpp_profiler_install");
    second.procedure = GetProcAddress(module, "il2cpp_profiler_install_allocation");
    third.procedure = GetProcAddress(module, "il2cpp_profiler_set_events");
    if (first.procedure == 0 || second.procedure == 0 || third.procedure == 0) {
        return 3;
    }
    installAllocation = second.installAllocation;
    setEvents = third.setEvents;
    state.thread = GetCurrentThreadId();
    state.installed = 1;
    first.install(&state, (DxmShutdownCallback)0);
    installAllocation(OnAllocation);
    setEvents(128);
    return 0;
}

__declspec(dllexport) int __cdecl DxmCounterBegin(void)
{
    if (!state.installed || state.detached || Read(&state.recording) != 0) {
        return 1;
    }
    if (GetCurrentThreadId() != state.thread) {
        return 2;
    }
    _InterlockedExchange64(&state.owner, 0);
    _InterlockedExchange64(&state.foreign, 0);
    _InterlockedExchange64(&state.recording, 1);
    return 0;
}

__declspec(dllexport) __int64 __cdecl DxmCounterEnd(void)
{
    _InterlockedExchange64(&state.recording, 0);
    return Read(&state.owner);
}

__declspec(dllexport) __int64 __cdecl DxmCounterForeign(void)
{
    return Read(&state.foreign);
}

__declspec(dllexport) __int64 __cdecl DxmCounterBadContext(void)
{
    return Read(&state.badContext);
}

__declspec(dllexport) __int64 __cdecl DxmCounterTotal(void)
{
    return Read(&state.total);
}

__declspec(dllexport) int __cdecl DxmCounterDetach(void)
{
    if (!state.installed || state.detached) {
        return 1;
    }
    _InterlockedExchange64(&state.recording, 0);
    setEvents(0);
    installAllocation((DxmAllocationCallback)0);
    state.detached = 1;
    return 0;
}
