using System;
using System.IO;
using System.Reflection;

/// <summary>
/// The type the .NET host actually looks for.
///
/// A startup hook MUST be a type named exactly "StartupHook" in the GLOBAL
/// namespace with a public static parameterless Initialize(). The host resolves it
/// by that literal name (see System.StartupHookProvider.CallStartupHook), so this
/// type cannot live in a namespace and cannot be renamed.
///
/// ORDER MATTERS HERE. CoreCLR's hook runs in the Default AssemblyLoadContext,
/// which does not probe this directory, so every sibling dependency has to be
/// resolved manually - and the resolver must be in place BEFORE any type that
/// needs those dependencies is JIT-compiled. If Initialize() called
/// PclHarmony.Ce.Host.Start() directly, compiling Initialize would already need
/// PclHarmony.Contract (Host's fields mention IDeclarativePlugin) and the launcher
/// would die with:
///     FileNotFoundException: Could not load file or assembly 'PclHarmony.Contract'
/// Hence: resolve first, then reach Host through reflection.
/// </summary>
internal static class StartupHook
{
    static StartupHook()
    {
        RegisterResolver();
    }

    public static void Initialize()
    {
        // Resolver is already registered by the static constructor; use reflection
        // so Host's dependencies are only bound when Invoke actually JITs it.
        try
        {
            var host = typeof(StartupHook).Assembly.GetType("PclHarmony.Ce.Host", true);
            var start = host.GetMethod("Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (start == null) return;
            start.Invoke(null, null);
        }
        catch
        {
            // A loader must never take the launcher down.
        }
    }

    private static void RegisterResolver()
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate (object sender, ResolveEventArgs args)
            {
                try
                {
                    string requested;
                    try { requested = new AssemblyName(args.Name).Name; }
                    catch { return null; }

                    string dir = null;
                    try
                    {
                        string location = typeof(StartupHook).Assembly.Location;
                        if (!string.IsNullOrEmpty(location)) dir = Path.GetDirectoryName(location);
                    }
                    catch { }
                    if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                    if (string.IsNullOrEmpty(dir)) return null;

                    string candidate = Path.Combine(dir, requested + ".dll");
                    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
                }
                catch { return null; }
            };
        }
        catch { }
    }
}
