using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Terminal.Connection.Native;

[SupportedOSPlatform("windows")]
internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafePseudoConsoleHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafePseudoConsoleHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        Kernel32.ClosePseudoConsole(handle);
        return true;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SafeKernelObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeKernelObjectHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeKernelObjectHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

[SupportedOSPlatform("windows")]
internal sealed class SafeProcThreadAttributeList : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeProcThreadAttributeList()
        : base(ownsHandle: true)
    {
    }

    private SafeProcThreadAttributeList(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    internal static SafeProcThreadAttributeList Create()
    {
        nint size = 0;
        _ = Kernel32.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size <= 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            if (!Kernel32.InitializeProcThreadAttributeList(pointer, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new SafeProcThreadAttributeList(pointer);
        }
        catch
        {
            Marshal.FreeHGlobal(pointer);
            throw;
        }
    }

    protected override bool ReleaseHandle()
    {
        Kernel32.DeleteProcThreadAttributeList(handle);
        Marshal.FreeHGlobal(handle);
        return true;
    }
}
