using EnumerableAsyncProcessor.Extensions;
using Microsoft.Extensions.Logging;
using ModularPipelines;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Options;

namespace AsyncSemaphore.Pipeline.Modules.LocalMachine;

[DependsOn<AddLocalNugetSourceModule>]
[DependsOn<PackagePathsModule>]
[DependsOn<CreateLocalNugetFolderModule>]
public class UploadPackagesToLocalNuGetModule : Module<CommandResult[]>
{
    protected override async Task OnBeforeExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var packagePaths = await context.GetModule<PackagePathsModule>();
        foreach (var packagePath in packagePaths.ValueOrDefault!)
        {
            context.Logger.LogInformation("[Local Directory] Uploading {File}", packagePath);
        }
    }

    protected override async Task<CommandResult[]> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var localRepoLocation = await context.GetModule<CreateLocalNugetFolderModule>();
        var packagePaths = await context.GetModule<PackagePathsModule>();
        return await packagePaths.ValueOrDefault!.SelectAsync(async file => await context.Tools.DotNet
            .NuGet
            .PushAsync(new DotNetNuGetPushOptions([file])
            {
                Source = localRepoLocation.ValueOrDefault!,
            }), cancellationToken: cancellationToken).ProcessOneAtATime();
    }
}
