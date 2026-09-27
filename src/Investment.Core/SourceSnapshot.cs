using System.Security.Cryptography;
using System.Text;
using System.Reflection;

namespace Investment.Core;

public sealed record SourceFile(string Path, string Content);
public sealed record BinaryProof(string AssemblyName, string Sha256, string SourceManifest);
public sealed record SourceSnapshot(string Hash, string Runtime, SourceFile[] Files, BinaryProof[]? Binaries = null)
{
    public static SourceSnapshot Capture(string root, params Assembly[] assemblies)
    {
        var sources = new List<SourceFile>();
        foreach (var folder in new[] { "src", "tests", "config" })
        {
            var directory = System.IO.Path.Combine(root, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Split('/').Any(s => s is "bin" or "obj")) continue;
                if (System.IO.Path.GetExtension(path) is not (".cs" or ".csproj" or ".json")) continue;
                sources.Add(new(relative, File.ReadAllText(path)));
            }
        }
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Investment.sln", "NuGet.Config" })
        {
            var path = System.IO.Path.Combine(root, name); if (File.Exists(path)) sources.Add(new(name, File.ReadAllText(path)));
        }
        if (sources.Count == 0) throw new ArgumentException("Run from the repository root to capture source provenance.");
        var files = sources.OrderBy(s => s.Path, StringComparer.Ordinal).ToArray();
        var binaries = assemblies.Select(a =>
        {
            using var resource = a.GetManifestResourceStream("Investment.SourceManifest") ?? throw new InvalidOperationException("Binary has no source manifest; rebuild before research.");
            using var reader = new StreamReader(resource); var manifest = reader.ReadToEnd();
            ValidateBuildManifest(manifest, root);
            using var binary = File.OpenRead(a.Location);
            return new BinaryProof(a.GetName().Name!, Convert.ToHexString(SHA256.HashData(binary)), manifest);
        }).ToArray();
        return new(ContentHash(files), System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, files, binaries);
    }
    public void ValidateArchive()
    {
        if (Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != Files.Length || Hash != ContentHash(Files)) throw new ArgumentException("Source archive content/hash mismatch.");
    }
    private static string ContentHash(SourceFile[] files) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(files))));
    public static void ValidateBuildManifest(string manifest, string root)
    {
        root = System.IO.Path.GetFullPath(root);
        var expected = Directory.EnumerateFiles(System.IO.Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(p => !System.IO.Path.GetRelativePath(root, p).Split(System.IO.Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj") && System.IO.Path.GetExtension(p) is ".cs" or ".csproj")
            .Select(System.IO.Path.GetFullPath).Concat(new[] { "Directory.Build.props", "Directory.Build.targets", "Investment.sln", "NuGet.Config" }.Select(n => System.IO.Path.Combine(root, n)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in manifest.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 66 || line[64] != '|') throw new InvalidOperationException("Invalid binary source manifest; rebuild.");
            var path = System.IO.Path.GetFullPath(line[65..]);
            if (!expected.Contains(path) || !seen.Add(path)) throw new InvalidOperationException("Binary/source tree mismatch; rebuild in this workspace.");
            using var file = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(line[..64], StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Source changed after binary build; rebuild before research.");
        }
        if (!seen.SetEquals(expected)) throw new InvalidOperationException("Source set changed after build; rebuild before research.");
    }
}
