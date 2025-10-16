namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct Aircraft
{
    [SimVar("TITLE", "", 0)] 
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;

    [SimVar("ATC MODEL", "", 1)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string AtcModel; // it should be String32, but it seems to work with 256.

    [SimVar("CATEGORY", "", 2)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Category; // it should be String32, but it seems to work with 256.
}