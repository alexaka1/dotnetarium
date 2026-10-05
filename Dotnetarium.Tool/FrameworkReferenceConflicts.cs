using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Dotnetarium.Tool;

// Resolve only global package-vs-framework conflicts. Explicit HintPath and
// aliased references are not interchangeable and retain their original metadata.
internal static class FrameworkReferenceConflicts
{
    internal static void Resolve(HashSet<string> frameworks, Dictionary<string, MetadataReferenceProperties> packages)
    {
        var known = frameworks.Select(path => (Path: path, Assembly: AssemblyName.GetAssemblyName(path)))
            .ToDictionary(item => Key(item.Assembly), item => item, StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.ToArray())
        {
            if (!package.Value.Aliases.IsDefaultOrEmpty && package.Value.Aliases.Any(alias => alias != "global")) continue;
            var assembly = AssemblyName.GetAssemblyName(package.Key);
            if (!known.TryGetValue(Key(assembly), out var framework) || package.Key == framework.Path) continue;
            var comparison = assembly.Version!.CompareTo(framework.Assembly.Version);
            if (comparison == 0) comparison = FileVersion(package.Key).CompareTo(FileVersion(framework.Path));
            if (comparison > 0) frameworks.Remove(framework.Path);
            else packages.Remove(package.Key);
        }
    }

    private static string Key(AssemblyName assembly) =>
        $"{assembly.Name}:{assembly.CultureName}:{Convert.ToHexString(assembly.GetPublicKeyToken() ?? [])}";

    private static Version FileVersion(string path)
    {
        var file = FileVersionInfo.GetVersionInfo(path);
        return new(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart, file.FilePrivatePart);
    }
}
