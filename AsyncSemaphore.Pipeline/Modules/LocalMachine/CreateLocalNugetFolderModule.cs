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
    protected override async Task<Folder?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localNugetRepositoryFolder = new ModularPipelines.FileSystem.Folder(appDataPath)
            .GetFolder("ModularPipelines")
            .GetFolder("LocalNuget")
            .Create();

        await Task.Yield();

        context.Logger.LogInformation("Local NuGet Repository Path: {Path}", localNugetRepositoryFolder.Path);

        return localNugetRepositoryFolder;
    }
}
