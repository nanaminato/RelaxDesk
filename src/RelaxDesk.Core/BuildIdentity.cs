using System.Reflection;
namespace RelaxDesk.Core;

public static class BuildIdentity
{
    public static string Version => typeof(BuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";
    public static string Display => "v" + Version;
}
