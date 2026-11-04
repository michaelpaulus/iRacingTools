using System.Runtime.InteropServices;

/// <summary>
/// The DXGI output numbers ffmpeg's ddagrab uses (output_idx). They aren't the order of <c>Screen.AllScreens</c>:
/// DXGI lists the primary monitor first, so with several monitors the two orders differ.
/// </summary>
static class DxgiOutputs
{
    /// <summary>The output index of the monitor with this device name (e.g. \\.\DISPLAY2) on the first GPU, or -1.</summary>
    public static int IndexOf(string deviceName)
    {
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) < 0) return -1;
        try
        {
            // ddagrab captures through the default (first) adapter.
            if (factory.EnumAdapters(0, out var adapter) < 0) return -1;
            try
            {
                for (uint i = 0; adapter.EnumOutputs(i, out var output) >= 0; i++)
                {
                    try
                    {
                        if (output.GetDesc(out var desc) >= 0 && string.Equals(desc.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                            return (int)i;
                    }
                    finally { Marshal.ReleaseComObject(output); }
                }
                return -1;
            }
            finally { Marshal.ReleaseComObject(adapter); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    // Only the methods used are declared properly; the others hold their vtable slots.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter adapter);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIOutput
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
    }
}
