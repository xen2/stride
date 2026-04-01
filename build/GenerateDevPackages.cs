// Dev-redirect NuGet stub generator for Stride.
// Usage: dotnet run build/GenerateDevPackages.cs -- [options]
//
// Generates stub .nupkg files that redirect to dev-built DLLs,
// eliminating the ~50s NuGet packing overhead on every incremental build.
//
// Steps:
//   1. Packs fresh nupkgs (--no-build) into a temp dir
//   2. Strips managed DLLs/PDBs, adds _._  markers
//   3. Injects build/<PkgId>.props with <Reference HintPath> redirects
//   4. Deploys to NugetDev, invalidates NuGet cache
//   5. Writes a stamp file so subsequent builds skip generation

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// --- Parse arguments ---
var strideRoot = "";
var configuration = "Debug";
var solution = "";
var version = "";
var nugetDevDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "NugetDev");

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--stride-root" when i + 1 < args.Length: strideRoot = args[++i]; break;
        case "--configuration" when i + 1 < args.Length: configuration = args[++i]; break;
        case "--solution" when i + 1 < args.Length: solution = args[++i]; break;
        case "--version" when i + 1 < args.Length: version = args[++i]; break;
        case "--nuget-dev" when i + 1 < args.Length: nugetDevDir = args[++i]; break;
    }
}

// --- Resolve defaults ---
if (string.IsNullOrEmpty(strideRoot))
{
    // Walk up from this script's location to find repo root
    var dir = AppContext.BaseDirectory;
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir, "sources", "targets")))
        {
            strideRoot = dir;
            break;
        }
        dir = Path.GetDirectoryName(dir);
    }
    if (string.IsNullOrEmpty(strideRoot))
    {
        // Fallback: assume CWD
        strideRoot = Directory.GetCurrentDirectory();
    }
}

if (string.IsNullOrEmpty(solution))
{
    solution = Directory.GetFiles(Path.Combine(strideRoot, "build"), "Stride.slnx").FirstOrDefault()
        ?? Path.Combine(strideRoot, "build", "Stride.sln");
}

if (string.IsNullOrEmpty(version))
{
    var sharedInfo = File.ReadAllText(Path.Combine(strideRoot, "sources", "shared", "SharedAssemblyInfo.cs"));
    var match = Regex.Match(sharedInfo, @"PublicVersion\s*=\s*""([^""]+)""");
    version = match.Success ? match.Groups[1].Value : throw new Exception("Could not determine version from SharedAssemblyInfo.cs");
}

Console.WriteLine($"Stride version: {version}");
Console.WriteLine($"Dev root: {strideRoot}");
Console.WriteLine($"Configuration: {configuration}");
Console.WriteLine($"Solution: {solution}");
Console.WriteLine($"NugetDev: {nugetDevDir}");

// --- Step 1: Pack fresh nupkgs ---
var tempPackDir = Path.Combine(Path.GetTempPath(), "stride-devpackages-pack");
if (Directory.Exists(tempPackDir)) Directory.Delete(tempPackDir, true);
Directory.CreateDirectory(tempPackDir);

Console.WriteLine("\nPacking fresh packages (--no-build)...");
// Some projects in the solution may not have been built — ignore pack errors and work with whatever succeeds.
// Output is suppressed to prevent MSBuild-formatted error lines from leaking into the parent build.
RunProcess("dotnet", $"pack \"{solution}\" --no-build -c {configuration} -p:StrideSkipAutoPack=false -p:StrideDevPackages=false -o \"{tempPackDir}\" --verbosity quiet", silent: true);

var freshPackages = Directory.GetFiles(tempPackDir, $"*.{version}.nupkg");
Console.WriteLine($"Packed {freshPackages.Length} packages");

if (freshPackages.Length == 0)
{
    Console.Error.WriteLine($"ERROR: No packages found for version {version}");
    return 1;
}

// --- Step 2: Build project map from solution ---
var projectMap = BuildProjectMap(solution, strideRoot);
Console.WriteLine($"Found {projectMap.Count} project mappings");

// --- Step 3: Process each package ---
Directory.CreateDirectory(nugetDevDir);
var stubCount = 0;
var skipCount = 0;
var nugetPackagesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

foreach (var pkgPath in freshPackages)
{
    var pkgFileName = Path.GetFileName(pkgPath);
    var pkgId = Regex.Replace(pkgFileName, $@"\.{Regex.Escape(version)}\.nupkg$", "", RegexOptions.IgnoreCase);

    if (!projectMap.TryGetValue(pkgId, out var projInfo))
    {
        Console.WriteLine($"  SKIP {pkgId} (no matching project)");
        skipCount++;
        continue;
    }

    Console.Write($"  {pkgId}...");

    try
    {
        ProcessPackage(pkgPath, pkgId, projInfo, nugetDevDir, nugetPackagesDir, version, strideRoot, configuration);
        stubCount++;
        Console.WriteLine(" OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($" ERROR: {ex.Message}");
    }
}

// --- Step 4: Write stamp file ---
var stampPath = Path.Combine(nugetDevDir, $".devpackages-{version}");
File.WriteAllText(stampPath, $"{DateTime.UtcNow:O}\n{configuration}\n{strideRoot}");

// Cleanup temp
try { Directory.Delete(tempPackDir, true); } catch { }

Console.WriteLine($"\nDone! Generated {stubCount} stubs, skipped {skipCount}.");
return 0;

// ============================================================
// Helper methods
// ============================================================

static int RunProcess(string fileName, string arguments, bool silent = false)
{
    var psi = new ProcessStartInfo(fileName, arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0 && !silent)
    {
        if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
    }
    return proc.ExitCode;
}

static Dictionary<string, ProjectInfo> BuildProjectMap(string solution, string strideRoot)
{
    var map = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);
    var slnDir = Path.GetDirectoryName(solution)!;

    var psi = new ProcessStartInfo("dotnet", $"sln \"{solution}\" list")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
    };
    var proc = Process.Start(psi)!;
    var output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();

    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var trimmed = line.Trim();
        if (!trimmed.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;

        var csprojPath = Path.GetFullPath(Path.Combine(slnDir, trimmed));
        if (!File.Exists(csprojPath)) continue;

        var content = File.ReadAllText(csprojPath);
        var projName = Path.GetFileNameWithoutExtension(csprojPath);
        var projDir = Path.GetDirectoryName(csprojPath)!;

        var pkgId = projName;
        var m = Regex.Match(content, @"<PackageId>([^<]+)</PackageId>");
        if (m.Success) pkgId = m.Groups[1].Value;

        var asmName = projName;
        m = Regex.Match(content, @"<AssemblyName>([^<]+)</AssemblyName>");
        if (m.Success) asmName = m.Groups[1].Value;

        var isGraphicsDependent = Regex.IsMatch(content, @"<StrideGraphicsApiDependent\s*>true</StrideGraphicsApiDependent>");

        var info = new ProjectInfo(projDir, asmName, isGraphicsDependent, csprojPath);

        // Don't overwrite — first match in solution wins
        map.TryAdd(pkgId, info);
        if (!string.Equals(asmName, pkgId, StringComparison.OrdinalIgnoreCase))
            map.TryAdd(asmName, info);
    }

    return map;
}

static void ProcessPackage(string pkgPath, string pkgId, ProjectInfo projInfo, string nugetDevDir,
    string nugetPackagesDir, string version, string strideRoot, string configuration)
{
    using var zip = ZipFile.Open(pkgPath, ZipArchiveMode.Update);

    // Find and remove own managed DLLs/PDBs
    var affectedDirs = new HashSet<string>();
    var toRemove = new List<string>();

    foreach (var entry in zip.Entries.ToList())
    {
        if (!entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)) continue;

        var name = Path.GetFileName(entry.FullName);
        var ext = Path.GetExtension(name).ToLowerInvariant();

        var isOwnDll = (ext is ".dll" or ".exe") && name.StartsWith(projInfo.AssemblyName + ".", StringComparison.OrdinalIgnoreCase);
        var isOwnPdb = ext == ".pdb" && name.StartsWith(projInfo.AssemblyName + ".", StringComparison.OrdinalIgnoreCase);

        if (isOwnDll || isOwnPdb)
        {
            var lastSlash = entry.FullName.LastIndexOf('/');
            var dir = lastSlash >= 0 ? entry.FullName.Substring(0, lastSlash + 1) : "";
            affectedDirs.Add(dir);
            toRemove.Add(entry.FullName);
        }
    }

    foreach (var entryName in toRemove)
    {
        zip.GetEntry(entryName)?.Delete();
    }

    // Add _._  markers in emptied lib/ directories
    foreach (var dir in affectedDirs)
    {
        var markerPath = dir + "_._";
        var hasDlls = zip.Entries.Any(e =>
            e.FullName.StartsWith(dir, StringComparison.OrdinalIgnoreCase) &&
            (e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
             e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));

        if (!hasDlls && zip.GetEntry(markerPath) == null)
        {
            var marker = zip.CreateEntry(markerPath);
            marker.Open().Close();
        }
    }

    // Generate and inject redirect props
    var propsContent = GenerateRedirectProps(pkgId, projInfo, strideRoot, configuration);

    foreach (var path in new[] { $"build/{pkgId}.props", $"buildTransitive/{pkgId}.props" })
    {
        zip.GetEntry(path)?.Delete();
        var entry = zip.CreateEntry(path);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(propsContent);
    }

    // Close zip before copying
    zip.Dispose();

    // Deploy to NugetDev
    var destPath = Path.Combine(nugetDevDir, Path.GetFileName(pkgPath));
    File.Copy(pkgPath, destPath, overwrite: true);

    // Invalidate NuGet cache
    var cacheDir = Path.Combine(nugetPackagesDir, pkgId.ToLowerInvariant(), version);
    if (Directory.Exists(cacheDir))
    {
        var sha512 = Path.Combine(cacheDir, $"{pkgId}.{version}.nupkg.sha512");
        var metadata = Path.Combine(cacheDir, ".nupkg.metadata");
        if (File.Exists(sha512)) File.Delete(sha512);
        if (File.Exists(metadata)) File.Delete(metadata);
    }
}

static string GenerateRedirectProps(string pkgId, ProjectInfo projInfo, string strideRoot, string configuration)
{
    var relProjDir = Path.GetRelativePath(strideRoot, projInfo.ProjectDir);

    // Special handling for tool packages
    if (string.Equals(pkgId, "Stride.Core.Assets.CompilerApp", StringComparison.OrdinalIgnoreCase))
    {
        return $"""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <StrideDevRedirect>true</StrideDevRedirect>
                <StrideDevRoot Condition="'$(StrideDevRoot)' == ''">{strideRoot}</StrideDevRoot>
                <StrideDevConfiguration Condition="'$(StrideDevConfiguration)' == ''">{configuration}</StrideDevConfiguration>
                <StrideCompileAssetCommand>$(StrideDevRoot)/{relProjDir.Replace('\\', '/')}/bin/$(StrideDevConfiguration)/net10.0/{projInfo.AssemblyName}.dll</StrideCompileAssetCommand>
              </PropertyGroup>
            </Project>
            """;
    }

    var hintPath = projInfo.IsGraphicsDependent
        ? $"$(StrideDevRoot)/{relProjDir.Replace('\\', '/')}/bin/$(StrideDevConfiguration)/net10.0/$(StrideGraphicsApi)/{projInfo.AssemblyName}.dll"
        : $"$(StrideDevRoot)/{relProjDir.Replace('\\', '/')}/bin/$(StrideDevConfiguration)/net10.0/{projInfo.AssemblyName}.dll";

    return $"""
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <StrideDevRedirect>true</StrideDevRedirect>
            <StrideDevRoot Condition="'$(StrideDevRoot)' == ''">{strideRoot}</StrideDevRoot>
            <StrideDevConfiguration Condition="'$(StrideDevConfiguration)' == ''">{configuration}</StrideDevConfiguration>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="{projInfo.AssemblyName}">
              <HintPath>{hintPath}</HintPath>
              <Private>true</Private>
            </Reference>
          </ItemGroup>
        </Project>
        """;
}

record ProjectInfo(string ProjectDir, string AssemblyName, bool IsGraphicsDependent, string CsprojPath);
