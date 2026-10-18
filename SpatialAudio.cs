using System.Runtime.InteropServices;

namespace AtmosBridge;

// Minimal Core Audio / Spatial Audio interop (mmdeviceapi.h, spatialaudioclient.h).

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
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
    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    public ISpatialAudioObjectRenderStream Stream { get; }
    public uint MaxDynamicObjects { get; }
    public int SampleRate { get; }
    public AutoResetEvent BufferEvent { get; } = new(false);

    readonly IntPtr activationParams, propVariant;

    public SpatialOutput(string deviceId) {
        Guid clsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E"), iid = typeof(IMMDeviceEnumerator).GUID;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out object e), "MMDeviceEnumerator");
        var enumerator = (IMMDeviceEnumerator)e;
        IMMDevice device;
        if (deviceId == null) {
            Check(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device), "GetDefaultAudioEndpoint");
        } else {
            if (!deviceId.StartsWith("{0.0.0.")) {
                deviceId = "{0.0.0.00000000}." + deviceId;
            }
            Check(enumerator.GetDevice(deviceId, out device), "GetDevice " + deviceId);
        }

        Guid iidClient = typeof(ISpatialAudioClient).GUID;
        Check(device.Activate(ref iidClient, 1, IntPtr.Zero, out object c), "Activate ISpatialAudioClient");
        var client = (ISpatialAudioClient)c;
        Check(client.GetMaxDynamicObjectCount(out uint maxDynamic), "GetMaxDynamicObjectCount");
        if (maxDynamic == 0) {
            throw new InvalidOperationException("Spatial sound is off on this endpoint (0 dynamic objects). Enable Dolby Atmos for Headphones.");
        }
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
        for (int i = 0; i < 24; i++) {
            Marshal.WriteByte(propVariant, i, 0);
        }
        Marshal.WriteInt16(propVariant, 0, 65);
        Marshal.WriteInt32(propVariant, 8, 40);
        Marshal.WriteIntPtr(propVariant, 16, activationParams);

        Guid iidStream = typeof(ISpatialAudioObjectRenderStream).GUID;
        Check(client.ActivateSpatialAudioStream(propVariant, ref iidStream, out object s), "ActivateSpatialAudioStream");
        Stream = (ISpatialAudioObjectRenderStream)s;
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
