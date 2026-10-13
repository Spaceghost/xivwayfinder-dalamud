using System.Runtime.InteropServices;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using TerraFX.Interop.DirectX;
using XivWayfinder.Core;
using XivWayfinder.Shared;

namespace XivWayfinder.Plugin;

/// <summary>
/// Shared custom-world depth, independent of the game's scene-depth texture.
/// Acquire only synchronously from a verified native or pre-Present fallback
/// immediate-context pass on the actual backbuffer. Returned DSVs own one COM reference: callers
/// must restore their targets before Release, and must NEVER clear this view.
/// All participants use D32_FLOAT, LESS_EQUAL and distance/(1+distance).
/// This resolves opaque/masked coverage; it is not order-independent alpha.
/// </summary>
internal sealed unsafe class GuideWorldDepth : IDisposable
{
    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog? log;
    private readonly ICallGateProvider<nint, uint, uint, nint> provider;
    private readonly object gate = new();
    private readonly GuideDepthFrame frame = new();
    private ID3D11Texture2D* texture;
    private ID3D11DepthStencilView* view;
    private nint device;
    private uint width, height;
    private bool disposed, registered;
    private nint observedSwapChain;
    private uint observedPresentCount;
    private bool loggedAdvancement;
    private long nextSourceLog, nextFailureLog, acquisitions, clears;

    public GuideWorldDepth(IDalamudPluginInterface pi, IPluginLog? log = null)
    {
        this.pi = pi;
        this.log = log;
        provider = pi.GetIpcProvider<nint, uint, uint, nint>(IpcContract.AcquireWorldDepth);
        try
        {
            provider.RegisterFunc(Acquire);
            registered = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public nint Acquire(nint devicePointer, uint requestedWidth, uint requestedHeight)
    {
        lock (gate)
        {
            if (disposed || devicePointer == 0
                || devicePointer != pi.UiBuilder.DeviceHandle
                || requestedWidth is 0 or > 16384 || requestedHeight is 0 or > 16384) return 0;
            // Dalamud's matched Present detour runs UiBuilder.Draw BEFORE the
            // original Present. Unlike per-plugin Draw counters, this DXGI
            // count identifies one frame for both native hooks and fallbacks.
            // DXVK implements this with the submitted presenter frame ID.
            var gameDevice = Device.Instance();
            if (gameDevice == null || gameDevice->SwapChain == null || gameDevice->SwapChain->DXGISwapChain == null) return 0;
            var swapChain = (IDXGISwapChain*)gameDevice->SwapChain->DXGISwapChain;
            uint lastPresentCount = 0;
            swapChain->AddRef();
            int result;
            try { result = swapChain->GetLastPresentCount(&lastPresentCount); }
            finally { swapChain->Release(); }
            if (result < 0)
            {
                var now = Environment.TickCount64;
                if (now >= nextFailureLog)
                {
                    nextFailureLog = now + 20000;
                    log?.Warning("Wayfinder shared world depth unavailable: DXGI GetLastPresentCount failed ({HResult:X8}); consumers use isolated depth.", result);
                }
                return 0;
            }
            if (!frame.BeginFrame((nint)swapChain, lastPresentCount)) return 0;
            TraceFrameSource((nint)swapChain, lastPresentCount);
            var requestedDevice = (ID3D11Device*)devicePointer;
            if (view == null || device != devicePointer || width != requestedWidth || height != requestedHeight)
                Create(requestedDevice, requestedWidth, requestedHeight);
            if (frame.NeedsClear)
            {
                ID3D11DeviceContext* context = null;
                requestedDevice->GetImmediateContext(&context);
                if (context == null) return 0;
                try
                {
                    context->ClearDepthStencilView(view, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 1, 0);
                    frame.MarkCleared();
                    clears++;
                }
                finally { context->Release(); }
            }
            // Provider teardown or resize may now release our own reference,
            // but an in-flight consumer keeps this resource alive until restore.
            view->AddRef();
            acquisitions++;
            return (nint)view;
        }
    }

    private void TraceFrameSource(nint swapChain, uint presentCount)
    {
        var now = Environment.TickCount64;
        if (observedSwapChain != swapChain)
        {
            observedSwapChain = swapChain;
            observedPresentCount = presentCount;
            loggedAdvancement = false;
            nextSourceLog = now + 20000;
            log?.Information("Wayfinder shared world depth frame source: DXGI swapchain={SwapChain:X}, presentCount={PresentCount}.", swapChain, presentCount);
        }
        else if (presentCount != observedPresentCount)
        {
            if (!loggedAdvancement)
            {
                loggedAdvancement = true;
                log?.Information("Wayfinder shared world depth presentation count advanced: {Previous}->{Current}.", observedPresentCount, presentCount);
            }
            observedPresentCount = presentCount;
        }
        if (now >= nextSourceLog)
        {
            nextSourceLog = now + 20000;
            log?.Information("Wayfinder shared world depth: presentCount={PresentCount}, acquisitions={Acquisitions}, clears={Clears}, countAdvanced={Advanced}.", presentCount, acquisitions, clears, loggedAdvancement);
        }
    }

    private void Create(ID3D11Device* requestedDevice, uint requestedWidth, uint requestedHeight)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = requestedWidth, Height = requestedHeight, MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL,
        };
        ID3D11Texture2D* nextTexture = null;
        ID3D11DepthStencilView* nextView = null;
        try
        {
            Check(requestedDevice->CreateTexture2D(&desc, null, &nextTexture));
            Check(requestedDevice->CreateDepthStencilView((ID3D11Resource*)nextTexture, null, &nextView));
        }
        catch
        {
            if (nextView != null) nextView->Release();
            if (nextTexture != null) nextTexture->Release();
            throw;
        }
        ReleaseResource();
        texture = nextTexture;
        view = nextView;
        device = (nint)requestedDevice;
        width = requestedWidth;
        height = requestedHeight;
        frame.ResourceChanged();
    }

    private void ReleaseResource()
    {
        if (view != null) { view->Release(); view = null; }
        if (texture != null) { texture->Release(); texture = null; }
        device = 0;
        width = height = 0;
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    public void Dispose()
    {
        bool unregister;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            frame.Dispose();
            ReleaseResource();
            unregister = registered;
            registered = false;
        }
        // Do not hold the resource lock while touching the call gate. A delegate
        // already captured by another caller safely observes disposed above.
        if (unregister) provider.UnregisterFunc();
    }
}
