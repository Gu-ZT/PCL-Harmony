using System.Collections.Generic;
using System.IO;
using PclHarmony.Contract;

namespace PclExample.Shared
{
    /// <summary>
    /// Example plugin that runs on BOTH launchers from one DLL.
    ///
    /// The two builds have nothing in common except the assembly name:
    ///   official  Plain Craft Launcher 2  2.99.0.0  VB.NET / net48 / CLR 4
    ///   PCL-CE    Plain Craft Launcher 2  2.15.1.0  C#     / net10 / CoreCLR
    ///
    /// So <see cref="GetPatches"/> branches on the version it is handed and returns
    /// different specs per side. The hosts do the patching; this type never touches
    /// Harmony (see IDeclarativePlugin for why).
    ///
    /// The hooks are plain public static methods on purpose: a compiler-generated
    /// lambda closure cannot be handed to Harmony.
    /// </summary>
    public sealed class SharedExample : IDeclarativePlugin
    {
        public string Name { get { return "PCL.Example.Shared"; } }

        public IEnumerable<PatchSpec> GetPatches(string hostKind, string hostVersion)
        {
            var specs = new List<PatchSpec>();

            if (hostKind == "ce")
            {
                // PCL-CE: probe the entry point so the loader proves it is live
                // without depending on any CE-internal type name that may move.
                specs.Add(new PatchSpec
                {
                    TypeName = "PCL.Program",
                    MethodName = "Main",
                    HookKind = "postfix",
                    HookTypeName = typeof(SharedExample).FullName,
                    HookMethodName = "CePostMain",
                });
            }
            else
            {
                // Official launcher: the version probe lives in the loader itself,
                // so the example only records that it was reached.
                specs.Add(new PatchSpec
                {
                    TypeName = "PCL.Application",
                    MethodName = "Main",
                    HookKind = "postfix",
                    HookTypeName = typeof(SharedExample).FullName,
                    HookMethodName = "OfficialPostMain",
                });
            }

            return specs;
        }

        // ------------------------------------------------------------- hooks ---

        /// <summary>Runs after PCL-CE's Program.Main returns.</summary>
        public static void CePostMain()
        {
            Note("ce: Program.Main returned");
        }

        /// <summary>Runs after the official launcher's Application.Main returns.</summary>
        public static void OfficialPostMain()
        {
            Note("official: Application.Main returned");
        }

        private static void Note(string message)
        {
            try
            {
                var dir = System.AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
                File.AppendAllText(Path.Combine(dir, "Example.Shared.log"),
                    System.DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + "\n");
            }
            catch { }
        }
    }
}
