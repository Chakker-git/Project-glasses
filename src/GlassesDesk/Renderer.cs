using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GlassesDesk;

/// <summary>
/// Direct3D 11 renderer for the glasses window: draws each captured monitor as a textured quad.
/// Uses a waitable flip-model swap chain with a maximum frame latency of 1 so head pose is sampled
/// as late as possible before each frame.
/// </summary>
internal sealed class Renderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer Constants : register(b0)
        {
            row_major float4x4 Mvp;
            float4 Tint;
        };
        Texture2D Screen : register(t0);
        SamplerState Linear : register(s0);

        struct VsOut { float4 Pos : SV_Position; float2 Uv : TEXCOORD0; };

        VsOut VS(uint id : SV_VertexID)
        {
            float2 c = float2(id & 1, id >> 1);          // (0,0) (1,0) (0,1) (1,1): triangle strip
            VsOut o;
            o.Pos = mul(float4(c.x * 2 - 1, 1 - c.y * 2, 0, 1), Mvp);
            o.Uv = c;
            return o;
        }

        float4 PS(VsOut i) : SV_Target
        {
            return float4(Screen.Sample(Linear, i.Uv).rgb, 1) * Tint;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 Mvp;
        public Vector4 Tint;
    }

    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain2 _swapChain;
    private readonly IntPtr _frameWaitable;
    private readonly ID3D11RenderTargetView _target;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _rasterizer;

    public ID3D11Device Device { get; }
    public int Width { get; }
    public int Height { get; }

    public Renderer(IntPtr hwnd, int width, int height)
    {
        Width = width;
        Height = height;

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device;
        _context = context;

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        using var swapChain1 = factory.CreateSwapChainForHwnd(Device, hwnd, new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        });
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        _swapChain = swapChain1.QueryInterface<IDXGISwapChain2>();
        _swapChain.MaximumFrameLatency = 1;
        _frameWaitable = _swapChain.FrameLatencyWaitableObject;

        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            _target = Device.CreateRenderTargetView(backBuffer);

        var vs = Compiler.Compile(ShaderSource, "VS", "panel.hlsl", "vs_5_0");
        var ps = Compiler.Compile(ShaderSource, "PS", "panel.hlsl", "ps_5_0");
        _vertexShader = Device.CreateVertexShader(vs.Span);
        _pixelShader = Device.CreatePixelShader(ps.Span);

        _constants = Device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
        _sampler = Device.CreateSamplerState(SamplerDescription.LinearClamp);
        _rasterizer = Device.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    /// <summary>Blocks until the swap chain can take another frame (keeps latency at one frame).</summary>
    public void WaitForFrame() => Native.WaitForSingleObjectEx(_frameWaitable, 100, true);

    public ID3D11DeviceContext Context => _context;

    public void Render(Matrix4x4 viewProjection, IEnumerable<(ID3D11ShaderResourceView View, Matrix4x4 World)> panels)
    {
        _context.OMSetRenderTargets(_target);
        _context.ClearRenderTargetView(_target, new Color4(0, 0, 0, 1));
        _context.RSSetViewport(new Viewport(Width, Height));
        _context.RSSetState(_rasterizer);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetSampler(0, _sampler);

        foreach (var (view, world) in panels)
        {
            var constants = new Constants { Mvp = world * viewProjection, Tint = Vector4.One };
            _context.UpdateSubresource(in constants, _constants);
            _context.PSSetShaderResource(0, view);
            _context.Draw(4, 0);
        }

        _swapChain.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        _rasterizer.Dispose();
        _sampler.Dispose();
        _constants.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _target.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        Device.Dispose();
    }
}
