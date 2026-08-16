using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// The COM plumbing between WPF and Windows.Graphics.Capture: the two things the projection cannot
/// give on its own — a capture item for an HWND, and a Direct3D device to receive frames on — plus
/// the readback that turns a captured surface into the <see cref="System.Drawing.Bitmap"/> the rest
/// of this application already speaks.
/// </summary>
/// <remarks>
/// Hand-written rather than pulled in with a D3D wrapper library. What is needed here is narrow —
/// create a device, hold it, hand it to the frame pool, never draw anything — and the surface a
/// wrapper would add is much wider than that, on a project whose whole dependency list is five
/// packages. The one thing worth borrowing from D3D, a GPU-side crop, is deliberately not done yet:
/// correctness of the capture semantics comes first and readback is measured afterwards.
///
/// Everything here is 1903 or older. The types are attributed accordingly so the callers are forced
/// to say out loud which Windows they need, because the application itself still runs on 1809.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
internal static class WgcInterop
{
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private const string GraphicsCaptureItemClassId = "Windows.Graphics.Capture.GraphicsCaptureItem";

    /// <summary>
    /// Whether this system can capture at all. Asked before anything else is built, because every
    /// call below is newer than the Windows this application supports.
    /// </summary>
    public static bool IsCaptureSupported()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return false;

        try
        {
            // Not the same question as the build number: capture is also refused on systems where
            // the graphics stack cannot serve it, and this is the only way to hear that.
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception)
        {
            // A missing or broken projection reads as unsupported rather than as a crash on startup.
            return false;
        }
    }

    /// <summary>
    /// A capture item for one top-level window, or null when the window cannot be captured — it has
    /// closed, it is a window this process may not read, or the system refused.
    /// </summary>
    public static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        var interopFactory = GetCaptureItemInterop();
        var iid = IidGraphicsCaptureItem;
        var abi = IntPtr.Zero;
        try
        {
            interopFactory.CreateForWindow(hwnd, ref iid, out abi);
            return abi == IntPtr.Zero ? null : FromAbi(abi);
        }
        finally
        {
            if (abi != IntPtr.Zero) Marshal.Release(abi);
        }
    }

    /// <summary>A capture item for one monitor. Same rules as <see cref="CreateItemForWindow"/>.</summary>
    public static GraphicsCaptureItem? CreateItemForMonitor(IntPtr hmonitor)
    {
        var interopFactory = GetCaptureItemInterop();
        var iid = IidGraphicsCaptureItem;
        var abi = IntPtr.Zero;
        try
        {
            interopFactory.CreateForMonitor(hmonitor, ref iid, out abi);
            return abi == IntPtr.Zero ? null : FromAbi(abi);
        }
        finally
        {
            if (abi != IntPtr.Zero) Marshal.Release(abi);
        }
    }

    /// <summary>
    /// A Direct3D device for the frame pool to hand frames back on, and the raw D3D11 device behind
    /// it, which the caller must release when it is done.
    /// </summary>
    /// <remarks>
    /// BGRA support is required, not optional: the frame pool is created for
    /// <c>B8G8R8A8UIntNormalized</c>, which is also the one format that maps onto a 32bpp GDI bitmap
    /// without a channel shuffle. The device is created without a debug layer and without a swap
    /// chain — nothing here presents anything.
    /// </remarks>
    public static IDirect3DDevice CreateDirect3DDevice(out IntPtr rawDevice)
    {
        const int DriverTypeHardware = 1;
        const uint FlagBgraSupport = 0x20;
        const uint SdkVersion = 7;

        var hr = D3D11CreateDevice(
            IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, FlagBgraSupport,
            IntPtr.Zero, 0, SdkVersion, out var device, out _, out var context);

        // A machine with no usable hardware device is rare but real — a remote session, a stripped
        // VM. WARP is slower and entirely good enough to read a window at four frames a second.
        if (hr < 0)
        {
            const int DriverTypeWarp = 5;
            hr = D3D11CreateDevice(
                IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, FlagBgraSupport,
                IntPtr.Zero, 0, SdkVersion, out device, out _, out context);
        }

        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        // The immediate context is never used here — frames arrive already copied into the pool's
        // textures — so it is released straight away rather than carried around.
        if (context != IntPtr.Zero) Marshal.Release(context);

        var dxgi = IntPtr.Zero;
        var abi = IntPtr.Zero;
        try
        {
            var dxgiIid = IidDxgiDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, ref dxgiIid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out abi));

            rawDevice = device;
            device = IntPtr.Zero;
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            if (abi != IntPtr.Zero) Marshal.Release(abi);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
    }

    /// <summary>
    /// Reads a captured surface back into a bitmap on the CPU, which is what recognition needs.
    /// </summary>
    /// <remarks>
    /// This is the expensive half of the whole path — a full GPU-to-CPU copy of the captured window
    /// — which is exactly why the backend does it at poll rate and not at frame rate, and why one
    /// readback serves every region rather than one per region.
    ///
    /// <c>CreateCopyFromSurfaceAsync</c> rather than a staging texture and a map: it is the only
    /// step of the D3D dance the projection will do on our behalf, and doing it by hand would mean
    /// defining ID3D11Device, ID3D11Texture2D and ID3D11DeviceContext to save one copy that has not
    /// yet been shown to cost anything. If measurement says otherwise, this is the one function that
    /// changes.
    /// </remarks>
    /// <param name="width">
    /// How much of the surface is actually the window. A capture texture is allocated to the size
    /// the pool was created for and is routinely larger than the content in it — the extra columns
    /// and rows are whatever was last in that memory — so the content size travels with every frame
    /// and is what gets copied out.
    /// </param>
    public static unsafe System.Drawing.Bitmap ReadBack(IDirect3DSurface surface, int width, int height)
    {
        using var software = SoftwareBitmap.CreateCopyFromSurfaceAsync(
            surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();

        var bitmap = new System.Drawing.Bitmap(
            Math.Clamp(width, 1, software.PixelWidth),
            Math.Clamp(height, 1, software.PixelHeight),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            using var buffer = software.LockBuffer(BitmapBufferAccessMode.Read);
            using var reference = buffer.CreateReference();

            // Not a plain cast. A projected WinRT object is not a classic COM callable wrapper, so
            // casting it to a [ComImport] interface throws — the pixels have to be reached by going
            // out to the ABI pointer and querying for the byte-access interface there.
            var abi = WinRT.MarshalInspectable<Windows.Foundation.IMemoryBufferReference>.FromManaged(reference);
            IMemoryBufferByteAccess access;
            try
            {
                access = (IMemoryBufferByteAccess)Marshal.GetObjectForIUnknown(abi);
            }
            finally
            {
                Marshal.Release(abi);
            }

            // Valid only while the reference above is open, which is the whole of this scope.
            access.GetBuffer(out var source, out _);

            var plane = buffer.GetPlaneDescription(0);
            var locked = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                // Row by row: the two strides agree in practice and must not be assumed to, and a
                // row is a memcpy either way.
                var rowBytes = bitmap.Width * 4;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    Buffer.MemoryCopy(
                        source + plane.StartIndex + (plane.Stride * y),
                        (byte*)locked.Scan0 + (locked.Stride * y),
                        rowBytes,
                        rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(locked);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static GraphicsCaptureItem FromAbi(IntPtr abi) =>
        WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);

    private static IGraphicsCaptureItemInterop GetCaptureItemInterop()
    {
        // RoGetActivationFactory by hand, because .NET dropped the built-in WinRT marshalling that
        // used to make this one line and the projection does not surface the interop factory.
        var classId = IntPtr.Zero;
        var factory = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(
                GraphicsCaptureItemClassId, GraphicsCaptureItemClassId.Length, out classId));

            var iid = IidGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref iid, out factory));

            return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (classId != IntPtr.Zero) WindowsDeleteString(classId);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        void CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe void GetBuffer(out byte* buffer, out uint capacity);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
