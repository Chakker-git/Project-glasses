using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GlassesDesk;

/// <summary>
/// Captures one monitor with Windows.Graphics.Capture into a texture owned by our D3D device.
/// Poll <see cref="Update"/> from the render thread; the texture keeps the last frame when the
/// screen hasn't changed.
/// </summary>
internal sealed unsafe class MonitorCapture : IDisposable
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _size;
    private ID3D11Texture2D? _texture;

    public Display Display { get; }
    public long FramesReceived { get; private set; }
    public string Status => View == null
        ? $"{Display.FriendlyName}: {FramesReceived} frames, no image yet"
        : $"{Display.FriendlyName}: {FramesReceived} frames, {_size.Width}x{_size.Height}";
    public ID3D11ShaderResourceView? View { get; private set; }

    public MonitorCapture(ID3D11Device device, IDirect3DDevice winrtDevice, Display display)
    {
        _device = device;
        _winrtDevice = winrtDevice;
        Display = display;
        _item = CreateItemForMonitor(display.Handle);
        _size = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;
        try { _session.IsBorderRequired = false; } catch { /* older Windows: yellow border stays */ }
        _session.StartCapture();
    }

    /// <summary>Copies the newest frame, if any, into our texture. Render thread only.</summary>
    public void Update(ID3D11DeviceContext context)
    {
        using var frame = _pool.TryGetNextFrame();
        if (frame == null) return;
        FramesReceived++;

        if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
        {
            // Monitor resolution changed: resize the pool and pick up the next frame.
            _size = frame.ContentSize;
            _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            View?.Dispose();
            _texture?.Dispose();
            View = null;
            _texture = null;
            return;
        }

        using var source = GetTexture(frame.Surface);
        if (_texture == null)
        {
            var desc = source.Description;
            _texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });
            View = _device.CreateShaderResourceView(_texture);
        }
        context.CopyResource(_texture, source);
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Check(WindowsCreateString(className, className.Length, out var hstring));
        try
        {
            var iid = IID_IGraphicsCaptureItemInterop;
            Check(RoGetActivationFactory(hstring, &iid, out var interop));
            try
            {
                // IGraphicsCaptureItemInterop: [3] CreateForWindow, [4] CreateForMonitor
                var vtbl = *(IntPtr**)interop;
                var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[4];
                var itemIid = IID_IGraphicsCaptureItem;
                IntPtr item;
                Check(createForMonitor(interop, monitor, &itemIid, &item));
                try { return GraphicsCaptureItem.FromAbi(item); }
                finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(interop); }
        }
        finally { WindowsDeleteString(hstring); }
    }

    private static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessIid = IID_IDirect3DDxgiInterfaceAccess;
            Check(Marshal.QueryInterface(unknown, in accessIid, out var access));
            try
            {
                // IDirect3DDxgiInterfaceAccess: [3] GetInterface(REFIID, void**)
                var vtbl = *(IntPtr**)access;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                var texIid = IID_ID3D11Texture2D;
                IntPtr texture;
                Check(getInterface(access, &texIid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(unknown); }
    }

    /// <summary>
    /// Call once on the render thread before creating captures: joins the multithreaded WinRT
    /// apartment and asks to drop the yellow capture border (Windows 11; ignored elsewhere).
    /// </summary>
    public static void InitializeThread()
    {
        RoInitialize(1); // RO_INIT_MULTITHREADED; S_FALSE / RPC_E_CHANGED_MODE are fine
        try { GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Wait(2000); }
        catch { /* not available on this Windows build */ }
    }

    /// <summary>Wraps our D3D11 device as the WinRT IDirect3DDevice that capture APIs want.</summary>
    public static IDirect3DDevice CreateWinRTDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
        View?.Dispose();
        _texture?.Dispose();
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int initType);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, out IntPtr factory);
}
