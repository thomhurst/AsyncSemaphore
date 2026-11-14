using Microsoft.Extensions.Logging;
using ModularPipelines;
using ModularPipelines.FileSystem;

namespace AsyncSemaphore.Pipeline.Modules.LocalMachine;

[DependsOn<RunUnitTestsModule>]
[DependsOn<PackagePathsModule>]
public class CreateLocalNugetFolderModule : Module<FolderPath>
{
    protected override async Task<FolderPath> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localNugetRepositoryFolder = new FolderPath(appDataPath)
            .GetFolder("ModularPipelines")
            .GetFolder("LocalNuget")
            .Create();

        await Task.Yield();

        context.Logger.LogInformation("Local NuGet Repository Path: {Path}", localNugetRepositoryFolder.Path);

        return localNugetRepositoryFolder;
    }
}
