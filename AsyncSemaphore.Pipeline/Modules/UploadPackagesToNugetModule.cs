using AsyncSemaphore.Pipeline.Settings;
using EnumerableAsyncProcessor.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Options;

namespace AsyncSemaphore.Pipeline.Modules;

[DependsOn<RunUnitTestsModule>]
[DependsOn<PackagePathsModule>]
public class UploadPackagesToNugetModule : Module<CommandResult[]?>
{
    private readonly IOptions<NuGetSettings> _options;

    public UploadPackagesToNugetModule(IOptions<NuGetSettings> options)
    {
        _options = options;
    }

    protected override void Configure(ModuleConfigurationBuilder module) => module
        .WithSkipWhen(async (context, cancellationToken) =>
        {
            var gitVersionInfo = await context.Tools.Git.Versioning.GetVersioningInformationAsync(cancellationToken);

            if (gitVersionInfo.BranchName != "main")
            {
                return true;
            }

            var publishPackages =
                System.Environment.GetEnvironmentVariable("PUBLISH_PACKAGES")!;

            if (!bool.TryParse(publishPackages, out var shouldPublishPackages) || !shouldPublishPackages)
            {
                return true;
            }

            return false;
        });

    protected override async Task OnBeforeExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var packagePaths = await context.GetModule<PackagePathsModule>();

        foreach (var packagePath in packagePaths.ValueOrDefault!)
        {
            context.Logger.LogInformation("Uploading {File}", packagePath);
        }
    }

    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_options.Value.ApiKey);

        var gitVersionInformation = await context.Tools.Git.Versioning.GetVersioningInformationAsync(cancellationToken);

        if (gitVersionInformation.BranchName != "main")
        {
            return null;
        }

        var packagePaths = await context.GetModule<PackagePathsModule>();

        return await packagePaths.ValueOrDefault!.SelectAsync(async file => await context.Tools.DotNet
            .NuGet
            .PushAsync(new DotNetNuGetPushOptions([file])
            {
                Source = "https://api.nuget.org/v3/index.json",
                ApiKey = _options.Value.ApiKey!
            }), cancellationToken: cancellationToken).ProcessOneAtATime();
    }
}
