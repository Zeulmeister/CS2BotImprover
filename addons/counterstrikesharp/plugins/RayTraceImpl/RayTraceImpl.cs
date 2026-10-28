using System;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace RayTraceImpl;

public class RayTraceImpl : BasePlugin
{
    public override string ModuleName => "RayTraceImpl";
    public override string ModuleVersion => "v1.0.1";
    public override string ModuleAuthor => "Slynx";

    internal static PluginCapability<CRayTraceInterface> RayTraceInterface { get; } = new PluginCapability<CRayTraceInterface>("raytrace:craytraceinterface");

    public override void Load(bool hotReload)
    {
        Capabilities.RegisterPluginCapability(RayTraceInterface, () =>
        {
            if (!NativeBridge.Loaded)
            {
                NativeBridge.Initialize();
            }
            return new CRayTrace();
        });
        RegisterListener<Listeners.OnMetamodAllPluginsLoaded>(OnMetamodAllPluginsLoaded);
        if (hotReload)
        {
            NativeBridge.Initialize();
        }
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.OnMetamodAllPluginsLoaded>(OnMetamodAllPluginsLoaded);
    }

    private void OnMetamodAllPluginsLoaded()
    {
        if (!NativeBridge.Initialize())
        {
            Prints.ServerLog("[RayTraceImpl] Native bridge initialization deferred or unavailable (using safe fallback).", ConsoleColor.Yellow);
        }
        else
        {
            Prints.ServerLog("[RayTraceImpl] Managed side initialized.", ConsoleColor.Green);
        }
    }
}

public class CRayTrace : CRayTraceInterface
{
    public unsafe bool TraceShape(Vector start, QAngle angles, CEntityInstance? ignore, RayTraceAPI.TraceOptions options, out RayTraceAPI.TraceResult result)
    {
        result = default;
        result.Fraction = 1.0f;
        if (!NativeBridge.Loaded && !NativeBridge.Initialize())
        {
            return false;
        }
        if (start == null || angles == null || start.Handle == IntPtr.Zero || angles.Handle == IntPtr.Zero || NativeBridge.TraceShape == null)
        {
            return false;
        }
        try
        {
            RayTraceAPI.TraceResult traceResult = default;
            RayTraceAPI.TraceOptions traceOptions = options;
            bool ok = NativeBridge.TraceShape(NativeBridge.Handle, start.Handle, angles.Handle, ignore?.Handle ?? IntPtr.Zero, (nint)(&traceOptions), (nint)(&traceResult));
            result = traceResult;
            return ok;
        }
        catch
        {
            result.Fraction = 1.0f;
            return false;
        }
    }

    public unsafe bool TraceEndShape(Vector start, Vector end, CEntityInstance? ignore, RayTraceAPI.TraceOptions options, out RayTraceAPI.TraceResult result)
    {
        result = default;
        result.Fraction = 1.0f;
        if (!NativeBridge.Loaded && !NativeBridge.Initialize())
        {
            return false;
        }
        if (start == null || end == null || start.Handle == IntPtr.Zero || end.Handle == IntPtr.Zero || NativeBridge.TraceEndShape == null)
        {
            return false;
        }
        try
        {
            RayTraceAPI.TraceResult traceResult = default;
            RayTraceAPI.TraceOptions traceOptions = options;
            bool ok = NativeBridge.TraceEndShape(NativeBridge.Handle, start.Handle, end.Handle, ignore?.Handle ?? IntPtr.Zero, (nint)(&traceOptions), (nint)(&traceResult));
            result = traceResult;
            return ok;
        }
        catch
        {
            result.Fraction = 1.0f;
            return false;
        }
    }

    public unsafe bool TraceHullShape(Vector start, Vector end, Vector mins, Vector maxs, CEntityInstance? ignore, RayTraceAPI.TraceOptions options, out RayTraceAPI.TraceResult result)
    {
        result = default;
        result.Fraction = 1.0f;
        if (!NativeBridge.Loaded && !NativeBridge.Initialize())
        {
            return false;
        }
        if (start == null || end == null || mins == null || maxs == null || start.Handle == IntPtr.Zero || end.Handle == IntPtr.Zero || NativeBridge.TraceHullShape == null)
        {
            return false;
        }
        try
        {
            RayTraceAPI.TraceResult traceResult = default;
            RayTraceAPI.TraceOptions traceOptions = options;
            bool ok = NativeBridge.TraceHullShape(NativeBridge.Handle, start.Handle, end.Handle, mins.Handle, maxs.Handle, ignore?.Handle ?? IntPtr.Zero, (nint)(&traceOptions), (nint)(&traceResult));
            result = traceResult;
            return ok;
        }
        catch
        {
            result.Fraction = 1.0f;
            return false;
        }
    }

    public unsafe bool TraceShapeEx(Vector start, Vector end, nint filter, nint ray, out RayTraceAPI.TraceResult result)
    {
        result = default;
        result.Fraction = 1.0f;
        if (!NativeBridge.Loaded && !NativeBridge.Initialize())
        {
            return false;
        }
        if (start == null || end == null || start.Handle == IntPtr.Zero || end.Handle == IntPtr.Zero || NativeBridge.TraceShapeEx == null)
        {
            return false;
        }
        try
        {
            RayTraceAPI.TraceResult traceResult = default;
            bool ok = NativeBridge.TraceShapeEx(NativeBridge.Handle, start.Handle, end.Handle, filter, ray, (nint)(&traceResult));
            result = traceResult;
            return ok;
        }
        catch
        {
            result.Fraction = 1.0f;
            return false;
        }
    }
}

public static class NativeBridge
{
    public static nint Handle;
    public static bool Loaded;

    public static Func<nint, nint, nint, nint, nint, nint, bool>? TraceShape;
    public static Func<nint, nint, nint, nint, nint, nint, bool>? TraceEndShape;
    public static Func<nint, nint, nint, nint, nint, nint, nint, nint, bool>? TraceHullShape;
    public static Func<nint, nint, nint, nint, nint, nint, bool>? TraceShapeEx;

    public static bool Initialize()
    {
        if (Loaded && Handle != IntPtr.Zero)
        {
            return true;
        }
        try
        {
            nint? factoryPtr = Utilities.MetaFactory("CRayTraceInterface002");
            if (!factoryPtr.HasValue || factoryPtr.Value == IntPtr.Zero)
            {
                return false;
            }
            Handle = factoryPtr.Value;
            Bind();
            Loaded = true;
            return true;
        }
        catch
        {
            Loaded = false;
            return false;
        }
    }

    private static void Bind()
    {
        int offset;
        int offset2;
        int offset3;
        int offset4;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            offset = 1;
            offset2 = 2;
            offset3 = 3;
            offset4 = 4;
        }
        else
        {
            offset = 2;
            offset2 = 3;
            offset3 = 4;
            offset4 = 5;
        }
        TraceShape = VirtualFunction.Create<nint, nint, nint, nint, nint, nint, bool>(Handle, offset);
        TraceEndShape = VirtualFunction.Create<nint, nint, nint, nint, nint, nint, bool>(Handle, offset2);
        TraceHullShape = VirtualFunction.Create<nint, nint, nint, nint, nint, nint, nint, nint, bool>(Handle, offset3);
        TraceShapeEx = VirtualFunction.Create<nint, nint, nint, nint, nint, nint, bool>(Handle, offset4);
    }
}

public static class Prints
{
    public static void ServerLog(string msg, ConsoleColor color = ConsoleColor.White)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(msg);
        Console.ResetColor();
    }
}

