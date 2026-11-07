using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace XivWayfinder.Plugin;

[StructLayout(LayoutKind.Sequential)]
internal struct GuideVertex(Vector3 position, Vector3 normal, Vector4 color, Vector2 uv)
{
    public Vector3 Position = position, Normal = normal;
    public Vector4 Color = color;
    public Vector2 UV = uv;
}
internal readonly record struct GuideBatch(int Start, int Count, nint Texture, float AlphaCutoff, bool Unlit, bool Transparent,
    int WrapU = 10497, int WrapV = 10497);

/// <summary>World-space model pass. Scene depth occludes models; a private depth
/// buffer resolves their own triangles and participating custom world surfaces.
/// Never writes the game's depth texture.</summary>
internal sealed unsafe class GuideGpu(Func<nint, uint, uint, nint>? acquireWorldDepth = null) : IDisposable
{
    private ID3D11Device* device;
    private ID3D11VertexShader* vertex;
    private ID3D11PixelShader* pixel;
    private ID3D11Buffer* constants;
    private ID3D11Buffer* vertices;
    private ID3D11InputLayout* layout;
    private readonly nint[] samplers = new nint[9];
    private ID3D11DepthStencilState* depthState;
    private ID3D11DepthStencilState* transparentDepthState;
    private ID3D11Texture2D* ownDepth;
    private ID3D11DepthStencilView* ownDepthView;
    private uint width, height;
    private const int Capacity = Core.GuideMesh.MaxTriangles * 3;
    public long Submissions { get; private set; }
    public long SharedDepthSubmissions { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProjection, InverseViewProjection;
        public Vector4 Camera, View, Material;
    }

    public void Draw(nint devicePointer, ReadOnlySpan<GuideVertex> data, ReadOnlySpan<GuideBatch> batches, float opacity)
    {
        if (data.Length == 0 || data.Length > Capacity || devicePointer == 0) return;
        var control = Control.Instance(); var targets = RenderTargetManager.Instance();
        var manager = CameraManager.Instance(); var camera = manager == null ? null : manager->GetActiveCamera();
        if (control == null || camera == null || targets == null || targets->DepthStencil == null) return;
        var depth = targets->DepthStencil;
        if (depth->D3D11ShaderResourceView == null || depth->ActualWidth == 0 || depth->ActualHeight == 0
            || depth->ActualWidth > depth->AllocatedWidth || depth->ActualHeight > depth->AllocatedHeight) return;
        Matrix4x4 vp = control->ViewProjectionMatrix;
        if (!Matrix4x4.Invert(vp, out var inverse)) return;
        if ((nint)device != devicePointer) { Dispose(); Create((ID3D11Device*)devicePointer); }
        ID3D11DeviceContext* baseContext = null;
        device->GetImmediateContext(&baseContext);
        var iid = IID.IID_ID3D11DeviceContext1; void* queried = null;
        var hr = baseContext->QueryInterface(&iid, &queried); baseContext->Release(); Check(hr);
        var ctx = (ID3D11DeviceContext1*)queried;
        ID3D11DepthStencilView* sharedDepth = null;
        try
        {
            var viewport = new D3D11_VIEWPORT(); uint viewportCount = 1;
            ctx->RSGetViewports(&viewportCount, &viewport);
            if (viewportCount != 1 || viewport.TopLeftX != 0 || viewport.TopLeftY != 0
                || viewport.Width <= 0 || viewport.Height <= 0 || viewport.Width > 16384 || viewport.Height > 16384) return;
            sharedDepth = (ID3D11DepthStencilView*)(acquireWorldDepth?.Invoke(devicePointer, (uint)viewport.Width, (uint)viewport.Height) ?? 0);
            if (sharedDepth == null) EnsureDepth((uint)viewport.Width, (uint)viewport.Height);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(ctx->Map((ID3D11Resource*)vertices, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
            data.CopyTo(new Span<GuideVertex>(mapped.pData, data.Length));
            ctx->Unmap((ID3D11Resource*)vertices, 0);

            ID3D11PixelShader* oldPs = null; ID3D11VertexShader* oldVs = null; ID3D11GeometryShader* oldGs = null;
            ID3D11ClassInstance** psClasses = stackalloc ID3D11ClassInstance*[256];
            ID3D11ClassInstance** vsClasses = stackalloc ID3D11ClassInstance*[256];
            ID3D11ClassInstance** gsClasses = stackalloc ID3D11ClassInstance*[256];
            uint psCount = 256, vsCount = 256, gsCount = 256;
            ctx->PSGetShader(&oldPs, psClasses, &psCount); ctx->VSGetShader(&oldVs, vsClasses, &vsCount); ctx->GSGetShader(&oldGs, gsClasses, &gsCount);
            ID3D11ShaderResourceView** oldViews = stackalloc ID3D11ShaderResourceView*[2]; ctx->PSGetShaderResources(0, 2, oldViews);
            ID3D11SamplerState* oldSampler = null; ctx->PSGetSamplers(0, 1, &oldSampler);
            ID3D11Buffer* oldPsBuffer = null; ID3D11Buffer* oldVsBuffer = null;
            uint psFirst = 0, psLength = 0, vsFirst = 0, vsLength = 0;
            ctx->PSGetConstantBuffers1(0, 1, &oldPsBuffer, &psFirst, &psLength);
            ctx->VSGetConstantBuffers1(0, 1, &oldVsBuffer, &vsFirst, &vsLength);
            ID3D11InputLayout* oldLayout = null; D3D_PRIMITIVE_TOPOLOGY oldTopology;
            ctx->IAGetInputLayout(&oldLayout); ctx->IAGetPrimitiveTopology(&oldTopology);
            ID3D11Buffer* oldVertices = null; uint oldStride = 0, oldOffset = 0;
            ctx->IAGetVertexBuffers(0, 1, &oldVertices, &oldStride, &oldOffset);
            ID3D11RenderTargetView* target = null; ID3D11DepthStencilView* oldDepth = null;
            ctx->OMGetRenderTargets(1, &target, &oldDepth);
            ID3D11DepthStencilState* oldDepthState = null; uint oldStencil = 0; ctx->OMGetDepthStencilState(&oldDepthState, &oldStencil);
            try
            {
                if (sharedDepth == null)
                    ctx->ClearDepthStencilView(ownDepthView, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 1, 0);
                ctx->OMSetRenderTargets(1, &target, sharedDepth != null ? sharedDepth : ownDepthView);
                ctx->OMSetDepthStencilState(depthState, 0);
                var cb = constants; var vb = vertices; uint stride = (uint)sizeof(GuideVertex), offset = 0;
                ctx->VSSetConstantBuffers(0, 1, &cb); ctx->PSSetConstantBuffers(0, 1, &cb);
                ctx->VSSetShader(vertex, null, 0); ctx->PSSetShader(pixel, null, 0); ctx->GSSetShader(null, null, 0);
                ctx->IASetInputLayout(layout); ctx->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
                ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                var values = new Constants { ViewProjection = vp, InverseViewProjection = inverse,
                    Camera = new((Vector3)camera->SceneCamera.Position, 1),
                    View = new(viewport.Width, viewport.Height, depth->ActualWidth, depth->ActualHeight) };
                ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[2];
                views[0] = (ID3D11ShaderResourceView*)depth->D3D11ShaderResourceView;
                foreach (var batch in batches)
                {
                    if (batch.Start < 0 || batch.Count <= 0 || batch.Start > data.Length - batch.Count) continue;
                    var sample = Sampler(batch.WrapU, batch.WrapV); ctx->PSSetSamplers(0, 1, &sample);
                    ctx->OMSetDepthStencilState(batch.Transparent ? transparentDepthState : depthState, 0);
                    values.Material = new(batch.Texture != 0 ? 1 : 0, opacity, batch.AlphaCutoff, batch.Unlit ? 1 : 0);
                    Check(ctx->Map((ID3D11Resource*)constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
                    *(Constants*)mapped.pData = values; ctx->Unmap((ID3D11Resource*)constants, 0);
                    views[1] = (ID3D11ShaderResourceView*)batch.Texture;
                    ctx->PSSetShaderResources(0, 2, views);
                    ctx->Draw((uint)batch.Count, (uint)batch.Start);
                }
                Submissions++;
                if (sharedDepth != null) SharedDepthSubmissions++;
            }
            finally
            {
                ctx->PSSetShaderResources(0, 2, oldViews);
                ctx->OMSetRenderTargets(1, &target, oldDepth); ctx->OMSetDepthStencilState(oldDepthState, oldStencil);
                ctx->VSSetShader(oldVs, vsClasses, vsCount); ctx->PSSetShader(oldPs, psClasses, psCount); ctx->GSSetShader(oldGs, gsClasses, gsCount);
                ctx->PSSetSamplers(0, 1, &oldSampler);
                ctx->PSSetConstantBuffers1(0, 1, &oldPsBuffer, &psFirst, &psLength); ctx->VSSetConstantBuffers1(0, 1, &oldVsBuffer, &vsFirst, &vsLength);
                ctx->IASetInputLayout(oldLayout); ctx->IASetPrimitiveTopology(oldTopology); ctx->IASetVertexBuffers(0, 1, &oldVertices, &oldStride, &oldOffset);
                if (oldPs != null) oldPs->Release(); if (oldVs != null) oldVs->Release(); if (oldGs != null) oldGs->Release();
                if (oldPsBuffer != null) oldPsBuffer->Release(); if (oldVsBuffer != null) oldVsBuffer->Release();
                if (oldLayout != null) oldLayout->Release(); if (oldVertices != null) oldVertices->Release();
                if (oldSampler != null) oldSampler->Release(); if (target != null) target->Release();
                if (oldDepth != null) oldDepth->Release(); if (oldDepthState != null) oldDepthState->Release();
                for (var i = 0; i < 2; i++) if (oldViews[i] != null) oldViews[i]->Release();
                for (var i = 0; i < psCount; i++) psClasses[i]->Release();
                for (var i = 0; i < vsCount; i++) vsClasses[i]->Release();
                for (var i = 0; i < gsCount; i++) gsClasses[i]->Release();
            }
        }
        finally
        {
            if (sharedDepth != null) sharedDepth->Release();
            ctx->Release();
        }
    }

    private void Create(ID3D11Device* value)
    {
        device = value; device->AddRef();
        try
        {
            var vs = Bytecode("GuideVertex"); var ps = Bytecode("GuidePixel");
            ID3D11VertexShader* v = null; ID3D11PixelShader* p = null;
            fixed (byte* b = vs) Check(device->CreateVertexShader(b, (nuint)vs.Length, null, &v)); vertex = v;
            fixed (byte* b = ps) Check(device->CreatePixelShader(b, (nuint)ps.Length, null, &p)); pixel = p;
            var desc = new D3D11_BUFFER_DESC { Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE, BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER, ByteWidth = (uint)sizeof(Constants) };
            ID3D11Buffer* buffer = null; Check(device->CreateBuffer(&desc, null, &buffer)); constants = buffer;
            desc.ByteWidth = (uint)(Capacity * sizeof(GuideVertex)); desc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER;
            buffer = null; Check(device->CreateBuffer(&desc, null, &buffer)); vertices = buffer;
            fixed (byte* position = "POSITION\0"u8, normal = "NORMAL\0"u8, color = "COLOR\0"u8, uv = "TEXCOORD\0"u8)
            fixed (byte* bytecode = vs)
            {
                D3D11_INPUT_ELEMENT_DESC* elements = stackalloc D3D11_INPUT_ELEMENT_DESC[4];
                elements[0] = new() { SemanticName = (sbyte*)position, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT, AlignedByteOffset = 0 };
                elements[1] = new() { SemanticName = (sbyte*)normal, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT, AlignedByteOffset = 12 };
                elements[2] = new() { SemanticName = (sbyte*)color, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT, AlignedByteOffset = 24 };
                elements[3] = new() { SemanticName = (sbyte*)uv, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, AlignedByteOffset = 40 };
                ID3D11InputLayout* l = null; Check(device->CreateInputLayout(elements, 4, bytecode, (nuint)vs.Length, &l)); layout = l;
            }
            var dd = new D3D11_DEPTH_STENCIL_DESC { DepthEnable = true, DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL,
                DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS_EQUAL };
            ID3D11DepthStencilState* d = null; Check(device->CreateDepthStencilState(&dd, &d)); depthState = d;
            dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO;
            d = null; Check(device->CreateDepthStencilState(&dd, &d)); transparentDepthState = d;
        }
        catch { Dispose(); throw; }
    }

    private ID3D11SamplerState* Sampler(int wrapU, int wrapV)
    {
        static int Index(int value) => value switch { 33071 => 1, 33648 => 2, _ => 0 };
        static D3D11_TEXTURE_ADDRESS_MODE Address(int index) => index switch
        {
            1 => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            2 => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_MIRROR,
            _ => D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP,
        };
        var u = Index(wrapU); var v = Index(wrapV); var key = u * 3 + v;
        if (samplers[key] != 0) return (ID3D11SamplerState*)samplers[key];
        var description = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = Address(u), AddressV = Address(v), AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP,
            MaxAnisotropy = 1, MaxLOD = float.MaxValue, ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
        };
        ID3D11SamplerState* created = null;
        Check(device->CreateSamplerState(&description, &created));
        samplers[key] = (nint)created;
        return created;
    }
    private void EnsureDepth(uint w, uint h)
    {
        if (width == w && height == h && ownDepthView != null) return;
        if (ownDepthView != null) { ownDepthView->Release(); ownDepthView = null; }
        if (ownDepth != null) { ownDepth->Release(); ownDepth = null; }
        var desc = new D3D11_TEXTURE2D_DESC { Width = w, Height = h, MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL };
        ID3D11Texture2D* t = null; Check(device->CreateTexture2D(&desc, null, &t)); ownDepth = t;
        ID3D11DepthStencilView* view = null; Check(device->CreateDepthStencilView((ID3D11Resource*)t, null, &view)); ownDepthView = view;
        width = w; height = h;
    }
    private static byte[] Bytecode(string name)
    {
        using var stream = typeof(GuideGpu).Assembly.GetManifestResourceStream("XivWayfinder." + name + ".dxbc")!;
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public void Dispose()
    {
        if (ownDepthView != null) { ownDepthView->Release(); ownDepthView = null; }
        if (ownDepth != null) { ownDepth->Release(); ownDepth = null; } width = height = 0;
        if (depthState != null) { depthState->Release(); depthState = null; }
        if (transparentDepthState != null) { transparentDepthState->Release(); transparentDepthState = null; }
        for (var i = 0; i < samplers.Length; i++)
            if (samplers[i] != 0) { ((ID3D11SamplerState*)samplers[i])->Release(); samplers[i] = 0; }
        if (layout != null) { layout->Release(); layout = null; }
        if (constants != null) { constants->Release(); constants = null; }
        if (vertices != null) { vertices->Release(); vertices = null; }
        if (pixel != null) { pixel->Release(); pixel = null; }
        if (vertex != null) { vertex->Release(); vertex = null; }
        if (device != null) { device->Release(); device = null; }
    }
}
