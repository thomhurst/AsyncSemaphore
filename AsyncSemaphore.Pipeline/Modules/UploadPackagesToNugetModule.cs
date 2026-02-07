using AsyncSemaphore.Pipeline.Settings;
using EnumerableAsyncProcessor.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Configuration;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Models;
using ModularPipelines.Modules;

namespace AsyncSemaphore.Pipeline.Modules;

[DependsOn<RunUnitTestsModule>]
[DependsOn<PackagePathsModule>]
public class UploadPackagesToNugetModule : Module<CommandResult[]>
{
    private readonly IOptions<NuGetSettings> _options;

    public UploadPackagesToNugetModule(IOptions<NuGetSettings> options)
    {
        _options = options;
    }

    protected override ModuleConfiguration Configure() => ModuleConfiguration.Create()
        .WithSkipWhen(async context =>
        {
            var gitVersionInfo = await context.Git().Versioning.GetGitVersioningInformation();

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
        })
        .WithBeforeExecute(async context =>
        {
            var packagePaths = await context.GetModule<PackagePathsModule>();

            foreach (var packagePath in packagePaths.ValueOrDefault!)
            {
                context.Logger.LogInformation("Uploading {File}", packagePath);
            }
        })
        .Build();

    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_options.Value.ApiKey);

        var gitVersionInformation = await context.Git().Versioning.GetGitVersioningInformation();

        if (gitVersionInformation.BranchName != "main")
        {
            return null;
        }

        var packagePaths = await context.GetModule<PackagePathsModule>();

        return await packagePaths.ValueOrDefault!.SelectAsync(async file => await context.DotNet()
            .Nuget
            .Push(new DotNetNugetPushOptions
            {
                Path = file,
                Source = "https://api.nuget.org/v3/index.json",
                ApiKey = _options.Value.ApiKey!
            }), cancellationToken: cancellationToken).ProcessOneAtATime();
    }
}
