using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HSA.Native;

/// <summary>
/// WSDAPI (Windows Web Services on Devices API) P/Invoke wrapper. Used to discover WSD
/// devices on the network and to send WSD-Print SOAP requests to a device's XAddr.
///
/// The WSDAPI's standard UDP multicast discovery (WSDCreateDiscoveryProvider) works for
/// network WSD devices. For WSD-USB devices the discovery happens through the WSD Port
/// Monitor's internal transport and is not directly exposed by WSDAPI; for that case we
/// read the device's XAddr from the registry (DEVPKEY_Device_LocationInfo / the WSD Port
/// Monitor's port config) and send WSD-Print SOAP via standard HttpClient.
///
/// References:
///   WSDAPI: https://learn.microsoft.com/en-us/windows/win32/wsdapi/wsdapi-portal
///   WSD-Print: http://schemas.microsoft.com/windows/2011/08/printing/wsprint
///   PWG 5100.13: IPP attribute set for consumables
/// </summary>
public static class WsdApi
{
    // P/Invoke
    [DllImport("WSDApi.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WSDCreateDiscoveryProvider(
        IntPtr context,
        out IntPtr discoveryProvider);

    [DllImport("WSDApi.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WSDGenerateProbe(
        [MarshalAs(UnmanagedType.LPWStr)] string? types,
        [MarshalAs(UnmanagedType.LPStruct)] Guid? id,
        IntPtr? scopesList,
        uint port,
        out IntPtr headerAny);

    [DllImport("WSDApi.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WSDCreateDeviceProxy(
        [MarshalAs(UnmanagedType.LPWStr)] string? deviceId,
        [MarshalAs(UnmanagedType.LPWStr)] string? localId,
        IntPtr metadata,
        out IntPtr deviceProxy);

    [DllImport("WSDApi.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WSDCreateDeviceProxyAdvanced(
        [MarshalAs(UnmanagedType.LPWStr)] string? deviceId,
        [MarshalAs(UnmanagedType.LPWStr)] string? localId,
        IntPtr transportAddress,
        [MarshalAs(UnmanagedType.LPWStr)] string? remoteAddress,
        IntPtr metadata,
        uint flags,
        out IntPtr deviceProxy);

    [DllImport("WSDApi.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WSDCreateUdpAddress(
        [MarshalAs(UnmanagedType.LPWStr)] string? id,
        [MarshalAs(UnmanagedType.LPWStr)] string? prefix,
        ushort port,
        out IntPtr address);

    [DllImport("WSDApi.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WSDCreateHttpAddress(
        [MarshalAs(UnmanagedType.LPWStr)] string? id,
        [MarshalAs(UnmanagedType.LPWStr)] string? prefix,
        out IntPtr address);

    [DllImport("WSDApi.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WSDCreateUdpTransport(
        IntPtr address,
        out IntPtr transport);

    [DllImport("WSDApi.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WSDCreateHttpTransport(
        IntPtr address,
        uint protocolFlags,
        out IntPtr transport);

    // HRESULT helpers
    public const int S_OK = 0;

    public static bool Succeeded(int hr) => hr >= 0;
    public static string HResultString(int hr) => $"0x{hr:X8}";
}

/// <summary>
/// GUIDs for the WSDAPI COM interfaces we need.
/// </summary>
internal static class WsdIids
{
    // {8E9C8B25-77BB-446A-89C8-9B1A2F1A0975}
    public static readonly Guid IID_IWSDiscoveryProvider = new("8e9c8b25-77bb-446a-89c8-9b1a2f1a0975");
    // {8E9C8B25-77BB-446A-89C8-9B1A2F1A0976}
    public static readonly Guid IID_IWSDiscoveryProviderNotify = new("8e9c8b25-77bb-446a-89c8-9b1a2f1a0976");
    // {8E9C8B25-77BB-446A-89C8-9B1A2F1A0977}
    public static readonly Guid IID_IWSDiscoveryProbeMatch = new("8e9c8b25-77bb-446a-89c8-9b1a2f1a0977");

    // IWSDDeviceProxy
    // {095E8F1A-E3A1-4CD3-9D5F-67A22F0D2F4D}
    public static readonly Guid IID_IWSDDeviceProxy = new("095e8f1a-e3a1-4cd3-9d5f-67a22f0d2f4d");

    // IWSDServiceMessaging
    // {949C218D-E36F-4F36-967A-FE8E0AA32D89}
    public static readonly Guid IID_IWSDServiceMessaging = new("949c218d-e36f-4f36-967a-fe8e0aa32d89");

    // IWSDMetadata
    // {E90FE359-DCC4-4360-A86C-0F50D4D9027D}
    public static readonly Guid IID_IWSDMetadata = new("e90fe359-dcc4-4360-a86c-0f50d4d9027d");
}

/// <summary>
/// WSD_PROBE_MATCH structure as returned by IWSDiscoveryProbeMatch.GetProbeMatch.
/// Layout on x64 (verified):
///   offset 0:  Types (WSD_NAME_LIST*)        (8 bytes)
///   offset 8:  Scopes (WSD_URI_LIST*)        (8 bytes)
///   offset 16: XAddrs (WSD_URI_LIST*)        (8 bytes)
///   offset 24: MetadataVersion (ULONG)      (4 bytes)
///   offset 28: MessageId (ULONG)            (4 bytes)
///   offset 32: AnyHeaders (WSD_HEADER_LIST*)(8 bytes)
///   offset 40: ProbeResolveStatus (HRESULT)  (4 bytes)
///   offset 44: Padding (4 bytes for 8-byte align)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WSD_PROBE_MATCH
{
    public IntPtr Types;
    public IntPtr Scopes;
    public IntPtr XAddrs;
    public uint MetadataVersion;
    public uint MessageId;
    public IntPtr AnyHeaders;
    public int ProbeResolveStatus;
    public int _pad;
}

/// <summary>
/// WSD_URI_LIST (XAddrs list as returned in WSD_PROBE_MATCH).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WSD_URI_LIST
{
    public IntPtr Next;   // WSD_URI_LIST*
    public IntPtr Element; // LPWSTR
}

/// <summary>
/// COM interface IWSDiscoveryProbeMatch — vtable layout.
/// </summary>
[ComImport]
[Guid("8E9C8B25-77BB-446A-89C8-9B1A2F1A0977")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWSDiscoveryProbeMatch
{
    [PreserveSig] int GetProbeMatch(out IntPtr pProbeMatch);
    [PreserveSig] int GetProbeRequestToken([MarshalAs(UnmanagedType.LPWStr)] out string ppszRequestToken);
}

/// <summary>
/// COM interface IWSDiscoveryProviderNotify — receives discovery results.
/// </summary>
[ComImport]
[Guid("8E9C8B25-77BB-446A-89C8-9B1A2F1A0976")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWSDiscoveryProviderNotify
{
    [PreserveSig] int Add(IntPtr pProvider, IntPtr pMatch);
    [PreserveSig] int Remove(IntPtr pProvider, IntPtr pMatch);
    [PreserveSig] int SearchFailed(IntPtr pProvider, int hr);
    [PreserveSig] int SearchComplete(IntPtr pProvider);
}

/// <summary>
/// COM interface IWSDiscoveryProvider — sends Probes and manages results.
/// </summary>
[ComImport]
[Guid("8E9C8B25-77BB-446A-89C8-9B1A2F1A0975")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWSDiscoveryProvider
{
    [PreserveSig] int SearchByType(
        [MarshalAs(UnmanagedType.LPWStr)] string? pszTypes,
        IntPtr pTimeout);
    [PreserveSig] int SearchById(
        [MarshalAs(UnmanagedType.LPWStr)] string? pszId,
        IntPtr pTimeout);
    [PreserveSig] int GetResult();
    [PreserveSig] int Attach(IntPtr pSink);
    [PreserveSig] int Detach();
    [PreserveSig] int SetAddressFamily(uint dwAddressFamily);
}

/// <summary>
/// WSD_SEARCH_TIMEOUT (used by SearchByType). Two DWORDs: long/short timeouts in ms.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WSD_SEARCH_TIMEOUT
{
    public uint Long;     // default 3000ms
    public uint ShortTimeout;  // default 100ms
    // constructor for default values
    public WSD_SEARCH_TIMEOUT(uint longMs, uint shortMs) { Long = longMs; ShortTimeout = shortMs; }
}
