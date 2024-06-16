using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularPipelines.Extensions;
using ModularPipelines.Host;
using AsyncSemaphore.Pipeline.Modules;
using AsyncSemaphore.Pipeline.Modules.LocalMachine;
using AsyncSemaphore.Pipeline.Settings;

await PipelineHostBuilder.Create()
    .ConfigureAppConfiguration((_, builder) =>
    {
        builder.AddJsonFile("appsettings.json")
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables();
    })
    .ConfigureServices((context, collection) =>
    {
        collection.Configure<NuGetSettings>(context.Configuration.GetSection("NuGet"));

        if (context.HostingEnvironment.IsDevelopment())
        {
            collection.AddModule<CreateLocalNugetFolderModule>()
                .AddModule<AddLocalNugetSourceModule>()
                .AddModule<UploadPackagesToLocalNuGetModule>();
        }
        else
        {
            collection.AddModule<UploadPackagesToNugetModule>();
        }
    })
    .AddModule<RunUnitTestsModule>()
    .AddModule<NugetVersionGeneratorModule>()
    .AddModule<PackProjectsModule>()
    .AddModule<PackageFilesRemovalModule>()
    .AddModule<PackagePathsParserModule>()
    .ExecutePipelineAsync();
