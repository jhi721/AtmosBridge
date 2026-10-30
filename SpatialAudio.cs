using System.Runtime.InteropServices;

namespace AtmosBridge;

// Minimal Core Audio / Spatial Audio interop (mmdeviceapi.h, spatialaudioclient.h).

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection {
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore {
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

struct PropertyKey {
    public Guid FormatId;
    public int PropertyId;
}

[StructLayout(LayoutKind.Sequential)]
struct PropVariant {
    public ushort Type;
    ushort reserved1, reserved2, reserved3;
    public IntPtr Pointer;
    IntPtr padding;
}

[ComImport, Guid("BBF8E066-AAAA-49BE-9A4D-FD2A858EA27F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpatialAudioClient {
    [PreserveSig] int GetStaticObjectPosition(uint type, out float x, out float y, out float z);
    [PreserveSig] int GetNativeStaticObjectTypeMask(out uint mask);
    [PreserveSig] int GetMaxDynamicObjectCount(out uint count);
    [PreserveSig] int GetSupportedAudioObjectFormatEnumerator(out IAudioFormatEnumerator formats);
    [PreserveSig] int GetMaxFrameCount(IntPtr format, out uint frames);
    [PreserveSig] int IsAudioObjectFormatSupported(IntPtr format);
    [PreserveSig] int IsSpatialAudioStreamAvailable(ref Guid streamUuid, IntPtr auxInfo);
    [PreserveSig] int ActivateSpatialAudioStream(IntPtr activationParams, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object stream);
}

[ComImport, Guid("DCDAA858-895A-4A22-A5EB-67BDA506096D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioFormatEnumerator {
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetFormat(uint index, out IntPtr format);
}

[ComImport, Guid("BAB5F473-B423-477B-85F5-B5A332A04153"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpatialAudioObjectRenderStream {
    [PreserveSig] int GetAvailableDynamicObjectCount(out uint count);
    [PreserveSig] int GetService(ref Guid riid, out IntPtr service);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int BeginUpdatingAudioObjects(out uint availableDynamicObjectCount, out uint frameCountPerBuffer);
    [PreserveSig] int EndUpdatingAudioObjects();
    [PreserveSig] int ActivateSpatialAudioObject(uint type, out ISpatialAudioObject obj);
}

[ComImport, Guid("DDE28967-521B-46E5-8F00-BD6F2BC8AB1D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpatialAudioObject {
    [PreserveSig] int GetBuffer(out IntPtr buffer, out uint bufferLength);
    [PreserveSig] int SetEndOfStream(uint frameCount);
    [PreserveSig] int IsActive(out int active);
    [PreserveSig] int GetAudioObjectType(out uint type);
    [PreserveSig] int SetPosition(float x, float y, float z);
    [PreserveSig] int SetVolume(float volume);
}

static class AudioObjectType {
    public const uint Dynamic = 1;
    public const uint LowFrequency = 0x10;
}

/// <summary>
/// Owns a Windows spatial audio object stream on one render endpoint.
/// </summary>
sealed class SpatialOutput : IDisposable {
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    public ISpatialAudioObjectRenderStream Stream { get; }
    public uint MaxDynamicObjects { get; }
    public int SampleRate { get; }
    public string DeviceId { get; }
    public AutoResetEvent BufferEvent { get; } = new(false);

    readonly IntPtr activationParams, propVariant;

    public SpatialOutput(string deviceId) {
        const string spatialOff = "Spatial sound is off (0 dynamic objects). Enable Dolby Atmos for Headphones on the output device.";
        if (deviceId == null) {
            deviceId = AutoDevice() ?? throw new InvalidOperationException(spatialOff);
        } else if (!deviceId.StartsWith("{0.0.0.")) {
            deviceId = "{0.0.0.00000000}." + deviceId;
        }
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Check(enumerator.GetDevice(deviceId, out IMMDevice device), "GetDevice " + deviceId);
        (ISpatialAudioClient client, uint maxDynamic) = Spatial(device);
        if (maxDynamic == 0) {
            throw new InvalidOperationException(spatialOff);
        }
        DeviceId = deviceId;
        MaxDynamicObjects = maxDynamic;

        Check(client.GetSupportedAudioObjectFormatEnumerator(out IAudioFormatEnumerator formats), "format enumerator");
        Check(formats.GetFormat(0, out IntPtr format), "GetFormat"); // first = preferred (float32 mono)
        SampleRate = Marshal.ReadInt32(format, 4);

        // SpatialAudioObjectRenderStreamActivationParams (x64 layout)
        activationParams = Marshal.AllocHGlobal(40);
        Marshal.WriteIntPtr(activationParams, 0, format);
        Marshal.WriteInt32(activationParams, 8, (int)AudioObjectType.LowFrequency);
        Marshal.WriteInt32(activationParams, 12, 0);
        Marshal.WriteInt32(activationParams, 16, (int)maxDynamic);
        Marshal.WriteInt32(activationParams, 20, 6); // AudioCategory_Media
        Marshal.WriteIntPtr(activationParams, 24, BufferEvent.SafeWaitHandle.DangerousGetHandle());
        Marshal.WriteIntPtr(activationParams, 32, IntPtr.Zero);

        // PROPVARIANT { VT_BLOB, cbSize, pBlobData }
        propVariant = Marshal.AllocHGlobal(24);
        Marshal.WriteInt64(propVariant, 0, 65); // vt + reserved words
        Marshal.WriteInt64(propVariant, 8, 40); // cbSize + padding
        Marshal.WriteIntPtr(propVariant, 16, activationParams);

        Guid iidStream = typeof(ISpatialAudioObjectRenderStream).GUID;
        Check(client.ActivateSpatialAudioStream(propVariant, ref iidStream, out object s), "ActivateSpatialAudioStream");
        Stream = (ISpatialAudioObjectRenderStream)s;
    }

    /// <summary>
    /// The endpoint auto mode plays to: the default one if spatial sound is on there, else the first active endpoint that
    /// has it (null: none).
    /// </summary>
    public static string AutoDevice() {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out IMMDevice device) >= 0 && Spatial(device).Item2 > 0) {
            device.GetId(out string id);
            return id;
        }
        if (enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection all) >= 0) {
            all.GetCount(out uint count);
            for (uint i = 0; i < count; i++) {
                if (all.Item(i, out IMMDevice candidate) >= 0 && Spatial(candidate).Item2 > 0) {
                    candidate.GetId(out string id);
                    return id;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Active render endpoints for the tray menu, with whether spatial sound is on there.
    /// </summary>
    public static List<(string Id, string Name, bool Spatial)> Devices() {
        var devices = new List<(string, string, bool)>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection all) < 0) {
            return devices;
        }
        all.GetCount(out uint count);
        for (uint i = 0; i < count; i++) {
            if (all.Item(i, out IMMDevice device) >= 0) {
                device.GetId(out string id);
                devices.Add((id, FriendlyName(device) ?? id, Spatial(device).Item2 > 0));
            }
        }
        return devices;
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);

    static string FriendlyName(IMMDevice device) {
        var key = new PropertyKey { FormatId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 }; // PKEY_Device_FriendlyName
        if (device.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore store) < 0 || store.GetValue(ref key, out PropVariant value) < 0) {
            return null;
        }
        string name = value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null;
        PropVariantClear(ref value);
        return name;
    }

    /// <summary>
    /// Spatial client of an endpoint and its dynamic object count (0: spatial sound off or unsupported).
    /// </summary>
    static (ISpatialAudioClient, uint) Spatial(IMMDevice device) {
        Guid iid = typeof(ISpatialAudioClient).GUID;
        if (device.Activate(ref iid, 1, IntPtr.Zero, out object c) < 0) {
            return (null, 0);
        }
        var client = (ISpatialAudioClient)c;
        return client.GetMaxDynamicObjectCount(out uint count) < 0 ? (client, 0) : (client, count);
    }

    public static void Check(int hr, string what) {
        if (hr < 0) {
            throw new COMException($"{what} failed: 0x{hr:X8}", hr);
        }
    }

    public void Dispose() {
        Stream.Stop();
        Marshal.FreeHGlobal(propVariant);
        Marshal.FreeHGlobal(activationParams);
    }
}
