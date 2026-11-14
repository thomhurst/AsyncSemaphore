using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularPipelines;
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
    builder.AddModule<CreateLocalNugetFolderModule>()
        .AddModule<AddLocalNugetSourceModule>()
        .AddModule<UploadPackagesToLocalNuGetModule>();
}
else
{
    builder.AddModule<UploadPackagesToNugetModule>();
}

builder.AddModule<RunUnitTestsModule>();
builder.AddModule<NugetVersionGeneratorModule>();
builder.AddModule<PackProjectsModule>();
builder.AddModule<PackageFilesRemovalModule>();
builder.AddModule<PushVersionTagModule>();
builder.AddModule<PackagePathsModule>();

await builder.RunAsync();
