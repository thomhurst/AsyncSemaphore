using ModularPipelines;
using ModularPipelines.Context;
using ModularPipelines.FileSystem;
using File = ModularPipelines.FileSystem.FilePath;

namespace AsyncSemaphore.Pipeline.Modules;

[DependsOn<PackProjectsModule>]
public class PackagePathsModule : Module<List<File>>
{
    protected override async Task<List<File>> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var git = await context.Tools.Git.Information.GetRequiredInfoAsync(cancellationToken);

        return git.Root.AssertExists().GetFiles(x => x.Extension == ".nupkg").ToList();
    }
}
