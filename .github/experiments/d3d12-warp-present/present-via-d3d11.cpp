// Workaround test for the DWM crash with an app-local WARP 1.0.13+ (d3d10warp.dll) on an IDD display:
// a D3D12 swap chain on screen crashes dwm.exe, a D3D11 one does not. Here D3D12 still renders every
// pixel, and D3D11 only presents the result.
//
//   present-via-d3d11 [--present d3d12|d3d11|shared|readback] [--reparent] [--seconds N]
//
//   d3d12     D3D12 renders and presents (the crash)
//   d3d11     D3D11 renders and presents
//   shared    D3D12 renders into a shared texture; D3D11 opens it, copies it into its swap chain and presents
//   readback  D3D12 renders and copies to a readback buffer; D3D11 uploads it into its swap chain and presents
//   --reparent  the swap chain's window is created under a hidden parking window, then moved into the
//               visible window with SetParent after 3 frames (Game Studio's hosting)

#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>

using Microsoft::WRL::ComPtr;

static const int BufferCount = 2;
static const DXGI_FORMAT Format = DXGI_FORMAT_B8G8R8A8_UNORM;

[[noreturn]] static void Fail(const char* what, HRESULT hr)
{
    std::printf("FAILED %s: hr=0x%08X\n", what, (unsigned)hr);
    std::fflush(stdout);
    std::exit(2);
}

#define CHECK(expr) do { HRESULT hr_ = (expr); if (FAILED(hr_)) Fail(#expr, hr_); } while (0)

static void Log(const char* what)
{
    SYSTEMTIME time;
    GetSystemTime(&time);
    std::printf("%02u:%02u:%02u.%03u UTC %s\n", time.wHour, time.wMinute, time.wSecond, time.wMilliseconds, what);
    std::fflush(stdout);
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    return message == WM_CLOSE ? 0 : DefWindowProcW(hwnd, message, wParam, lParam);
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

static void PumpUntil(HANDLE handle)
{
    while (MsgWaitForMultipleObjects(1, &handle, FALSE, INFINITE, QS_ALLINPUT) != WAIT_OBJECT_0)
        PumpMessages();
    PumpMessages();
}

enum class Present { D3D12, D3D11, Shared, Readback };

struct Renderer
{
    Present mode{};
    int width{};
    int height{};
    ComPtr<IDXGIFactory4> factory;
    ComPtr<IDXGIAdapter> adapter;

    // D3D12: renders (all modes but d3d11)
    ComPtr<ID3D12Device> device12;
    ComPtr<ID3D12CommandQueue> queue;
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    ComPtr<ID3D12Fence> fence;
    HANDLE fenceEvent{};
    UINT64 fenceValue{};
    ComPtr<ID3D12DescriptorHeap> rtvHeap;
    UINT rtvSize{};
    ComPtr<ID3D12Resource> buffers12[BufferCount]; // d3d12: the swap chain's buffers
    ComPtr<ID3D12Resource> texture12;              // shared, readback: the render target
    ComPtr<ID3D12Resource> readback;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
    UINT64 readbackSize{};

    // D3D11: presents (all modes but d3d12)
    ComPtr<ID3D11Device1> device11;
    ComPtr<ID3D11DeviceContext> context11;
    ComPtr<ID3D11Texture2D> shared11;
    ComPtr<ID3D11Query> query11;

    ComPtr<IDXGISwapChain3> swapChain;

    void WaitForGpu()
    {
        CHECK(queue->Signal(fence.Get(), ++fenceValue));
        if (fence->GetCompletedValue() < fenceValue)
        {
            CHECK(fence->SetEventOnCompletion(fenceValue, fenceEvent));
            WaitForSingleObject(fenceEvent, INFINITE);
        }
    }

    void WaitForD3D11()
    {
        context11->End(query11.Get());
        context11->Flush();
        BOOL done = FALSE;
        while (context11->GetData(query11.Get(), &done, sizeof(done), 0) == S_FALSE)
            Sleep(0);
    }

    void CreateDevices()
    {
        CHECK(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)));
        CHECK(factory->EnumWarpAdapter(IID_PPV_ARGS(&adapter)));

        if (mode != Present::D3D11)
        {
            CHECK(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device12)));
            D3D12_COMMAND_QUEUE_DESC queueDesc{};
            queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
            CHECK(device12->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue)));
            CHECK(device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence)));
            fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            CHECK(device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator)));
            CHECK(device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list)));
            CHECK(list->Close());
            D3D12_DESCRIPTOR_HEAP_DESC heapDesc{};
            heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
            heapDesc.NumDescriptors = BufferCount;
            CHECK(device12->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&rtvHeap)));
            rtvSize = device12->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        }

        if (mode != Present::D3D12)
        {
            ComPtr<ID3D11Device> device;
            D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
            CHECK(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                &level, 1, D3D11_SDK_VERSION, &device, nullptr, &context11));
            CHECK(device.As(&device11));
            D3D11_QUERY_DESC queryDesc{ D3D11_QUERY_EVENT, 0 };
            CHECK(device11->CreateQuery(&queryDesc, &query11));
        }

        wchar_t warpPath[MAX_PATH] = L"(not loaded)";
        if (HMODULE warp = GetModuleHandleW(L"d3d10warp.dll"))
            GetModuleFileNameW(warp, warpPath, MAX_PATH);
        std::printf("d3d10warp.dll: %ls\n", warpPath);
    }

    void CreateSwapChain(HWND hwnd)
    {
        RECT rect;
        GetClientRect(hwnd, &rect);
        width = rect.right;
        height = rect.bottom;

        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = width;
        desc.Height = height;
        desc.Format = Format;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = BufferCount;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        IUnknown* presenter = mode == Present::D3D12 ? (IUnknown*)queue.Get() : (IUnknown*)device11.Get();
        ComPtr<IDXGISwapChain1> swapChain1;
        CHECK(factory->CreateSwapChainForHwnd(presenter, hwnd, &desc, nullptr, nullptr, &swapChain1));
        CHECK(swapChain1.As(&swapChain));

        D3D12_CPU_DESCRIPTOR_HANDLE rtv{};
        if (device12)
            rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
        if (mode == Present::D3D12)
        {
            for (UINT i = 0; i < BufferCount; ++i)
            {
                CHECK(swapChain->GetBuffer(i, IID_PPV_ARGS(&buffers12[i])));
                device12->CreateRenderTargetView(buffers12[i].Get(), nullptr, { rtv.ptr + i * rtvSize });
            }
        }
        else if (mode == Present::Shared || mode == Present::Readback)
        {
            // The D3D12 render target: shared with D3D11, or copied to a readback buffer
            D3D12_HEAP_PROPERTIES heap{ D3D12_HEAP_TYPE_DEFAULT };
            D3D12_RESOURCE_DESC texture{};
            texture.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            texture.Width = width;
            texture.Height = height;
            texture.DepthOrArraySize = 1;
            texture.MipLevels = 1;
            texture.Format = Format;
            texture.SampleDesc.Count = 1;
            texture.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
            bool shared = mode == Present::Shared;
            CHECK(device12->CreateCommittedResource(&heap, shared ? D3D12_HEAP_FLAG_SHARED : D3D12_HEAP_FLAG_NONE, &texture,
                D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&texture12)));
            device12->CreateRenderTargetView(texture12.Get(), nullptr, rtv);

            if (shared)
            {
                HANDLE handle;
                CHECK(device12->CreateSharedHandle(texture12.Get(), nullptr, GENERIC_ALL, nullptr, &handle));
                CHECK(device11->OpenSharedResource1(handle, IID_PPV_ARGS(&shared11)));
                CloseHandle(handle);
            }
            else
            {
                device12->GetCopyableFootprints(&texture, 0, 1, 0, &footprint, nullptr, nullptr, &readbackSize);
                D3D12_HEAP_PROPERTIES readbackHeap{ D3D12_HEAP_TYPE_READBACK };
                D3D12_RESOURCE_DESC buffer{};
                buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
                buffer.Width = readbackSize;
                buffer.Height = 1;
                buffer.DepthOrArraySize = 1;
                buffer.MipLevels = 1;
                buffer.SampleDesc.Count = 1;
                buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
                CHECK(device12->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &buffer,
                    D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback)));
            }
        }
        std::printf("swap chain %dx%d\n", width, height);
        std::fflush(stdout);
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

    void Frame(UINT64 frame)
    {
        float color[4] = { (frame % 256) / 255.0f, 0.3f, 0.6f, 1.0f };

        if (mode == Present::D3D11)
        {
            ComPtr<ID3D11Texture2D> backBuffer;
            CHECK(swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer)));
            ComPtr<ID3D11RenderTargetView> rtv;
            CHECK(device11->CreateRenderTargetView(backBuffer.Get(), nullptr, &rtv));
            context11->ClearRenderTargetView(rtv.Get(), color);
            CHECK(swapChain->Present(1, 0));
            WaitForD3D11();
            return;
        }

        // D3D12 renders
        CHECK(allocator->Reset());
        CHECK(list->Reset(allocator.Get(), nullptr));
        D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
        ID3D12Resource* target = texture12.Get();
        D3D12_RESOURCE_STATES idle = D3D12_RESOURCE_STATE_COMMON;
        if (mode == Present::D3D12)
        {
            UINT index = swapChain->GetCurrentBackBufferIndex();
            target = buffers12[index].Get();
            rtv.ptr += index * rtvSize;
            idle = D3D12_RESOURCE_STATE_PRESENT;
        }
        Barrier(list.Get(), target, idle, D3D12_RESOURCE_STATE_RENDER_TARGET);
        list->ClearRenderTargetView(rtv, color, 0, nullptr);
        if (mode == Present::Readback)
        {
            Barrier(list.Get(), target, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_COPY_SOURCE);
            D3D12_TEXTURE_COPY_LOCATION destination{ readback.Get(), D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT };
            destination.PlacedFootprint = footprint;
            D3D12_TEXTURE_COPY_LOCATION source{ target, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX };
            list->CopyTextureRegion(&destination, 0, 0, 0, &source, nullptr);
            Barrier(list.Get(), target, D3D12_RESOURCE_STATE_COPY_SOURCE, idle);
        }
        else
        {
            Barrier(list.Get(), target, D3D12_RESOURCE_STATE_RENDER_TARGET, idle);
        }
        CHECK(list->Close());
        ID3D12CommandList* lists[] = { list.Get() };
        queue->ExecuteCommandLists(1, lists);

        if (mode == Present::D3D12)
        {
            CHECK(swapChain->Present(1, 0));
            WaitForGpu();
            return;
        }
        WaitForGpu();

        // D3D11 presents
        ComPtr<ID3D11Texture2D> backBuffer;
        CHECK(swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer)));
        if (mode == Present::Shared)
        {
            context11->CopyResource(backBuffer.Get(), shared11.Get());
        }
        else
        {
            void* data;
            D3D12_RANGE range{ 0, (SIZE_T)readbackSize };
            CHECK(readback->Map(0, &range, &data));
            context11->UpdateSubresource(backBuffer.Get(), 0, nullptr, (BYTE*)data + footprint.Offset, footprint.Footprint.RowPitch, 0);
            D3D12_RANGE written{ 0, 0 };
            readback->Unmap(0, &written);
        }
        CHECK(swapChain->Present(1, 0));
        WaitForD3D11();
    }
};

int main(int argc, char** argv)
{
    Renderer renderer;
    const char* present = "d3d12";
    bool reparent = false;
    int seconds = 10;
    for (int i = 1; i < argc; ++i)
    {
        if (!std::strcmp(argv[i], "--present") && i + 1 < argc) present = argv[++i];
        else if (!std::strcmp(argv[i], "--reparent")) reparent = true;
        else if (!std::strcmp(argv[i], "--seconds") && i + 1 < argc) seconds = std::atoi(argv[++i]);
    }
    if (!std::strcmp(present, "d3d12")) renderer.mode = Present::D3D12;
    else if (!std::strcmp(present, "d3d11")) renderer.mode = Present::D3D11;
    else if (!std::strcmp(present, "shared")) renderer.mode = Present::Shared;
    else if (!std::strcmp(present, "readback")) renderer.mode = Present::Readback;
    else { std::printf("unknown --present %s\n", present); return 1; }
    std::printf("present: %s%s\n", present, reparent ? ", reparent" : "");
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    renderer.CreateDevices();

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WndProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"PresentViaD3D11";
    RegisterClassW(&windowClass);
    RECT work;
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    HWND top = CreateWindowExW(0, windowClass.lpszClassName, L"Present via D3D11", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        work.left, work.top, work.right - work.left, work.bottom - work.top, nullptr, nullptr, windowClass.hInstance, nullptr);
    PumpMessages();
    RECT client;
    GetClientRect(top, &client);

    // The render thread owns the swap chain's window, as in an editor hosting a game view
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HWND child = nullptr;
    std::thread render([&]
    {
        HWND hwnd = top;
        if (reparent)
        {
            HWND parking = CreateWindowExW(0, windowClass.lpszClassName, L"Parking", WS_OVERLAPPED,
                0, 0, 300, 300, nullptr, nullptr, windowClass.hInstance, nullptr);
            hwnd = child = CreateWindowExW(0, windowClass.lpszClassName, L"", WS_CHILD,
                0, 0, client.right, client.bottom, parking, nullptr, windowClass.hInstance, nullptr);
        }
        renderer.CreateSwapChain(hwnd);
        UINT64 frame = 0;
        for (; frame < 3; ++frame)
            renderer.Frame(frame);
        Log("swap chain created and presented");
        SetEvent(ready);

        ULONGLONG end = GetTickCount64() + (ULONGLONG)seconds * 1000;
        while (GetTickCount64() < end)
        {
            PumpMessages();
            renderer.Frame(frame++);
        }
        std::printf("presented %llu frames\n", frame);
        std::fflush(stdout);
    });
    PumpUntil(ready);

    if (reparent)
    {
        SetParent(child, top);
        SetWindowPos(child, HWND_TOP, 0, 0, client.right, client.bottom, SWP_ASYNCWINDOWPOS | SWP_NOACTIVATE | SWP_NOZORDER);
        ShowWindow(child, SW_SHOWNOACTIVATE);
        Log("SetParent done, child shown");
    }

    PumpUntil(render.native_handle());
    render.join();
    return 0;
}
