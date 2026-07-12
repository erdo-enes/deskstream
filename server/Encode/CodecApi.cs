using System.Runtime.InteropServices;

namespace DeskStreamer.Server.Encode;

/// <summary>
/// Minimal ICodecAPI interop. Vortice.MediaFoundation does not expose ICodecAPI, so we
/// QI it off the encoder MFT via classic COM interop. Method order below must match the
/// ICodecAPI vtable (strmif.h); trailing methods we never call are omitted, which is safe
/// because vtable slots are assigned in declaration order.
/// </summary>
[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig]
    int GetParameterRange(ref Guid api,
        [MarshalAs(UnmanagedType.Struct)] out object valueMin,
        [MarshalAs(UnmanagedType.Struct)] out object valueMax,
        [MarshalAs(UnmanagedType.Struct)] out object steppingDelta);
    [PreserveSig]
    int GetParameterValues(ref Guid api, out IntPtr values, out uint valuesCount);
    [PreserveSig]
    int GetDefaultValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig]
    int GetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig]
    int SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

/// <summary>RAII wrapper: QIs ICodecAPI from an MFT's IUnknown and releases the RCW.</summary>
internal sealed class CodecApi : IDisposable
{
    private ICodecAPI? _api;

    public static CodecApi? TryCreate(IntPtr transformUnknown)
    {
        try
        {
            var obj = Marshal.GetObjectForIUnknown(transformUnknown);
            if (obj is ICodecAPI api)
                return new CodecApi { _api = api };
            Marshal.ReleaseComObject(obj);
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when the encoder reports the given CODECAPI parameter is supported (IsSupported == S_OK).
    /// Used to avoid noisy SetValue failures on parameters the MFT does not expose.
    /// </summary>
    public bool IsSupported(Guid key)
    {
        if (_api == null)
            return false;
        try { return _api.IsSupported(ref key) == 0; }
        catch { return false; }
    }

    public bool IsModifiable(Guid key)
    {
        if (_api == null)
            return false;
        try { return _api.IsModifiable(ref key) == 0; }
        catch { return false; }
    }

    /// <summary>Sets a VT_UI4 property. Returns the raw HRESULT (0 on success).</summary>
    public int SetUInt32(Guid key, uint value)
    {
        object boxed = value; // boxed uint marshals as VT_UI4
        return _api!.SetValue(ref key, ref boxed);
    }

    /// <summary>Sets a VT_BOOL property. Returns the raw HRESULT (0 on success).</summary>
    public int SetBool(Guid key, bool value)
    {
        object boxed = value; // boxed bool marshals as VT_BOOL
        return _api!.SetValue(ref key, ref boxed);
    }

    public void Dispose()
    {
        if (_api != null)
        {
            try { Marshal.ReleaseComObject(_api); } catch { }
            _api = null;
        }
    }
}
