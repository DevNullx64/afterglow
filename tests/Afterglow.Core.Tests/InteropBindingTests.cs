using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;

namespace Afterglow.Core.Tests;

/// <summary>
/// Pins how the source-generated P/Invokes bind. [LibraryImport] either forwards
/// a signature as a plain [DllImport] on the method itself or wraps a local
/// "__PInvoke" function; either way the binding attributes must be the ones the
/// hand-written [DllImport]s had.
/// </summary>
public class InteropBindingTests
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void Nvapi_query_interface_loads_nvapi64_from_System32_only_with_cdecl()
    {
        var method = typeof(NvapiNative).GetMethod("QueryInterface", AnyStatic)!;
        var import = BindingOf(method);

        Assert.Equal("nvapi64.dll", import.Library);
        Assert.Equal("nvapi_QueryInterface", import.EntryPoint);
        Assert.Equal(DllImportSearchPath.System32, import.SearchPaths);
        Assert.True(import.Cdecl);
    }

    [Fact]
    public void Every_nvml_entry_point_binds_through_the_nvml_resolver_with_cdecl()
    {
        // The resolver installed by NvmlNative.EnsureResolverInstalled answers for
        // library name "nvml.dll" in this assembly; every generated P/Invoke must
        // carry exactly that name (and Cdecl, as nvml.h declares).
        var imports = PInvokesOf(typeof(NvmlNative));

        Assert.Equal(53, imports.Count);
        Assert.All(imports, import =>
        {
            Assert.Equal("nvml.dll", import.Library);
            Assert.True(import.Cdecl, $"{import.EntryPoint} is not cdecl");
            Assert.StartsWith("nvml", import.EntryPoint, StringComparison.Ordinal);
        });
    }

    private sealed record Binding(string Library, string EntryPoint, DllImportSearchPath? SearchPaths, bool Cdecl);

    /// <summary>Every P/Invoke declared by <paramref name="type"/>, including generated local __PInvoke stubs.</summary>
    private static List<Binding> PInvokesOf(Type type) =>
        type.GetMethods(AnyStatic)
            .Concat(type.GetNestedTypes(BindingFlags.NonPublic).SelectMany(n => n.GetMethods(AnyStatic)))
            .Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(BindingOf)
            .ToList();

    private static Binding BindingOf(MethodInfo method)
    {
        Assert.True(method.Attributes.HasFlag(MethodAttributes.PinvokeImpl), $"{method.Name} is not a P/Invoke");
        var import = method.GetCustomAttribute<DllImportAttribute>()!;
        var callConv = method.GetCustomAttribute<UnmanagedCallConvAttribute>();
        bool cdecl = import.CallingConvention == CallingConvention.Cdecl ||
                     (callConv?.CallConvs?.Contains(typeof(CallConvCdecl)) ?? false);
        var searchPaths = method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths;
        return new Binding(import.Value, import.EntryPoint ?? method.Name, searchPaths, cdecl);
    }
}
