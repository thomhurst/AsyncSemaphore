using Microsoft.Extensions.Logging;
using ModularPipelines.Configuration;
using ModularPipelines.Context;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;

// ReSharper disable HeuristicUnreachableCode
#pragma warning disable CS0162

namespace AsyncSemaphore.Pipeline.Modules;

public class NugetVersionGeneratorModule : Module<string>
{
    protected override ModuleConfiguration Configure() => ModuleConfiguration.Create()
        .WithAfterExecute(async context =>
        {
            var moduleResult = await this;
            context.Logger.LogInformation("NuGet Version to Package: {Version}", moduleResult.ValueOrDefault);
        })
        .Build();

    protected override async Task<string?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var gitVersionInformation = await context.Git().Versioning.GetGitVersioningInformation();

        if (gitVersionInformation.BranchName == "main")
        {
            return gitVersionInformation.SemVer!;
        }

        return $"{gitVersionInformation.Major}.{gitVersionInformation.Minor}.{gitVersionInformation.Patch}-{gitVersionInformation.PreReleaseLabel}-{gitVersionInformation.CommitsSinceVersionSource}";
    }
}
