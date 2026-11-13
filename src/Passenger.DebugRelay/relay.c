// Passenger's debug relay. Loaded into a .NET 11 app as a profiler (DllGetClassObject), it creates the
// runtime's own ICorDebug for this process and carries its calls and callbacks to Passenger over one stream: a
// generic vtable call (object, slot, arguments) and, the other way, each managed callback with its arguments.
// It knows nothing of ICorDebug's methods beyond the callbacks' argument kinds, so the engine stays in the IDE.
#include <stdint.h>
#include <stddef.h>
#include <stdarg.h>

typedef int32_t HRESULT;
typedef struct { uint32_t a; uint16_t b, c; uint8_t d[8]; } GUID;
#define S_OK 0
#define E_FAIL ((HRESULT)0x80004005)
#define E_NOINTERFACE ((HRESULT)0x80004002)
#define E_INVALIDARG ((HRESULT)0x80070057)

// The C library, declared here: the relay is built without a platform SDK's headers.
extern void *dlopen(const char *, int);
extern void *dlsym(void *, const char *);
extern char *dlerror(void);
typedef struct { const char *dli_fname; void *dli_fbase; const char *dli_sname; void *dli_saddr; } Dl_info;
extern int dladdr(const void *, Dl_info *);
extern int getpid(void);
extern void *malloc(size_t);
extern void *calloc(size_t, size_t);
extern void free(void *);
extern void *memcpy(void *, const void *, size_t);
extern void *memset(void *, int, size_t);
extern int memcmp(const void *, const void *, size_t);
extern size_t strlen(const char *);
extern char *getenv(const char *);
extern int atoi(const char *);
extern int snprintf(char *, size_t, const char *, ...);
extern int socket(int, int, int);
extern int bind(int, const void *, unsigned);
extern int listen(int, int);
extern int accept(int, void *, void *);
extern long read(int, void *, size_t);
extern long write(int, const void *, size_t);
extern int close(int);
struct pollfd { int fd; short events; short revents; };
extern int poll(struct pollfd *, unsigned long, int);
typedef long pthread_t;
extern int pthread_create(pthread_t *, const void *, void *(*)(void *), void *);
extern int pthread_mutex_lock(void *);
extern int pthread_mutex_unlock(void *);
extern int pthread_cond_wait(void *, void *);
extern int pthread_cond_broadcast(void *);
struct timespec { long tv_sec; long tv_nsec; };
extern int clock_gettime(int, struct timespec *);
#if defined(__ANDROID__)
extern int __android_log_print(int, const char *, const char *, ...);
#define LOG(...) __android_log_print(4, "passenger-relay", __VA_ARGS__)
#define RTLD_NOW 2
#define RTLD_NOLOAD 4
#define CORECLR_NAME "libcoreclr.so"
#define RTLD_DEFAULT ((void *)0)
#define DBI_NAME "libmscordbi.so"
#define DAC_NAME "libmscordaccore.so"
#else
#include <stdio.h>
static void logf_(const char *f, ...) { va_list a; va_start(a, f); vfprintf(stderr, f, a); fputc('\n', stderr); va_end(a); }
#define LOG(...) logf_(__VA_ARGS__)
#define RTLD_NOW 2
#define RTLD_NOLOAD 0x10
#define CORECLR_NAME "libcoreclr.dylib"
#define RTLD_DEFAULT ((void *)-2)
#define DBI_NAME "libmscordbi.dylib"
#define DAC_NAME "libmscordaccore.dylib"
#endif

static const GUID IID_IUnknown = {0x00000000, 0x0000, 0x0000, {0xC0, 0, 0, 0, 0, 0, 0, 0x46}};
static const GUID IID_IClassFactory = {0x00000001, 0x0000, 0x0000, {0xC0, 0, 0, 0, 0, 0, 0, 0x46}};
static const GUID IID_ICorProfilerCallback = {0x176FBED1, 0xA55C, 0x4796, {0x98, 0xCA, 0xA9, 0xDA, 0x0E, 0xF8, 0x83, 0xE7}};
static const GUID IID_ICorProfilerCallback2 = {0x8A8CC829, 0xCCF2, 0x49fe, {0xBB, 0xAE, 0x0F, 0x02, 0x22, 0x28, 0x07, 0x1A}};
static const GUID IID_ICorDebug = {0x3D6F5F61, 0x7538, 0x11D3, {0x8D, 0x5B, 0x00, 0x10, 0x4B, 0x35, 0xE7, 0xEF}};
static const GUID IID_CB[4] = {
    {0x3D6F5F60, 0x7538, 0x11D3, {0x8D, 0x5B, 0x00, 0x10, 0x4B, 0x35, 0xE7, 0xEF}},
    {0x250E5EEA, 0xDB5C, 0x4C76, {0xB6, 0xF3, 0x8C, 0x46, 0xF1, 0x2E, 0x32, 0x03}},
    {0x264EA0FC, 0x2591, 0x49AA, {0x86, 0x8E, 0x83, 0x5E, 0x65, 0x15, 0x32, 0x3F}},
    {0x322911AE, 0x16A5, 0x49BA, {0x84, 0xA3, 0xED, 0x69, 0x67, 0x81, 0x38, 0xA3}},
};

typedef HRESULT (*AnyCall)(void *, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t,
                           uint64_t, uint64_t, uint64_t);
#define SLOT(obj, n) ((*(void ***)(obj))[n])

// ---- The stream to Passenger ----------------------------------------------------------------------------------------

static int g_conn = -1;
static uint64_t g_writeLock[8], g_stateLock[8], g_stateCond[8];
static int g_go, g_local;
static void *g_cordebug;

static int readAll(int fd, void *buf, size_t n) {
    uint8_t *p = buf;
    while (n > 0) {
        long r = read(fd, p, n);
        if (r <= 0) return 0;
        p += r; n -= (size_t)r;
    }
    return 1;
}

static int writeAll(int fd, const void *buf, size_t n) {
    const uint8_t *p = buf;
    while (n > 0) {
        long r = write(fd, p, n);
        if (r <= 0) return 0;
        p += r; n -= (size_t)r;
    }
    return 1;
}

typedef struct { uint8_t *b; size_t n, cap; } Buf;
static void put(Buf *o, const void *d, size_t n) {
    if (o->n + n > o->cap) {
        size_t cap = (o->cap ? o->cap * 2 : 256);
        while (cap < o->n + n) cap *= 2;
        uint8_t *nb = malloc(cap);
        if (o->b) { memcpy(nb, o->b, o->n); free(o->b); }
        o->b = nb; o->cap = cap;
    }
    memcpy(o->b + o->n, d, n); o->n += n;
}
static void put8(Buf *o, uint8_t v) { put(o, &v, 1); }
static void put32(Buf *o, uint32_t v) { put(o, &v, 4); }
static void put64(Buf *o, uint64_t v) { put(o, &v, 8); }

// A frame is its length, then a request id (0 for what the relay says unasked), then the body.
static void sendFrame(uint32_t id, Buf *body) {
    uint32_t len = (uint32_t)body->n + 4;
    pthread_mutex_lock(g_writeLock);
    if (g_conn >= 0) {
        writeAll(g_conn, &len, 4);
        writeAll(g_conn, &id, 4);
        writeAll(g_conn, body->b, body->n);
    }
    pthread_mutex_unlock(g_writeLock);
}

static uint64_t identityOf(void *p);

// ---- Managed callbacks, forwarded --------------------------------------------------------------------------------------

// Each callback's arguments: o an interface (kept for Passenger), i a number, s a UTF-16 string, b bytes whose
// count is the next argument.
static const char *const CB_KINDS[4][26] = {
    {"ooo", "oooi", "oo", "ooi", "ooo", "ooo", "o", "o", "oo", "oo", "oo", "oo", "oo", "oo", "oii", "ooiss", "ooiiss",
     "oo", "oo", "oo", "oo", "o", "oo", "ooo", "oooi", "oooi"},
    {"ooooi", "ois", "oi", "oi", "oooiii", "ooii", "ooo", "ooo"},
    {"oo"},
    {"o", "o", "oobi"},
};

typedef struct { void **vtbl; int iface; } CallbackObj;
static CallbackObj g_cb[4];
static void *g_cbVtbl[4][3 + 26];

static void autoContinue(int iface, int slot, uint64_t *a) {
    void *controller = (void *)(iface == 2 ? a[1] : a[0]);
    if (iface == 0 && slot == 7) return;  // ExitProcess
    if (controller) ((HRESULT(*)(void *, int))SLOT(controller, 4))(controller, 0);
}

static HRESULT onCallback(int iface, int slot, uint64_t *a) {
    const char *k = CB_KINDS[iface][slot];
    if (g_local || g_conn < 0) {
        LOG("callback %d.%d", iface + 1, slot);
        autoContinue(iface, slot, a);
        return S_OK;
    }
    Buf o = {0};
    put8(&o, 0x81);
    put8(&o, (uint8_t)(iface + 1));
    put8(&o, (uint8_t)slot);
    put8(&o, (uint8_t)strlen(k));
    for (int i = 0; k[i]; i++) {
        if (k[i] == 'o') {
            void *p = (void *)a[i];
            if (p) ((uint32_t(*)(void *))SLOT(p, 1))(p);
            put64(&o, a[i]);
            put64(&o, identityOf(p));
        } else if (k[i] == 'i') {
            put64(&o, a[i]);
        } else if (k[i] == 's') {
            const uint16_t *s = (const uint16_t *)a[i];
            uint32_t n = 0;
            while (s && s[n]) n++;
            put32(&o, n * 2);
            if (n) put(&o, s, n * 2);
        } else if (k[i] == 'b') {
            uint32_t n = (uint32_t)a[i + 1];
            put32(&o, n);
            put(&o, (const void *)a[i], n);
        }
    }
    sendFrame(0, &o);
    free(o.b);
    return S_OK;
}

#define CB(I, S)                                                                                                       \
    static HRESULT cb_##I##_##S(void *self, uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5,         \
                                uint64_t a6, uint64_t a7) {                                                            \
        (void)self;                                                                                                    \
        uint64_t a[7] = {a1, a2, a3, a4, a5, a6, a7};                                                                  \
        return onCallback(I, S, a);                                                                                    \
    }
CB(0, 0) CB(0, 1) CB(0, 2) CB(0, 3) CB(0, 4) CB(0, 5) CB(0, 6) CB(0, 7) CB(0, 8) CB(0, 9) CB(0, 10) CB(0, 11) CB(0, 12) CB(0, 13) CB(0, 14) CB(0, 15) CB(0, 16) CB(0, 17) CB(0, 18) CB(0, 19) CB(0, 20) CB(0, 21) CB(0, 22) CB(0, 23) CB(0, 24) CB(0, 25)
CB(1, 0) CB(1, 1) CB(1, 2) CB(1, 3) CB(1, 4) CB(1, 5) CB(1, 6) CB(1, 7)
CB(2, 0)
CB(3, 0) CB(3, 1) CB(3, 2)

static HRESULT cbQI(void *self, const GUID *iid, void **out) {
    for (int i = 0; i < 4; i++) {
        if (!memcmp(iid, &IID_CB[i], sizeof(GUID))) { *out = &g_cb[i]; return S_OK; }
    }
    if (!memcmp(iid, &IID_IUnknown, sizeof(GUID))) { *out = &g_cb[0]; return S_OK; }
    *out = 0;
    return E_NOINTERFACE;
}
static uint32_t one(void *self) { return 1; }

static void initCallbacks(void) {
#define F(I, S) g_cbVtbl[I][3 + S] = (void *)cb_##I##_##S;
    F(0, 0) F(0, 1) F(0, 2) F(0, 3) F(0, 4) F(0, 5) F(0, 6) F(0, 7) F(0, 8) F(0, 9) F(0, 10) F(0, 11) F(0, 12)
    F(0, 13) F(0, 14) F(0, 15) F(0, 16) F(0, 17) F(0, 18) F(0, 19) F(0, 20) F(0, 21) F(0, 22) F(0, 23) F(0, 24) F(0, 25)
    F(1, 0) F(1, 1) F(1, 2) F(1, 3) F(1, 4) F(1, 5) F(1, 6) F(1, 7)
    F(2, 0)
    F(3, 0) F(3, 1) F(3, 2)
#undef F
    for (int i = 0; i < 4; i++) {
        g_cbVtbl[i][0] = (void *)cbQI;
        g_cbVtbl[i][1] = (void *)one;
        g_cbVtbl[i][2] = (void *)one;
        g_cb[i].vtbl = g_cbVtbl[i];
        g_cb[i].iface = i;
    }
}

// ---- Calls from Passenger -------------------------------------------------------------------------------------------

enum { K_INT = 0, K_OUT = 1, K_IN = 2, K_INOUT = 3, K_OUTOBJ = 4 };

// The longest frame either side sends.
#define MAX_FRAME (64u << 20)

// An interface's identity, its IUnknown, by which Passenger knows two pointers for one object.
static uint64_t identityOf(void *p) {
    void *unk = 0;
    if (!p || ((HRESULT(*)(void *, const GUID *, void **))SLOT(p, 0))(p, &IID_IUnknown, &unk) < 0 || !unk) return 0;
    ((uint32_t(*)(void *))SLOT(unk, 2))(unk);
    return (uint64_t)unk;
}

// A call names an object (an interface pointer this relay handed out), a vtable slot and up to 11 arguments; what
// the callee writes through K_OUT and K_INOUT arguments goes back with its HRESULT. A call that runs past its frame, or
// whose answer would be longer than a frame, is refused unmade.
static void serveCall(uint32_t id, const uint8_t *p, const uint8_t *end) {
    uint64_t obj = 0; uint32_t slot = 0; uint8_t n = 0;
    uint64_t a[11] = {0};
    uint8_t kinds[11]; uint32_t sizes[11]; uint8_t *bufs[11] = {0};
    uint64_t answer = 8;
    HRESULT refused = end - p < 13 ? E_INVALIDARG : S_OK;
    if (refused == S_OK) {
        memcpy(&obj, p, 8); p += 8;
        memcpy(&slot, p, 4); p += 4;
        n = *p++;
        refused = !obj ? (HRESULT)0x80004003 /* E_POINTER */ : n > 11 ? E_INVALIDARG : S_OK;
    }
    for (int i = 0; refused == S_OK && i < n; i++) {
        if (p >= end) { refused = E_INVALIDARG; break; }
        kinds[i] = *p++; sizes[i] = 0;
        if (kinds[i] == K_INT) {
            if (end - p < 8) { refused = E_INVALIDARG; break; }
            memcpy(&a[i], p, 8); p += 8;
            continue;
        }
        if (kinds[i] > K_OUTOBJ || end - p < 4) { refused = E_INVALIDARG; break; }
        memcpy(&sizes[i], p, 4); p += 4;
        // What comes back for it: the bytes its room holds, or each object as its pointer and identity.
        answer += kinds[i] == K_IN ? 0 : kinds[i] == K_OUTOBJ ? 4 + (uint64_t)sizes[i] * 16 : 4 + (uint64_t)sizes[i];
        if (answer > MAX_FRAME) { refused = E_INVALIDARG; break; }
        if (kinds[i] == K_OUTOBJ) sizes[i] *= 8;
        int carried = kinds[i] == K_IN || kinds[i] == K_INOUT;
        if (carried && (size_t)(end - p) < sizes[i]) { refused = E_INVALIDARG; break; }
        bufs[i] = calloc(sizes[i] ? sizes[i] : 1, 1);
        if (!bufs[i]) { refused = (HRESULT)0x8007000E /* E_OUTOFMEMORY */; break; }
        if (carried) { memcpy(bufs[i], p, sizes[i]); p += sizes[i]; }
        a[i] = (uint64_t)bufs[i];
    }
    if (refused != S_OK) {
        for (int i = 0; i < 11; i++) free(bufs[i]);
        Buf o = {0}; put32(&o, (uint32_t)refused); sendFrame(id, &o); free(o.b);
        return;
    }
    HRESULT hr = ((AnyCall)SLOT((void *)obj, slot))((void *)obj, a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8],
                                                   a[9], a[10]);
    Buf o = {0};
    put32(&o, (uint32_t)hr);
    for (int i = 0; i < n; i++) {
        if (kinds[i] == K_OUT || kinds[i] == K_INOUT) { put32(&o, sizes[i]); put(&o, bufs[i], sizes[i]); }
        if (kinds[i] == K_OUTOBJ) {
            uint32_t count = sizes[i] / 8;
            put32(&o, count);
            for (uint32_t j = 0; j < count; j++) {
                uint64_t ptr; memcpy(&ptr, bufs[i] + j * 8, 8);
                put64(&o, ptr);
                // An enumerator that fails partway has handed out the elements before the failure.
                put64(&o, identityOf((void *)ptr));
            }
        }
        free(bufs[i]);
    }
    sendFrame(id, &o);
    free(o.b);
}

static void setGo(void) {
    pthread_mutex_lock(g_stateLock);
    g_go = 1;
    pthread_cond_broadcast(g_stateCond);
    pthread_mutex_unlock(g_stateLock);
}

static void *serve(void *unused) {
    for (;;) {
        uint32_t len, id;
        if (!readAll(g_conn, &len, 4) || len < 5 || len > MAX_FRAME) break;
        uint8_t *body = malloc(len);
        if (!readAll(g_conn, body, len)) { free(body); break; }
        memcpy(&id, body, 4);
        uint8_t op = body[4];
        if (op == 1) serveCall(id, body + 5, body + len);
        else if (op == 2) {
            Buf o = {0}; put32(&o, 0); sendFrame(id, &o); free(o.b);
            setGo();
        } else if (op == 3) {
            // Objects Passenger let go of, each one reference, answered with nothing.
            uint32_t count;
            if (len < 9) { free(body); continue; }
            memcpy(&count, body + 5, 4);
            for (uint32_t i = 0; i < count && 9 + (size_t)(i + 1) * 8 <= len; i++) {
                void *each;
                memcpy(&each, body + 9 + (size_t)i * 8, 8);
                if (each) ((uint32_t(*)(void *))SLOT(each, 2))(each);
            }
        }
        free(body);
    }
    LOG("Passenger's connection ended");
    pthread_mutex_lock(g_writeLock);
    close(g_conn); g_conn = -1;
    pthread_mutex_unlock(g_writeLock);
    setGo();
    return 0;
}

// ---- Start ----------------------------------------------------------------------------------------------------------

static HRESULT createCordb(void) {
    void *dbi = dlopen(DBI_NAME, RTLD_NOW);
    if (!dbi) { LOG("dlopen %s: %s", DBI_NAME, dlerror()); return E_FAIL; }
    // The DBI starts its platform layer and its transport in DllMain, which only a PAL loader calls.
    int (*dllMain)(void *, uint32_t, void *) = dlsym(dbi, "DllMain");
    HRESULT (*create)(int, uint32_t, const uint16_t *, const uint16_t *, void *, void **) =
        dlsym(dbi, "CoreCLRCreateCordbObject3");
    void *clr = dlopen(CORECLR_NAME, RTLD_NOW | RTLD_NOLOAD);
    // A runtime linked into the executable (iOS's static linking) is found among everything loaded.
    void *clrSym = dlsym(clr ? clr : RTLD_DEFAULT, "coreclr_initialize");
    Dl_info info = {0}, dbiInfo = {0};
    if (!dllMain || !create || !clrSym || !dladdr(clrSym, &info) || !dladdr((void *)create, &dbiInfo)) {
        LOG("no DBI entry points or runtime");
        return E_FAIL;
    }
    if (!dllMain(dbi, 1 /* DLL_PROCESS_ATTACH */, 0)) { LOG("the DBI's DllMain failed"); return E_FAIL; }
    // The DAC beside the DBI, named in UTF-16 as the DBI takes paths.
    static uint16_t dacPath[1024];
    const char *dir = dbiInfo.dli_fname;
    size_t cut = strlen(dir);
    while (cut > 0 && dir[cut - 1] != '/') cut--;
    const char *dacName = DAC_NAME;
    size_t n = 0;
    for (size_t i = 0; i < cut && n < 1000; i++) dacPath[n++] = (uint8_t)dir[i];
    for (size_t i = 0; dacName[i] && n < 1020; i++) dacPath[n++] = (uint8_t)dacName[i];
    dacPath[n] = 0;
    void *unk = 0;
    HRESULT hr = create(4, (uint32_t)getpid(), 0, dacPath, info.dli_fbase, &unk);
    if (hr < 0 || !unk) { LOG("CoreCLRCreateCordbObject3: 0x%x", hr); return hr < 0 ? hr : E_FAIL; }
    hr = ((HRESULT(*)(void *, const GUID *, void **))SLOT(unk, 0))(unk, &IID_ICorDebug, &g_cordebug);
    LOG("ICorDebug for pid %d: 0x%x (runtime at %p, DBI %s)", getpid(), hr, info.dli_fbase, dbiInfo.dli_fname);
    return hr;
}

static void attachLocally(void) {
    HRESULT hr = ((HRESULT(*)(void *))SLOT(g_cordebug, 3))(g_cordebug);
    LOG("Initialize: 0x%x", hr);
    hr = ((HRESULT(*)(void *, void *))SLOT(g_cordebug, 5))(g_cordebug, &g_cb[0]);
    LOG("SetManagedHandler: 0x%x", hr);
    void *process = 0;
    hr = ((HRESULT(*)(void *, uint32_t, int, void **))SLOT(g_cordebug, 8))(g_cordebug, (uint32_t)getpid(), 0, &process);
    LOG("DebugActiveProcess: 0x%x", hr);
}

#if defined(__APPLE__)
// A simulator shares the Mac's network and usbmux reaches a device's ports: TCP on the loopback, PASSENGER_DEBUG_PORT.
static int listenSocket(char *said, size_t saidSize) {
    const char *port = getenv("PASSENGER_DEBUG_PORT");
    int p = port ? atoi(port) : 5599;
    int s = socket(2 /* AF_INET */, 1 /* SOCK_STREAM */, 0);
    struct { uint8_t len, family; uint16_t port; uint32_t addr; uint8_t zero[8]; } addr;
    memset(&addr, 0, sizeof addr);
    addr.len = sizeof addr;
    addr.family = 2;
    addr.port = (uint16_t)((p >> 8) | ((p & 0xff) << 8));
    addr.addr = 0x0100007f;  // 127.0.0.1
    snprintf(said, saidSize, "127.0.0.1:%d", p);
    if (s < 0 || bind(s, &addr, sizeof addr) < 0 || listen(s, 1) < 0) { if (s >= 0) close(s); return -1; }
    return s;
}
#else
// Android: an abstract socket, no file, which adb forward tcp:N localabstract:NAME reaches.
static int listenSocket(char *said, size_t saidSize) {
    const char *name = getenv("PASSENGER_DEBUG_SOCKET");
    if (!name || !*name) name = "passenger-debug";
    int s = socket(1 /* AF_UNIX */, 1 /* SOCK_STREAM */, 0);
    struct { uint16_t family; char path[108]; } addr;
    memset(&addr, 0, sizeof addr);
    addr.family = 1;
    size_t n = strlen(name);
    if (n > 100) n = 100;
    memcpy(addr.path + 1, name, n);
    snprintf(said, saidSize, "@%s", name);
    if (s < 0 || bind(s, &addr, (unsigned)(2 + 1 + n)) < 0 || listen(s, 1) < 0) { if (s >= 0) close(s); return -1; }
    return s;
}
#endif

static long nowMs(void) {
    struct timespec t;
    clock_gettime(1 /* CLOCK_MONOTONIC */, &t);
    return t.tv_sec * 1000 + t.tv_nsec / 1000000;
}

// Whether a client's first frame (u32 length, u32 id, 4, the token) carries the token the app's environment gave,
// read within a few seconds; compared whole, whatever differs, so its time says nothing of where.
static int givesToken(int c, const char *token) {
    struct pollfd pfd = {c, 1, 0};
    uint32_t len;
    if (poll(&pfd, 1, 5000) <= 0 || !readAll(c, &len, 4)) return 0;
    size_t n = strlen(token);
    if (len != 4 + 1 + n) return 0;
    uint8_t *body = malloc(len);
    if (!readAll(c, body, len)) { free(body); return 0; }
    uint8_t differs = body[4] ^ 4;
    for (size_t i = 0; i < n; i++) differs |= body[5 + i] ^ (uint8_t)token[i];
    free(body);
    return differs == 0;
}

// Takes the first client that gives PASSENGER_DEBUG_TOKEN, where the environment names one: any other is closed unheard.
static int waitForPassenger(int waitMs) {
    char said[128];
    int s = listenSocket(said, sizeof said);
    if (s < 0) { LOG("listen on %s failed", said); return 0; }
    const char *token = getenv("PASSENGER_DEBUG_TOKEN");
    LOG("waiting %d ms for Passenger on %s", waitMs, said);
    long end = nowMs() + waitMs;
    for (;;) {
        long left = end - nowMs();
        struct pollfd pfd = {s, 1, 0};
        if (left <= 0 || poll(&pfd, 1, (int)left) <= 0) { LOG("Passenger did not come"); close(s); return 0; }
        int c = accept(s, 0, 0);
        if (c < 0) continue;
        if (token && *token && !givesToken(c, token)) {
            LOG("a client without the session's token was turned away");
            close(c);
            continue;
        }
        g_conn = c;
        close(s);
        return 1;
    }
}

static HRESULT start(void) {
    initCallbacks();
    const char *local = getenv("PASSENGER_DEBUG_LOCAL");
    g_local = local && *local == '1';
    const char *wait = getenv("PASSENGER_DEBUG_WAIT_MS");
    int waitMs = wait ? atoi(wait) : 60000;
    if (createCordb() < 0) return S_OK;
    if (g_local) { attachLocally(); return S_OK; }
    if (!waitForPassenger(waitMs)) return S_OK;
    Buf o = {0};
    put8(&o, 0x80);
    put32(&o, (uint32_t)getpid());
    put64(&o, (uint64_t)g_cordebug);
    put64(&o, (uint64_t)&g_cb[0]);
    sendFrame(0, &o);
    free(o.b);
    pthread_t t;
    pthread_create(&t, 0, serve, 0);
    // The runtime goes on only once Passenger attached and said so, so no module loads before its breakpoints.
    pthread_mutex_lock(g_stateLock);
    while (!g_go) pthread_cond_wait(g_stateCond, g_stateLock);
    pthread_mutex_unlock(g_stateLock);
    LOG("startup goes on");
    return S_OK;
}

// ---- The profiler CoreCLR loads ----------------------------------------------------------------------------------------

typedef struct { void **vtbl; } Obj;
static void *g_profVtbl[160];
static Obj g_profiler = {g_profVtbl};
static HRESULT ok(void *self) { return S_OK; }
static HRESULT profQI(void *self, const GUID *iid, void **out) {
    if (!memcmp(iid, &IID_IUnknown, sizeof(GUID)) || !memcmp(iid, &IID_ICorProfilerCallback, sizeof(GUID))
        || !memcmp(iid, &IID_ICorProfilerCallback2, sizeof(GUID))) {
        *out = &g_profiler;
        return S_OK;
    }
    *out = 0;
    return E_NOINTERFACE;
}
static HRESULT profInitialize(void *self, void *info) { return start(); }

static void *g_factoryVtbl[5];
static Obj g_factory = {g_factoryVtbl};
static HRESULT factoryQI(void *self, const GUID *iid, void **out) {
    if (!memcmp(iid, &IID_IUnknown, sizeof(GUID)) || !memcmp(iid, &IID_IClassFactory, sizeof(GUID))) {
        *out = &g_factory;
        return S_OK;
    }
    *out = 0;
    return E_NOINTERFACE;
}
static HRESULT factoryCreate(void *self, void *outer, const GUID *iid, void **out) { return profQI(0, iid, out); }

__attribute__((visibility("default"))) HRESULT DllGetClassObject(const GUID *clsid, const GUID *iid, void **out) {
    for (int i = 0; i < 160; i++) g_profVtbl[i] = (void *)ok;
    g_profVtbl[0] = (void *)profQI;
    g_profVtbl[1] = (void *)one;
    g_profVtbl[2] = (void *)one;
    g_profVtbl[3] = (void *)profInitialize;
    g_factoryVtbl[0] = (void *)factoryQI;
    g_factoryVtbl[1] = (void *)one;
    g_factoryVtbl[2] = (void *)one;
    g_factoryVtbl[3] = (void *)factoryCreate;
    g_factoryVtbl[4] = (void *)ok;
    return factoryQI(0, iid, out);
}
