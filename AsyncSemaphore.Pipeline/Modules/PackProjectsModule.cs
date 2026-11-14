using Microsoft.Extensions.Logging;
using ModularPipelines;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Options;
using File = ModularPipelines.FileSystem.FilePath;

namespace AsyncSemaphore.Pipeline.Modules;

[DependsOn<PackageFilesRemovalModule>]
[DependsOn<NugetVersionGeneratorModule>]
[DependsOn<RunUnitTestsModule>]
public class PackProjectsModule : Module<List<CommandResult>>
{
    protected override async Task<List<CommandResult>> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var results = new List<CommandResult>();
        var packageVersion = await context.GetModule<NugetVersionGeneratorModule>();
        var git = await context.Tools.Git.Information.GetRequiredInfoAsync(cancellationToken);
        var projectFiles = git.Root.GetFiles(f => GetProjectsPredicate(f, context));
        foreach (var projectFile in projectFiles)
        {
            results.Add(await context.Tools.DotNet.PackAsync(new DotNetPackOptions {
                ProjectSolution = projectFile.Path,
                Configuration = "Release",
                Properties = new KeyValue[]
                {
                    ("PackageVersion", packageVersion.ValueOrDefault)!,
                    ("Version", packageVersion.ValueOrDefault)!,
                },
                IncludeSource = true
            }));
        }

        return results;
    }

    private bool GetProjectsPredicate(File file, IModuleContext context)
    {
        var path = file.Path;
        if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Contains("Tests", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Pipeline", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Benchmark", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Example", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        context.Logger.LogInformation("Found File: {File}", path);
        return true;
    }
}
