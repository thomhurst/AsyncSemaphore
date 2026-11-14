using ModularPipelines;
using ModularPipelines.Context;

namespace AsyncSemaphore.Pipeline.Modules;

public class PackageFilesRemovalModule : Module
{
    protected override async Task ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var git = await context.Tools.Git.Information.GetRequiredInfoAsync(cancellationToken);
        var packageFiles = git.Root.GetFiles(path => path.Extension is ".nupkg");

        foreach (var packageFile in packageFiles)
        {
            packageFile.Delete();
        }
    }
}
