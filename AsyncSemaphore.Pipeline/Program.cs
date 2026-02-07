using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularPipelines;
using ModularPipelines.Extensions;
using AsyncSemaphore.Pipeline.Modules;
using AsyncSemaphore.Pipeline.Modules.LocalMachine;
using AsyncSemaphore.Pipeline.Settings;

var builder = Pipeline.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.Services.Configure<NuGetSettings>(builder.Configuration.GetSection("NuGet"));

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddModule<CreateLocalNugetFolderModule>()
        .AddModule<AddLocalNugetSourceModule>()
        .AddModule<UploadPackagesToLocalNuGetModule>();
}
else
{
    builder.Services.AddModule<UploadPackagesToNugetModule>();
}

builder.Services.AddModule<RunUnitTestsModule>();
builder.Services.AddModule<NugetVersionGeneratorModule>();
builder.Services.AddModule<PackProjectsModule>();
builder.Services.AddModule<PackageFilesRemovalModule>();
builder.Services.AddModule<PushVersionTagModule>();
builder.Services.AddModule<PackagePathsModule>();

await builder.ExecutePipelineAsync();
