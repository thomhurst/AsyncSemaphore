using ModularPipelines;
using ModularPipelines.Context;
using ModularPipelines.Exceptions;
using ModularPipelines.Git;
using ModularPipelines.Git.Options;

namespace AsyncSemaphore.Pipeline.Modules;

[RunOnlyOnBranch("main")]
[RunIf<OnLinux>]
[DependsOn<NugetVersionGeneratorModule>]
public class PushVersionTagModule : Module<CommandResult>
{
    protected override void Configure(ModuleConfigurationBuilder module) => module
        .WithIgnoreFailuresWhen(async (ctx, ex, _) =>
        {
            var versionInformation = await ctx.GetModule<NugetVersionGeneratorModule>();

            // v4 no longer puts the command output in the exception message.
            return ex is CommandException commandException
                   && commandException.Result.StandardError.Contains($"tag 'v{versionInformation.ValueOrDefault}' already exists");
        });

    protected override async Task<CommandResult> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var versionInformation = await context.GetModule<NugetVersionGeneratorModule>();

        await context.Tools.Git.Commands.Branches.TagAsync(new GitTagOptions
        {
            TagName = $"v{versionInformation.ValueOrDefault}",
        }, cancellationToken: cancellationToken);

        return await context.Tools.Git.Commands.Remotes.PushAsync(new GitPushOptions
        {
            Tags = true,
        }, cancellationToken: cancellationToken);
    }
}
