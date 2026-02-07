using EnumerableAsyncProcessor.Extensions;
using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Configuration;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace AsyncSemaphore.Pipeline.Modules.LocalMachine;

[DependsOn<AddLocalNugetSourceModule>]
[DependsOn<PackagePathsModule>]
[DependsOn<CreateLocalNugetFolderModule>]
public class UploadPackagesToLocalNuGetModule : Module<CommandResult[]>
{
    protected override ModuleConfiguration Configure() => ModuleConfiguration.Create()
        .WithBeforeExecute(async context =>
        {
            var packagePaths = await context.GetModule<PackagePathsModule>();
            foreach (var packagePath in packagePaths.ValueOrDefault!)
            {
                context.Logger.LogInformation("[Local Directory] Uploading {File}", packagePath);
            }
        })
        .Build();

    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var localRepoLocation = await context.GetModule<CreateLocalNugetFolderModule>();
        var packagePaths = await context.GetModule<PackagePathsModule>();
        return await packagePaths.ValueOrDefault!.SelectAsync(async file => await context.DotNet()
            .Nuget
            .Push(new DotNetNugetPushOptions
            {
                Path = file,
                Source = localRepoLocation.ValueOrDefault!,
            }), cancellationToken: cancellationToken).ProcessOneAtATime();
    }
}
