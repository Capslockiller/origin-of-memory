using System.Reflection;

namespace Oom.Contracts;

public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "3.1.0+unknown";
}
