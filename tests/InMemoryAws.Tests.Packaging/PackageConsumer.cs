using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TUnit.Core.Interfaces;

namespace InMemoryAws.Tests.Packaging;

/// <summary>
/// A workspace outside the repository with the InMemoryAws package packed into a feed of its own, and the consumer
/// projects under <c>Consumers/</c> copied into it, restoring from that feed and nuget.org.
/// </summary>
public sealed class PackageConsumer : IAsyncInitializer, IAsyncDisposable
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public string Workspace { get; } = Path.Combine(Path.GetTempPath(), "inmemoryaws-packaging-" + Guid.NewGuid().ToString("N")[..8]);

    private string PackagesFolder => Path.Combine(Workspace, "packages");

    public async Task InitializeAsync()
    {
        var feed = Path.Combine(Workspace, "feed");
        Directory.CreateDirectory(feed);

        // A version no other restore has seen, so NuGet can't serve a stale copy from a cache.
        var version = "0.0.0-packaging." + Path.GetFileName(Workspace)["inmemoryaws-packaging-".Length..];
        var pack = await DotNetAsync(
            RepositoryRoot,
            "pack", Path.Combine(RepositoryRoot, "src", "InMemoryAws", "InMemoryAws.csproj"),
            "--configuration", "Release", "--output", feed, "-p:PackageVersion=" + version);
        pack.ShouldSucceed();

        CopyDirectory(Path.Combine(RepositoryRoot, "tests", "InMemoryAws.Tests.Packaging", "Consumers"), Workspace);
        File.Copy(Path.Combine(RepositoryRoot, "global.json"), Path.Combine(Workspace, "global.json"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "nuget.config"), NuGetConfig(feed));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "Directory.Packages.props"), PackageVersions(version));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Workspace, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS to clean up.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Restores, then runs <c>dotnet</c> with <paramref name="arguments"/> in the consumer project at <paramref name="project"/>.</summary>
    public async Task<DotNetResult> RunAsync(string project, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var directory = Path.Combine(Workspace, project);
        var restore = await DotNetAsync(directory, "restore");
        restore.ShouldSucceed();
        return await DotNetAsync(directory, arguments);
    }

    /// <summary>The files <c>build/InMemoryAws.targets</c> added to the compilation of the consumer project at <paramref name="project"/>.</summary>
    public async Task<IReadOnlyList<string>> SdkSourcesCompiledAsync(string project)
    {
        // With a target named, -getItem reports the items once it has run, including those targets added.
        var build = await RunAsync(project, "build", "--no-restore", "-t:Build", "-getItem:Compile");
        build.ShouldSucceed();
        using var json = JsonDocument.Parse(build.Output[build.Output.IndexOf('{', StringComparison.Ordinal)..]);
        return json.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
            .Select(item => item.GetProperty("FullPath").GetString()!)
            .Where(path => path.Replace('\\', '/').Contains("/build/sources/", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList()!;
    }

    private async Task<DotNetResult> DotNetAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The test host's own MSBuild settings would leak into the child build, and a packages folder of the
        // workspace's own keeps the test package out of the user's.
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment["NUGET_PACKAGES"] = PackagesFolder;
        start.Environment["MSBUILDTERMINALLOGGER"] = "off";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(start)!;
        var output = new StringBuilder();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        output.Append(await stdout).Append(await stderr);
        return new DotNetResult(string.Join(' ', arguments), process.ExitCode, output.ToString());
    }

    private static string NuGetConfig(string feed)
    {
        // The packages already restored for this repository serve the AWS SDK without a download.
        var globalPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add", new XAttribute("key", "InMemoryAws"), new XAttribute("value", feed)),
                    new XElement("add", new XAttribute("key", "NuGet"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
                new XElement("packageSourceMapping",
                    new XElement("packageSource", new XAttribute("key", "InMemoryAws"),
                        new XElement("package", new XAttribute("pattern", "InMemoryAws"))),
                    new XElement("packageSource", new XAttribute("key", "NuGet"),
                        new XElement("package", new XAttribute("pattern", "*")))),
                new XElement("fallbackPackageFolders",
                    new XElement("add", new XAttribute("key", "repository"), new XAttribute("value", globalPackages))))).ToString();
    }

    /// <summary>The test package's version, and the repository's own AWS SDK versions, which Renovate keeps current.</summary>
    private static string PackageVersions(string version)
    {
        var repository = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Packages.props"));
        var awsSdk = repository.Descendants("PackageVersion")
            .Where(package => ((string?)package.Attribute("Include"))?.StartsWith("AWSSDK.", StringComparison.Ordinal) == true)
            .Select(package => new XElement(package));
        return new XElement("Project",
            new XElement("ItemGroup",
                new XElement("PackageVersion", new XAttribute("Include", "InMemoryAws"), new XAttribute("Version", version)),
                awsSdk)).ToString();
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InMemoryAws.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Can't find the repository root (InMemoryAws.slnx) above " + AppContext.BaseDirectory);
    }
}

/// <summary>What a <c>dotnet</c> command did.</summary>
public sealed record DotNetResult(string Command, int ExitCode, string Output)
{
    public void ShouldSucceed()
    {
        if (ExitCode != 0)
        {
            throw new InvalidOperationException($"'dotnet {Command}' exited with {ExitCode}:{Environment.NewLine}{Output}");
        }
    }
}
