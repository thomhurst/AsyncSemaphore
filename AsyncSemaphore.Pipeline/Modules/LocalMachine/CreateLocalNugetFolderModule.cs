using Microsoft.Extensions.Logging;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.FileSystem;
using ModularPipelines.Modules;

namespace AsyncSemaphore.Pipeline.Modules.LocalMachine;

[DependsOn<RunUnitTestsModule>]
[DependsOn<PackagePathsModule>]
public class CreateLocalNugetFolderModule : Module<Folder>
{
    protected override async Task<Folder?> ExecuteAsync(IPipelineContext context, CancellationToken cancellationToken)
    {
        var localNugetRepositoryFolder = context.FileSystem.GetFolder(Environment.SpecialFolder.ApplicationData)
            .GetFolder("ModularPipelines")
            .GetFolder("LocalNuget")
            .Create();
        
        await Task.Yield();

        context.Logger.LogInformation("Local NuGet Repository Path: {Path}", localNugetRepositoryFolder.Path);

        return localNugetRepositoryFolder;
    }
}
