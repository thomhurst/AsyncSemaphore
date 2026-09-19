$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE"
    }
}

$repositoryRoot = Split-Path $PSScriptRoot -Parent
# Unique versions and directories prevent an earlier cached package from masking a regression.
$packageVersion = "99.0.0-compatibility.$([Guid]::NewGuid().ToString('N'))"
# The release pipeline scans the repository for projects/packages, so keep fixtures outside it.
$artifactRoot = Join-Path ([System.IO.Path]::GetTempPath()) "AsyncSemaphore-package-compatibility/$packageVersion"
$packageDirectory = Join-Path $artifactRoot 'packages'
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    Invoke-DotNet pack AsyncSemaphore/AsyncSemaphore.csproj -c Release "-p:PackageVersion=$packageVersion" -o $packageDirectory

    $archive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $packageDirectory "AsyncSemaphore.$packageVersion.nupkg"))
    try {
        $reader = [System.IO.StreamReader]::new($archive.GetEntry('AsyncSemaphore.nuspec').Open())
        try {
            [xml] $manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $groups = $manifest.package.metadata.dependencies.group
    foreach ($framework in @('.NETStandard2.0', 'net8.0', 'net9.0', 'net10.0')) {
        $group = $groups | Where-Object targetFramework -EQ $framework
        if (-not $group) {
            throw "Missing dependency group for $framework"
        }
        if ($framework -eq '.NETStandard2.0') {
            foreach ($dependency in @{ 'Microsoft.Bcl.AsyncInterfaces' = '6.0.0'; 'System.Threading.Tasks.Extensions' = '4.5.4' }.GetEnumerator()) {
                $actual = $group.dependency | Where-Object id -EQ $dependency.Key
                if ($actual.version -ne $dependency.Value) {
                    throw "Unexpected dependency floor for $($dependency.Key): $($actual.version)"
                }
            }
        }
        elseif ($group.dependency) {
            throw "Modern target $framework should not have package dependencies"
        }
    }

    foreach ($interfacesVersion in @('6.0.0', '8.0.0')) {
        $consumerDirectory = Join-Path $artifactRoot "consumer-$interfacesVersion"
        New-Item -ItemType Directory -Path $consumerDirectory -Force | Out-Null
        # Isolate consumers from repository build settings and feed mappings.
        '<Project />' | Set-Content (Join-Path $consumerDirectory 'Directory.Build.props')
        @'
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="../packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping><clear /></packageSourceMapping>
</configuration>
'@ | Set-Content (Join-Path $consumerDirectory 'NuGet.Config')
        @"
<Project>
  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="AsyncSemaphore" Version="$packageVersion" />
    <PackageVersion Include="Microsoft.Bcl.AsyncInterfaces" Version="$interfacesVersion" />
    <PackageVersion Include="System.Threading.Tasks.Extensions" Version="4.5.4" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $consumerDirectory 'Directory.Packages.props')
        @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>netstandard2.0;net8.0;net9.0;net10.0</TargetFrameworks>
    <LangVersion>latest</LangVersion>
    <WarningsAsErrors>NU1605</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="AsyncSemaphore" />
    <PackageReference Include="Microsoft.Bcl.AsyncInterfaces" />
    <PackageReference Include="System.Threading.Tasks.Extensions" />
  </ItemGroup>
</Project>
'@ | Set-Content (Join-Path $consumerDirectory 'Consumer.csproj')
        @'
using System.Threading.Tasks;
using Semaphores;

public static class Consumer
{
    public static async Task AcquireAsync()
    {
        var semaphore = new AsyncSemaphore(1);
        using (await semaphore.WaitAsync()) { }
        using (semaphore.Wait()) { }

        var signal = new UnpairedAsyncSemaphore(0);
        var pending = signal.WaitAsync();
        signal.Release();
        await pending;
    }
}
'@ | Set-Content (Join-Path $consumerDirectory 'Consumer.cs')
        Invoke-DotNet build (Join-Path $consumerDirectory 'Consumer.csproj') -c Release
    }
    Write-Host "Package compatibility checks passed. Artifacts: $artifactRoot"
}
finally {
    Pop-Location
}
