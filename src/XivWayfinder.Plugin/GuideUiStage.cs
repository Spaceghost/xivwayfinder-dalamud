using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;


namespace XivWayfinder.Plugin;

/// <summary>
/// Identifies the game's UI-shader boundary on the actual backbuffer. If that
/// boundary is absent, UiBuilder.Draw may finish the same frame before plugin
/// UI is rendered. Both paths require complete synchronous D3D state restore.
/// </summary>
internal sealed unsafe class GuideUiStage : IDisposable
{
    private delegate void SetShader(ImmediateContext* context, PixelShader* shader, void** buffers);
    private readonly Hook<SetShader> hook;
    private readonly object lifecycleGate = new();
    private int activeCallbacks;
    private readonly IPluginLog log;
    private readonly Action<nint> draw;
    private readonly Func<nint> deviceProvider;
    // These standalone pixel shaders are named by the installed game's UI
    // renderer and present in its Shader SqPack index. Native 2D UI does not use
    // ui.shpk/ui_3d.shpk; 3dui.shpk belongs to world-space 3D UI instead.
    private static readonly string[] UiPixelShaderPaths =
    [
        "shader/sm5/shcd/PrimitiveUIPS.shcd",
        "shader/sm5/shcd/PrimitiveUIMaskPS.shcd",
        "shader/sm5/shcd/PrimitiveUINaviMapPS.shcd",
        "shader/sm5/shcd/PrimitiveUIAreaMapPS.shcd",
        "shader/sm5/shcd/PrimitiveUICharaViewPS.shcd",
        "shader/sm5/shcd/FontPS.shcd",
        "shader/sm5/shcd/FontEdgePS.shcd",
        "shader/sm5/shcd/FontGlarePS.shcd",
        "shader/sm5/shcd/FontHighlightPS.shcd",
        "shader/sm5/shcd/FontEmbossPS.shcd",
    ];
    private volatile nint[] uiShaders = [];
    private int drawn = 1;
    private volatile bool disposed, faulted;
    private long frames, shaderMatches, targetMisses;
    private long fallbackFrames;
    private long nextDiscoveryAt, nextMissingLogAt;
    private int loggedContextIdentity, loggedTargetProbe;
    public string Status { get; private set; } = "Locating the game's UI shaders";
    public long Frames => Interlocked.Read(ref frames);
    public long FallbackFrames => Interlocked.Read(ref fallbackFrames);
    public bool Faulted => faulted;
    public GuideUiStage(IGameInteropProvider interop, IPluginLog log, Action<nint> draw, Func<nint> deviceProvider)
    {
        this.draw = draw; this.log = log; this.deviceProvider = deviceProvider;
        var address = (nint)ImmediateContext.MemberFunctionPointers.SetPixelShader;
        if (address == 0) throw new NotSupportedException("The game UI shader boundary could not be resolved.");
        hook = interop.HookFromAddress<SetShader>(address, Detour);
        try { hook.Enable(); }
        catch { Dispose(); throw; }
    }
    // Arm from the previous frame's UiBuilder.Draw/Present boundary, not from
    // Framework.Update: game updates and render frames need not be one-to-one.
    public void NewFrame() => Interlocked.Exchange(ref drawn, 0);

    /// <summary>
    /// Call only from UiBuilder.Draw, before NewFrame. That callback runs before
    /// ImGui submits its UI. Complete an unconsumed native frame when hidden HUD
    /// categories produced no native UI shader boundary; never draw twice.
    /// </summary>
    public void TryDrawUiFallback()
    {
        lock (lifecycleGate)
        {
            if (disposed || faulted || Volatile.Read(ref drawn) != 0 || uiShaders.Length == 0) return;
            activeCallbacks++;
        }
        try
        {
            var device = Device.Instance();
            if (device == null || device->SwapChain == null || device->SwapChain->BackBuffer == null)
            {
                MissTarget("UI fallback: game device or backbuffer is unavailable.");
                return;
            }
            var knownDevice = deviceProvider();
            if (knownDevice == 0) { MissTarget("UI fallback: Dalamud's D3D device is unavailable."); return; }
            var expectedBackBuffer = (nint)device->SwapChain->BackBuffer->D3D11Texture2D;
            var obtainedContext = false;
            GuideRenderPipeline.VisitImmediateContext(knownDevice, knownContext =>
            {
                obtainedContext = true;
                var visited = false;
                var result = GuideRenderPipeline.VisitUiBackBuffer(knownContext, expectedBackBuffer, boundContext =>
                {
                    visited = true;
                    // Shared with Detour: a native pass that already claimed
                    // this frame wins, including during model preparation.
                    if (disposed || faulted || Interlocked.CompareExchange(ref drawn, 1, 0) != 0) return false;
                    draw(boundContext);
                    Interlocked.Increment(ref frames);
                    if (Interlocked.Increment(ref fallbackFrames) == 1)
                        log.Information("XivWayfinder world renderer used the verified backbuffer before plugin UI because no native UI boundary occurred.");
                    Status = "World callback executed before plugin UI (native UI boundary absent).";
                    return true;
                }, out var diagnostic);
                if (!visited) MissTarget("UI fallback: " + diagnostic);
                return result;
            });
            if (!obtainedContext) MissTarget("UI fallback: known D3D device returned no immediate context.");
        }
        catch (Exception ex)
        {
            faulted = true;
            Status = "Render pass stopped: " + ex.GetType().Name;
            log.Error(ex, "XivWayfinder world renderer stopped after a UI fallback failure");
        }
        finally
        {
            lock (lifecycleGate)
                if (--activeCallbacks == 0) Monitor.PulseAll(lifecycleGate);
        }
    }
    public void FindShaders()
    {
        if (disposed || faulted) return;
        var now = Environment.TickCount64;
        if (now < nextDiscoveryAt) return;
        nextDiscoveryAt = now + 1000;
        var manager = ResourceManager.Instance();
        if (manager == null || manager->ResourceGraph == null)
        {
            Status = "Native UI resource manager/graph is unavailable.";
            if (now >= nextMissingLogAt)
            {
                nextMissingLogAt = now + 5000;
                log.Warning("XivWayfinder imported guide UI shader discovery: resource manager/graph unavailable");
            }
            return;
        }
        var category = ResourceCategory.Shader; uint type = 0x73686364; // 'shcd'
        var pointers = new HashSet<nint>();
        var found = new List<string>();
        var rejected = new List<string>();
        int attempts = 0, missing = 0, wrongType = 0, wrongName = 0, missingShader = 0;
        foreach (var path in UiPixelShaderPaths)
        {
            // GetResourceSync/GetResourceAsync use final-XOR CRC32 (see
            // Penumbra's ResourceLoader/Crc32); Lumina returns its complement
            // for SqPack indexes. Diagnose both hash conventions and exact
            // known spellings, always verifying the returned resource name.
            var originalCrc = Lumina.Misc.Crc32.Get(path);
            var lowercaseCrc = Lumina.Misc.Crc32.Get(path.ToLowerInvariant());
            foreach (var lookup in new[]
            {
                (Hash: ~originalCrc, Description: "original/CRC32"),
                (Hash: ~lowercaseCrc, Description: "lowercase/CRC32"),
                (Hash: originalCrc, Description: "original/SqPack"),
                (Hash: lowercaseCrc, Description: "lowercase/SqPack"),
            })
            {
                attempts++;
                var hash = lookup.Hash;
                var handle = manager->ResourceGraph->FindResourceHandle(&category, &type, &hash);
                if (handle == null) { missing++; continue; }
                var actualName = handle->FileName.Length > 256 ? "<overlong>" : handle->FileName.ToString();
                string? reason = null;
                if (handle->FileType != type || handle->Type.Category != ResourceHandleType.HandleCategory.Shader)
                { wrongType++; reason = "type"; }
                else if (!string.Equals(actualName, path, StringComparison.OrdinalIgnoreCase))
                { wrongName++; reason = "name"; }
                else if (((ShaderCodeResourceHandle*)handle)->Shader == null)
                { missingShader++; reason = "shader-null"; }
                if (reason is not null)
                {
                    if (rejected.Count < 4)
                        rejected.Add($"{path} {lookup.Description} hash={hash:X8}: {reason}, category={handle->Type.Category}, type={handle->FileType:X8}, name={actualName}, load={handle->LoadState}, read={handle->ReadState}, io={handle->LastIOResult}");
                    continue;
                }
                // This is the native Kernel::Shader object received by
                // SetPixelShader, not its embedded ID3D11PixelShader pointer.
                pointers.Add((nint)((ShaderCodeResourceHandle*)handle)->Shader);
                found.Add($"{path} (hash source: {lookup.Description})");
                break;
            }
        }
        var previous = uiShaders;
        uiShaders = pointers.ToArray(); // publish an immutable render-thread snapshot
        if (pointers.Count == 0)
        {
            Status = $"UI shader lookup: {missing}/{attempts} absent, {wrongType} type, {wrongName} name, {missingShader} shader-null.";
            if (now >= nextMissingLogAt)
            {
                nextMissingLogAt = now + 5000;
                log.Warning("XivWayfinder imported guide UI shader discovery: {Attempts} lookups, {Missing} null handles, {WrongType} wrong type, {WrongName} wrong name, {MissingShader} null shader. Rejections: {Rejected}",
                    attempts, missing, wrongType, wrongName, missingShader, string.Join("; ", rejected));
            }
            return;
        }
        if (!previous.AsSpan().SequenceEqual(uiShaders))
            log.Information("XivWayfinder imported guide: found {Count} native UI shader variants: {Paths}", uiShaders.Length, string.Join(", ", found));
        if (Frames == 0)
            Status = $"UI shaders located ({uiShaders.Length}); matches {Interlocked.Read(ref shaderMatches)}, non-backbuffer {Interlocked.Read(ref targetMisses)}.";
    }
    private void Detour(ImmediateContext* context, PixelShader* shader, void** buffers)
    {
        SetShader original;
        lock (lifecycleGate)
        {
            // Retiring the hook and choosing the continuation are one operation.
            // A callback already in the native chain may arrive after Disable/Dispose.
            original = hook.OriginalDisposeSafe;
            activeCallbacks++;
        }
        try
        {
            if (disposed || faulted || Volatile.Read(ref drawn) != 0 || context == null
                || shader == null || !uiShaders.AsSpan().Contains((nint)shader)) return;
            Interlocked.Increment(ref shaderMatches);
            var device = Device.Instance();
            if (device == null || device->SwapChain == null || device->SwapChain->BackBuffer == null)
            {
                MissTarget("Game device or backbuffer is unavailable.");
                return;
            }
            // Pointer identity only: a worker/deferred-context shader call must
            // never cause us to touch the immediate context from another thread.
            // Do not dereference the borrowed D3D interface in Kernel::ImmediateContext.
            var expectedKernelContext = device->ImmediateContext;
            if (Interlocked.Exchange(ref loggedContextIdentity, 1) == 0)
                log.Information("XivWayfinder imported guide UI context identity: hook={Hook:X}, game immediate={Expected:X}, matches={Matches}",
                    (nint)context, (nint)expectedKernelContext, context == expectedKernelContext);
            if (context != expectedKernelContext)
            {
                MissTarget("Shader call is not on the game's immediate context.");
                return;
            }
            var knownDevice = deviceProvider();
            if (knownDevice == 0) { MissTarget("Dalamud's D3D device is unavailable."); return; }
            var expectedBackBuffer = (nint)device->SwapChain->BackBuffer->D3D11Texture2D;
            var obtainedContext = false;
            GuideRenderPipeline.VisitImmediateContext(knownDevice, knownContext =>
            {
                obtainedContext = true;
                var targetMatches = GuideRenderPipeline.IsBackBufferTarget(knownContext, expectedBackBuffer, out var diagnostic);
                if (Interlocked.Exchange(ref loggedTargetProbe, 1) == 0)
                    log.Information("XivWayfinder imported guide first known-device target probe: device={Device:X}, context={Context:X}, backbuffer={BackBuffer:X}; {Diagnostic}",
                        knownDevice, knownContext, expectedBackBuffer, diagnostic);
                if (!targetMatches) { MissTarget(diagnostic); return false; }
                if (Interlocked.Exchange(ref drawn, 1) != 0) return false;
                // The scoped COM reference remains alive for both the target
                // probe and draw; no context pointer escapes this callback.
                draw(knownContext);
                Interlocked.Increment(ref frames);
                Status = "Pre-native-UI callback executed (visual verification required).";
                return true;
            });
            if (!obtainedContext) MissTarget("Known D3D device returned no immediate context.");
        }
        catch (Exception ex)
        {
            faulted = true;
            Status = "Render pass stopped: " + ex.GetType().Name;
            log.Error(ex, "XivWayfinder imported guide native UI stage stopped after a surface-pass failure");
        }
        finally
        {
            try { original(context, shader, buffers); }
            finally
            {
                lock (lifecycleGate)
                    if (--activeCallbacks == 0) Monitor.PulseAll(lifecycleGate);
            }
        }
    }
    private void MissTarget(string diagnostic)
    {
        Interlocked.Increment(ref targetMisses);
        Status = "Native UI target skipped: " + diagnostic;
    }
    public void Dispose()
    {
        lock (lifecycleGate)
        {
            disposed = true;
            if (hook.IsDisposed) return;
            hook.Disable();
            // Keep the trampoline alive through every captured continuation, including
            // recursive shader calls. Callers must not hold their render-resource lock.
            while (activeCallbacks != 0) Monitor.Wait(lifecycleGate);
            hook.Dispose();
        }
    }
}
