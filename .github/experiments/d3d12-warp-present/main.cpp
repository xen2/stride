// Minimal D3D12 presenter on WARP: clears and presents flip-model swap chains in a loop.
// Used to check whether DWM survives it on an indirect display (IDD) without Game Studio.
//
//   d3d12-present [--seconds N] [--windows N] [--child] [--tearing] [--resize-every FRAMES] [--adapter0]
//                 [--rgba] [--colorspace] [--srgb-rtv] [--copy] [--fullscreen-desc] [--devices N] [--reparent]
//                 [--no-resize-buffers] [--frame-ms N] [--debug]
//                 [--present-before-attach N] [--attach-delay-ms N] [--quiet-ms N]
//
//   --child            swap chain on a child window, like a WPF HwndHost
//   --tearing          DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING on the swap chain (presents still use interval 1)
//   --resize-every     resize the windows (and ResizeBuffers) every N frames; with --reparent, every 500 ms
//   --adapter0         use the first DXGI adapter instead of EnumWarpAdapter
//   --rgba             R8G8B8A8_UNORM swap chain instead of B8G8R8A8_UNORM (Stride's default)
//   --colorspace       SetColorSpace1(RGB_FULL_G22_NONE_P709), as Stride does
//   --srgb-rtv         render target views in the sRGB variant of the swap chain format
//   --copy             clear an offscreen texture and copy it into the back buffer
//   --fullscreen-desc  pass a windowed DXGI_SWAP_CHAIN_FULLSCREEN_DESC, as Stride does
//   --devices N        create N-1 more idle D3D12 devices on the same adapter
//   --reparent         Game Studio's hosting: a render thread creates a hidden child window under a hidden
//                      parking window and its swap chain, then the main thread moves it into the visible
//                      window with SetParent, and keeps positioning it with SWP_ASYNCWINDOWPOS and ShowWindow
//   --no-resize-buffers  never ResizeBuffers: DWM stretches the back buffers over the window, as in Game Studio
//                        between showing the scene view and its first resize (up to 10 s)
//   --frame-ms N       wait N ms between frames (Game Studio's scene view presents rarely)
//   --debug            enable the D3D12 debug layer (Game Studio creates its device with the Debug flag)
//   --present-before-attach N  with --reparent, present N frames while still under the parking window
//   --attach-delay-ms N        with --reparent, wait N ms between SetParent and the first SetWindowPos + ShowWindow
//   --quiet-ms N               with --reparent, no present for N ms after the window is shown (Game Studio: 3-9 s)
//
// Exit code: 0 = ran to the end, 2 = a D3D12/DXGI call failed.

#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

static ID3D12Device* g_device;

[[noreturn]] static void Fail(const char* what, HRESULT hr)
{
    HRESULT reason = g_device ? g_device->GetDeviceRemovedReason() : S_OK;
    std::printf("FAILED %s: hr=0x%08X removed-reason=0x%08X\n", what, (unsigned)hr, (unsigned)reason);
    std::fflush(stdout);
    std::exit(2);
}

#define CHECK(expr) do { HRESULT hr_ = (expr); if (FAILED(hr_)) Fail(#expr, hr_); } while (0)

struct Options
{
    int seconds = 60;
    int windows = 1;
    int resizeEvery = 0;
    int devices = 1;
    bool child = false;
    bool tearing = false;
    bool adapter0 = false;
    bool rgba = false;
    bool colorSpace = false;
    bool srgbRtv = false;
    bool copy = false;
    bool fullscreenDesc = false;
    bool reparent = false;
    bool noResizeBuffers = false;
    int frameMs = 0;
    bool debug = false;
    int presentBeforeAttach = 0;
    int attachDelayMs = 0;
    int quietMs = 0;
};

static const int BufferCount = 2;

struct Target
{
    HWND top{};
    HWND hwnd{};
    RECT fullRect{};
    ComPtr<IDXGISwapChain3> swapChain;
    ComPtr<ID3D12DescriptorHeap> rtvHeap; // back buffers, then the offscreen texture
    ComPtr<ID3D12Resource> buffers[BufferCount];
    ComPtr<ID3D12Resource> offscreen;
    int width{};
    int height{};
};

static Options g_options;
static ComPtr<IDXGIFactory6> g_factory;
static ComPtr<ID3D12CommandQueue> g_queue;
static ComPtr<ID3D12CommandAllocator> g_allocator;
static ComPtr<ID3D12GraphicsCommandList> g_list;
static ComPtr<ID3D12Fence> g_fence;
static HANDLE g_fenceEvent;
static UINT64 g_fenceValue;
static UINT g_rtvSize;
static UINT g_swapChainFlags;
static DXGI_FORMAT g_format = DXGI_FORMAT_B8G8R8A8_UNORM;
static DXGI_FORMAT g_rtvFormat = DXGI_FORMAT_B8G8R8A8_UNORM;

static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_CLOSE)
        return 0;
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

static void PumpMessages()
{
    MSG msg;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

static void WaitForGpu()
{
    ++g_fenceValue;
    CHECK(g_queue->Signal(g_fence.Get(), g_fenceValue));
    if (g_fence->GetCompletedValue() < g_fenceValue)
    {
        CHECK(g_fence->SetEventOnCompletion(g_fenceValue, g_fenceEvent));
        if (WaitForSingleObject(g_fenceEvent, 10000) != WAIT_OBJECT_0)
            Fail("fence wait (10 s timeout)", E_FAIL);
    }
}

static void ClientSize(HWND hwnd, int& width, int& height)
{
    RECT rect;
    GetClientRect(hwnd, &rect);
    width = rect.right - rect.left > 8 ? rect.right - rect.left : 8;
    height = rect.bottom - rect.top > 8 ? rect.bottom - rect.top : 8;
}

static D3D12_CPU_DESCRIPTOR_HANDLE Rtv(Target& target, int index)
{
    D3D12_CPU_DESCRIPTOR_HANDLE rtv = target.rtvHeap->GetCPUDescriptorHandleForHeapStart();
    rtv.ptr += (SIZE_T)index * g_rtvSize;
    return rtv;
}

static void CreateTargets(Target& target)
{
    D3D12_RENDER_TARGET_VIEW_DESC rtvDesc{};
    rtvDesc.Format = g_rtvFormat;
    rtvDesc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
    for (int i = 0; i < BufferCount; ++i)
    {
        CHECK(target.swapChain->GetBuffer(i, IID_PPV_ARGS(&target.buffers[i])));
        g_device->CreateRenderTargetView(target.buffers[i].Get(), &rtvDesc, Rtv(target, i));
    }

    if (g_options.copy)
    {
        D3D12_HEAP_PROPERTIES heap{};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc{};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = target.width;
        desc.Height = target.height;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.Format = g_format;
        desc.SampleDesc.Count = 1;
        desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        CHECK(g_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
            D3D12_RESOURCE_STATE_RENDER_TARGET, nullptr, IID_PPV_ARGS(&target.offscreen)));
        g_device->CreateRenderTargetView(target.offscreen.Get(), &rtvDesc, Rtv(target, BufferCount));
    }
}

static void CreateSwapChain(Target& target, int index)
{
    ClientSize(target.hwnd, target.width, target.height);

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = target.width;
    desc.Height = target.height;
    desc.Format = g_format;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = BufferCount;
    desc.Scaling = DXGI_SCALING_STRETCH;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.Flags = g_swapChainFlags;
    DXGI_SWAP_CHAIN_FULLSCREEN_DESC fullscreenDesc{};
    fullscreenDesc.Windowed = TRUE;
    ComPtr<IDXGISwapChain1> swapChain;
    CHECK(g_factory->CreateSwapChainForHwnd(g_queue.Get(), target.hwnd, &desc,
        g_options.fullscreenDesc ? &fullscreenDesc : nullptr, nullptr, &swapChain));
    CHECK(swapChain.As(&target.swapChain));
    if (g_options.colorSpace)
        CHECK(target.swapChain->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709));
    CHECK(g_factory->MakeWindowAssociation(target.hwnd, DXGI_MWA_NO_ALT_ENTER));

    D3D12_DESCRIPTOR_HEAP_DESC heapDesc{};
    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    heapDesc.NumDescriptors = BufferCount + 1;
    CHECK(g_device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&target.rtvHeap)));
    CreateTargets(target);
    std::printf("window %d: swap chain %dx%d\n", index, target.width, target.height);
    std::fflush(stdout);
}

static void Resize(Target& target)
{
    // With --reparent, the main thread positions the child window
    if (target.hwnd != target.top && !g_options.reparent)
    {
        int width, height;
        ClientSize(target.top, width, height);
        MoveWindow(target.hwnd, 0, 0, width, height, TRUE);
    }

    int width, height;
    ClientSize(target.hwnd, width, height);
    if ((width == target.width && height == target.height) || g_options.noResizeBuffers)
        return;

    WaitForGpu();
    for (auto& buffer : target.buffers)
        buffer.Reset();
    target.offscreen.Reset();
    CHECK(target.swapChain->ResizeBuffers(BufferCount, width, height, DXGI_FORMAT_UNKNOWN, g_swapChainFlags));
    target.width = width;
    target.height = height;
    CreateTargets(target);
}

static void Barrier(ID3D12GraphicsCommandList* list, ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
{
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
    list->ResourceBarrier(1, &barrier);
}

// Resizes the top-level windows between their full and 3/4 size
static void ToggleSize(std::vector<Target>& targets, bool shrunk)
{
    for (Target& target : targets)
    {
        RECT rect = target.fullRect;
        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;
        if (shrunk)
        {
            width = width * 3 / 4;
            height = height * 3 / 4;
        }
        SetWindowPos(target.top, nullptr, rect.left, rect.top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}

// Clears (or copies) and presents each target once
static void RenderFrame(std::vector<Target>& targets, UINT64 frame)
{
    for (Target& target : targets)
    {
        UINT index = target.swapChain->GetCurrentBackBufferIndex();
        ID3D12Resource* backBuffer = target.buffers[index].Get();
        ID3D12GraphicsCommandList* list = g_list.Get();
        CHECK(g_allocator->Reset());
        CHECK(list->Reset(g_allocator.Get(), nullptr));

        float color[4] = { (frame % 256) / 255.0f, 0.3f, 0.6f, 1.0f };
        if (g_options.copy)
        {
            ID3D12Resource* offscreen = target.offscreen.Get();
            list->ClearRenderTargetView(Rtv(target, BufferCount), color, 0, nullptr);
            Barrier(list, offscreen, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
            Barrier(list, backBuffer, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST);
            list->CopyResource(backBuffer, offscreen);
            Barrier(list, backBuffer, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT);
            Barrier(list, offscreen, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_RENDER_TARGET);
        }
        else
        {
            Barrier(list, backBuffer, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);
            list->ClearRenderTargetView(Rtv(target, index), color, 0, nullptr);
            Barrier(list, backBuffer, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);
        }
        CHECK(list->Close());

        ID3D12CommandList* lists[] = { list };
        g_queue->ExecuteCommandLists(1, lists);
        CHECK(target.swapChain->Present(1, 0));
        WaitForGpu();
    }
}

// Renders and presents until --seconds is over. Without ownsResize, it only follows the window size.
static void RenderLoop(std::vector<Target>& targets, bool ownsResize)
{
    const Options& options = g_options;
    ULONGLONG start = GetTickCount64();
    ULONGLONG lastReport = start;
    UINT64 frames = 0;
    UINT64 lastFrames = 0;
    bool shrunk = false;
    while (GetTickCount64() - start < (ULONGLONG)options.seconds * 1000)
    {
        PumpMessages();

        if (ownsResize && options.resizeEvery > 0 && frames > 0 && frames % options.resizeEvery == 0)
        {
            shrunk = !shrunk;
            ToggleSize(targets, shrunk);
            PumpMessages();
        }
        for (Target& target : targets)
            Resize(target);

        RenderFrame(targets, frames);
        ++frames;
        if (options.frameMs > 0)
            Sleep(options.frameMs);

        ULONGLONG now = GetTickCount64();
        if (now - lastReport >= 5000)
        {
            std::printf("t=%llus frames=%llu (+%llu) swap chain %dx%d removed-reason=0x%08X\n", (now - start) / 1000,
                frames, frames - lastFrames, targets[0].width, targets[0].height, (unsigned)g_device->GetDeviceRemovedReason());
            std::fflush(stdout);
            lastReport = now;
            lastFrames = frames;
        }
    }

    WaitForGpu();
    std::printf("rendered %llu frames\n", frames);
    std::fflush(stdout);
}

// Prints the UTC time, to match against DWM crash times from the event log
static void LogTime(const char* what)
{
    SYSTEMTIME time;
    GetSystemTime(&time);
    std::printf("%02u:%02u:%02u.%03u %s\n", time.wHour, time.wMinute, time.wSecond, time.wMilliseconds, what);
    std::fflush(stdout);
}

// Pumps this thread's messages until the handle is signaled (true) or the timeout expires (false)
static bool PumpUntil(HANDLE handle, DWORD timeout)
{
    ULONGLONG end = timeout == INFINITE ? ~0ull : GetTickCount64() + timeout;
    for (;;)
    {
        ULONGLONG now = GetTickCount64();
        DWORD wait = timeout == INFINITE ? INFINITE : now >= end ? 0 : (DWORD)(end - now);
        DWORD result = MsgWaitForMultipleObjects(1, &handle, FALSE, wait, QS_ALLINPUT);
        PumpMessages();
        if (result == WAIT_OBJECT_0)
            return true;
        if (result == WAIT_TIMEOUT)
            return false;
    }
}

// GameEngineHost.UpdateWindowPosition: fill the parent's client area, asynchronously, and show
static void PlaceChild(Target& target)
{
    int width, height;
    ClientSize(target.top, width, height);
    SetWindowPos(target.hwnd, HWND_TOP, 0, 0, width, height, SWP_ASYNCWINDOWPOS | SWP_NOACTIVATE | SWP_NOZORDER);
    ShowWindow(target.hwnd, SW_SHOWNOACTIVATE);
}

static Options ParseOptions(int argc, char** argv)
{
    Options options;
    for (int i = 1; i < argc; ++i)
    {
        const char* arg = argv[i];
        if (!std::strcmp(arg, "--seconds") && i + 1 < argc) options.seconds = std::atoi(argv[++i]);
        else if (!std::strcmp(arg, "--windows") && i + 1 < argc) options.windows = std::atoi(argv[++i]);
        else if (!std::strcmp(arg, "--resize-every") && i + 1 < argc) options.resizeEvery = std::atoi(argv[++i]);
        else if (!std::strcmp(arg, "--devices") && i + 1 < argc) options.devices = std::atoi(argv[++i]);
        else if (!std::strcmp(arg, "--child")) options.child = true;
        else if (!std::strcmp(arg, "--tearing")) options.tearing = true;
        else if (!std::strcmp(arg, "--adapter0")) options.adapter0 = true;
        else if (!std::strcmp(arg, "--rgba")) options.rgba = true;
        else if (!std::strcmp(arg, "--colorspace")) options.colorSpace = true;
        else if (!std::strcmp(arg, "--srgb-rtv")) options.srgbRtv = true;
        else if (!std::strcmp(arg, "--copy")) options.copy = true;
        else if (!std::strcmp(arg, "--fullscreen-desc")) options.fullscreenDesc = true;
        else if (!std::strcmp(arg, "--reparent")) options.reparent = true;
        else if (!std::strcmp(arg, "--no-resize-buffers")) options.noResizeBuffers = true;
        else if (!std::strcmp(arg, "--frame-ms") && i + 1 < argc) options.frameMs = std::atoi(argv[++i]);
        else if (!std::strcmp(arg, "--debug")) options.debug = true;
        else if (!std::strcmp(arg, "--present-before-attach") && i + 1 < argc) options.presentBeforeAttach = std::atoi(argv[++i]);
        else if (!std::strcmp(arg, "--attach-delay-ms") && i + 1 < argc) options.attachDelayMs = std::atoi(argv[++i]);
        else if (!std::strcmp(arg, "--quiet-ms") && i + 1 < argc) options.quietMs = std::atoi(argv[++i]);
        else { std::printf("unknown argument: %s\n", arg); std::exit(1); }
    }
    return options;
}

int main(int argc, char** argv)
{
    g_options = ParseOptions(argc, argv);
    const Options& options = g_options;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    if (options.rgba)
        g_format = DXGI_FORMAT_R8G8B8A8_UNORM;
    g_rtvFormat = !options.srgbRtv ? g_format
        : options.rgba ? DXGI_FORMAT_R8G8B8A8_UNORM_SRGB : DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;

    CHECK(CreateDXGIFactory2(0, IID_PPV_ARGS(&g_factory)));

    ComPtr<IDXGIAdapter1> adapter;
    for (UINT i = 0; g_factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i)
    {
        DXGI_ADAPTER_DESC1 desc;
        adapter->GetDesc1(&desc);
        std::printf("adapter %u: %ls luid=%08X:%08X flags=0x%X\n", i, desc.Description,
            (unsigned)desc.AdapterLuid.HighPart, (unsigned)desc.AdapterLuid.LowPart, desc.Flags);
    }
    adapter.Reset();
    if (options.adapter0)
        CHECK(g_factory->EnumAdapters1(0, &adapter));
    else
        CHECK(g_factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter)));
    DXGI_ADAPTER_DESC1 adapterDesc;
    adapter->GetDesc1(&adapterDesc);
    std::printf("using: %ls luid=%08X:%08X\n", adapterDesc.Description,
        (unsigned)adapterDesc.AdapterLuid.HighPart, (unsigned)adapterDesc.AdapterLuid.LowPart);

    if (options.debug)
    {
        ComPtr<ID3D12Debug> debug;
        CHECK(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)));
        debug->EnableDebugLayer();
        std::printf("D3D12 debug layer enabled\n");
    }

    ComPtr<ID3D12Device> device;
    CHECK(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device)));
    g_device = device.Get();

    std::vector<ComPtr<ID3D12Device>> extraDevices(options.devices > 1 ? options.devices - 1 : 0);
    for (auto& extraDevice : extraDevices)
        CHECK(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&extraDevice)));

    if (options.tearing)
    {
        BOOL allowTearing = FALSE;
        g_factory->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allowTearing, sizeof(allowTearing));
        std::printf("tearing supported: %d\n", allowTearing);
        if (allowTearing)
            g_swapChainFlags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
    }

    D3D12_COMMAND_QUEUE_DESC queueDesc{};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    CHECK(device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&g_queue)));
    CHECK(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_fence)));
    g_fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    g_rtvSize = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    CHECK(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g_allocator)));
    CHECK(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_allocator.Get(), nullptr, IID_PPV_ARGS(&g_list)));
    CHECK(g_list->Close());

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WndProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_ARROW);
    windowClass.lpszClassName = L"D3D12Present";
    RegisterClassW(&windowClass);

    // Tile the windows side by side over the work area
    RECT work;
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    std::printf("work area: %ldx%ld\n", work.right - work.left, work.bottom - work.top);
    int tileWidth = (work.right - work.left) / options.windows;

    std::vector<Target> targets(options.windows);
    for (int i = 0; i < options.windows; ++i)
    {
        Target& target = targets[i];
        target.fullRect = { work.left + i * tileWidth, work.top, work.left + (i + 1) * tileWidth, work.bottom };
        target.top = CreateWindowExW(0, L"D3D12Present", L"D3D12Present", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            target.fullRect.left, target.fullRect.top, tileWidth, work.bottom - work.top,
            nullptr, nullptr, windowClass.hInstance, nullptr);
    }

    if (!options.reparent)
    {
        for (int i = 0; i < options.windows; ++i)
        {
            Target& target = targets[i];
            target.hwnd = target.top;
            if (options.child)
            {
                int width, height;
                ClientSize(target.top, width, height);
                target.hwnd = CreateWindowExW(0, L"D3D12Present", L"", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                    0, 0, width, height, target.top, nullptr, windowClass.hInstance, nullptr);
            }
            PumpMessages();
            CreateSwapChain(target, i);
        }
        RenderLoop(targets, true);
        return 0;
    }

    // Render thread: hidden child windows under a hidden parking window, as WinForms does for a
    // TopLevel = false form without a parent, then their swap chains and the render loop
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE shown = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE never = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::thread render([&]
    {
        HWND parking = CreateWindowExW(0, L"D3D12Present", L"Parking", WS_OVERLAPPED,
            0, 0, 300, 300, nullptr, nullptr, windowClass.hInstance, nullptr);
        for (int i = 0; i < options.windows; ++i)
        {
            Target& target = targets[i];
            target.hwnd = CreateWindowExW(0, L"D3D12Present", L"", WS_CHILD,
                0, 0, 512, 512, parking, nullptr, windowClass.hInstance, nullptr);
            CreateSwapChain(target, i);
        }
        for (int frame = 0; frame < options.presentBeforeAttach; ++frame)
        {
            RenderFrame(targets, frame);
            PumpUntil(never, 200);
        }
        LogTime("presented under the parking window");
        SetEvent(ready);
        if (options.quietMs > 0)
        {
            PumpUntil(shown, INFINITE);
            PumpUntil(never, options.quietMs);
            LogTime("quiet period over");
        }
        RenderLoop(targets, false);
    });
    PumpUntil(ready, INFINITE);

    // GameEngineHost.Attach
    for (Target& target : targets)
    {
        LONG style = GetWindowLongW(target.hwnd, GWL_STYLE);
        SetWindowLongW(target.hwnd, GWL_STYLE, style | WS_CHILD);
        ShowWindow(target.hwnd, SW_HIDE);
        SetParent(target.hwnd, target.top);
    }
    LogTime("SetParent done");
    if (options.attachDelayMs > 0)
        PumpUntil(never, options.attachDelayMs);
    for (Target& target : targets)
        PlaceChild(target);
    LogTime("SetWindowPos + ShowWindow done");
    SetEvent(shown);

    bool shrunk = false;
    while (!PumpUntil(render.native_handle(), 500))
    {
        if (options.resizeEvery > 0)
        {
            shrunk = !shrunk;
            ToggleSize(targets, shrunk);
        }
        for (Target& target : targets)
            PlaceChild(target);
    }
    render.join();
    return 0;
}
