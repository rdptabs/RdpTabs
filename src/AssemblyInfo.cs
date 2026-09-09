using System;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;

// Version metadata for the released binary, so Explorer's property sheet and `RdpTabs.exe --version`
// both name the build. Bump AppVersion below when tagging a release; the csproj sets
// GenerateAssemblyInfo=false so the net8 path uses these same attributes instead of generating its own.

[assembly: AssemblyTitle("RdpTabs")]
[assembly: AssemblyDescription("A Remote Desktop client with Chrome-style tabs")]
[assembly: AssemblyProduct("RdpTabs")]
[assembly: AssemblyCopyright("Copyright 2026 yzhou79 -- Apache License 2.0")]
[assembly: AssemblyVersion(RdpTabs.AppInfo.AssemblyVersion)]
[assembly: AssemblyFileVersion(RdpTabs.AppInfo.AssemblyVersion)]
[assembly: AssemblyInformationalVersion(RdpTabs.AppInfo.Version)]
[assembly: ComVisible(false)]
[assembly: NeutralResourcesLanguage("en")]

namespace RdpTabs
{
    internal static class AppInfo
    {
        /// <summary>Release version. Keep in step with the git tag.</summary>
        public const string Version = "1.0.1";

        /// <summary>Four-part form required by AssemblyVersion / AssemblyFileVersion.</summary>
        public const string AssemblyVersion = Version + ".0";

        /// <summary>One line for --version, naming the runtime that actually loaded us.</summary>
        public static string Describe()
        {
            return "RdpTabs " + Version + " (" + (IntPtr.Size == 8 ? "x64" : "x86") +
                ", .NET " + Environment.Version + ")";
        }
    }
}
