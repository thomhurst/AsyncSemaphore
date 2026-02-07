using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Extensions;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;
using File = ModularPipelines.FileSystem.File;

namespace AsyncSemaphore.Pipeline.Modules;

[DependsOn<PackProjectsModule>]
public class PackagePathsModule : Module<List<File>>
{
    protected override async Task<List<File>?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        await Task.Yield();

        return context.Git().RootDirectory.AssertExists().GetFiles(x => x.Extension == ".nupkg").ToList();
    }
}
