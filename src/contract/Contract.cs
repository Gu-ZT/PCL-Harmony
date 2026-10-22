using System.Collections.Generic;

namespace PclHarmony.Contract
{
    /// <summary>
    /// One patch a plugin wants applied.
    ///
    /// The plugin does NOT call Harmony itself. A netstandard2.0 assembly cannot
    /// drive Harmony: Lib.Harmony ships no netstandard2.0 build, and loading the
    /// net48 one from a netstandard2.0 caller dies in MonoMod with
    /// "GenericArguments[0] (CecilILGenerator) violates the constraint of type
    /// parameter TTarget". So the plugin only DESCRIBES the work and the host,
    /// which owns a Harmony build matching its own runtime, performs it.
    /// </summary>
    public sealed class PatchSpec
    {
        /// <summary>Full name of the target type, e.g. "PCL.Core.App.IoC.Lifecycle".</summary>
        public string TypeName;

        /// <summary>Target method name.</summary>
        public string MethodName;

        /// <summary>
        /// What to do with the target:
        ///
        ///   "prefix"   - run the hook before the target body.
        ///   "postfix"  - run the hook after the target body.
        ///   "redirect" - the target itself cannot be patched (see below), so every
        ///                CALL SITE is rewritten to call the hook instead.
        ///
        /// Hooks receive the target's own parameters by NAME, plus the usual
        /// Harmony-injected names: <c>__instance</c> for the target instance and
        /// <c>__result</c> (by reference) to read or replace the return value.
        /// That binding is done by Harmony from the hook's parameter names, so it
        /// needs no field here - e.g. a hook declared as
        /// <c>static void Post(object __instance, ref object __result)</c> works.
        /// </summary>
        public string HookKind;

        /// <summary>Full name of the type that declares the hook.</summary>
        public string HookTypeName;

        /// <summary>
        /// Name of a PUBLIC STATIC method on <see cref="HookTypeName"/>.
        /// It must be a real method, not a lambda: a compiler-generated closure
        /// (e.g. "&lt;GetPatches&gt;b__2_0") makes Harmony fail when it takes the
        /// MethodInfo, even though the same shape works as a named static method.
        ///
        /// For <c>"redirect"</c> this is the drop-in replacement: it must accept the
        /// same parameter list as the target and return <c>object</c>. The host
        /// rewrites each call site to invoke it and then casts the result back to the
        /// target's declared return type, which is what keeps the rewritten stack
        /// shape identical to the original call.
        /// </summary>
        public string HookMethodName;

        /// <summary>Parameter type names for overload selection; null = first match.</summary>
        public string[] TargetParameterTypes;

        public override string ToString()
        {
            return (HookKind ?? "postfix") + " " + TypeName + "." + MethodName
                   + " -> " + HookTypeName + "." + HookMethodName;
        }
    }

    /// <summary>
    /// Services the host offers a plugin.
    ///
    /// Plugins must not reference the loader assembly directly: the official side
    /// ships a net48 loader and the CE side a different host entirely, so anything
    /// shared has to come through this contract.
    /// </summary>
    public interface IPluginHost
    {
        /// <summary>Appends a line to the loader log. Must never throw.</summary>
        void Log(string message);
    }

    /// <summary>
    /// Implemented by plugins that want to run on both the official launcher
    /// (net48/CLR4) and PCL-CE (net10/CoreCLR). Implementations need a public
    /// parameterless constructor and must be free of Harmony types.
    /// </summary>
    public interface IDeclarativePlugin
    {
        /// <summary>Stable, human readable name.</summary>
        string Name { get; }

        /// <summary>
        /// Patches this plugin wants applied. Called once per host, before the
        /// host applies anything; a plugin may return different specs per runtime.
        ///
        /// Do not assume the launcher's types exist yet: on PCL-CE this runs before
        /// the launcher assembly is loaded, and the host applies (or retries) each
        /// spec once its target becomes resolvable.
        /// </summary>
        /// <param name="hostKind">"official" or "ce".</param>
        /// <param name="hostVersion">Version of the launcher assembly being patched.</param>
        /// <param name="host">Host services, notably logging.</param>
        IEnumerable<PatchSpec> GetPatches(string hostKind, string hostVersion, IPluginHost host);
    }
}
