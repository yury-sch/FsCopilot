namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct Aircraft
{
    [SimVar("TITLE", "", 0)] 
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;

    [SimVar("CATEGORY", "", 1)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Category; // it should be String32, but it seems to work with 256.

    [SimVar("ATC MODEL", "", 2)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string AtcModel; // it should be String32, but it seems to work with 256.

    [SimVar("ATC TYPE", "", 2)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string AtcType; // it should be String32, but it seems to work with 256.

    [SimVar("ATC AIRLINE", "", 2)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string AtcAirline; // it should be String32, but it seems to work with 256.

    [SimVar("ATC ID", "", 2)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string AtcID; // it should be String32, but it seems to work with 256.
}