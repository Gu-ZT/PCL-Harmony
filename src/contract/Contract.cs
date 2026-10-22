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

        /// <summary>"prefix" or "postfix".</summary>
        public string HookKind;

        /// <summary>Full name of the type that declares the hook.</summary>
        public string HookTypeName;

        /// <summary>
        /// Name of a PUBLIC STATIC method on <see cref="HookTypeName"/>.
        /// It must be a real method, not a lambda: a compiler-generated closure
        /// (e.g. "&lt;GetPatches&gt;b__2_0") makes Harmony fail when it takes the
        /// MethodInfo, even though the same shape works as a named static method.
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
        /// </summary>
        /// <param name="hostKind">"official" or "ce".</param>
        /// <param name="hostVersion">Version of the launcher assembly being patched.</param>
        IEnumerable<PatchSpec> GetPatches(string hostKind, string hostVersion);
    }
}
