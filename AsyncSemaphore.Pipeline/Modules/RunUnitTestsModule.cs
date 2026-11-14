using ModularPipelines;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Options;

namespace AsyncSemaphore.Pipeline.Modules;

public class RunUnitTestsModule : Module<List<CommandResult>>
{
    protected override async Task<List<CommandResult>> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var results = new List<CommandResult>();

        var git = await context.Tools.Git.Information.GetRequiredInfoAsync(cancellationToken);

        foreach (var unitTestProjectFile in git.Root
                     .GetFiles(file => file.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                                       && file.Path.Contains("Tests", StringComparison.OrdinalIgnoreCase)))
        {
            results.Add(await context.Tools.DotNet.TestAsync(new DotNetTestOptions
            {
                Project = unitTestProjectFile,

                // The package ships the Release build, and the stress tests race code whose timing and
                // codegen differ under Debug, so that is the build they have to run against.
                Configuration = "Release",

                // A multi-targeted test project is one module per framework. Run them one at a time so
                // the stress tests contend with each other, not with a second copy of the suite.
                MaxParallelTestModules = 1,
            }));
        }

        return results;
    }
}
