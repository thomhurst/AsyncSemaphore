$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('AsyncSemaphore-packages-' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $smokeRoot 'packages'
$version = '0.0.0-smoke.' + [guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $packages -Force | Out-Null
$escapedPackages = [Security.SecurityElement]::Escape($packages)
$nugetConfig = Join-Path $smokeRoot 'NuGet.Config'
@"
<configuration>
  <packageSources>
    <clear />
    <add key="smoke" value="$escapedPackages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfig

function Invoke-DotNet {
    param([string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}

foreach ($project in @('AsyncSemaphore/AsyncSemaphore.csproj', 'AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers/AsyncSemaphore.Analyzers.csproj')) {
    Invoke-DotNet -Arguments @('pack', (Join-Path $repoRoot $project), '-c', 'Release', '-o', $packages, "-p:PackageVersion=$version", '-p:GITHUB_ACTIONS=true')
}

foreach ($packageName in @('AsyncSemaphore', 'AsyncSemaphore.Analyzers')) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $packages "$packageName.$version.nupkg"))
    try {
        if ($archive.Entries.FullName -notcontains 'analyzers/dotnet/cs/AsyncSemaphore.Analyzers.dll') {
            throw "$packageName does not contain the analyzer in the compiler discovery directory."
        }
        if ($archive.Entries.FullName -contains 'lib/netstandard2.0/AsyncSemaphore.Analyzers.dll') {
            throw "$packageName exposes the analyzer as a runtime library."
        }
        $reader = [IO.StreamReader]::new($archive.GetEntry("$packageName.nuspec").Open())
        try { $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($manifest -match '<dependency id="Microsoft.CodeAnalysis') {
            throw "$packageName leaks compiler dependencies to consumers."
        }
    }
    finally { $archive.Dispose() }

    $consumer = Join-Path $smokeRoot $packageName
    New-Item -ItemType Directory -Path $consumer | Out-Null
    $runtimeReference = ''
    if ($packageName -eq 'AsyncSemaphore.Analyzers') {
        $library = Join-Path $repoRoot 'AsyncSemaphore/bin/Release/net10.0/AsyncSemaphore.dll'
        $escapedLibrary = [Security.SecurityElement]::Escape($library)
        $runtimeReference = "<Reference Include=`"AsyncSemaphore`"><HintPath>$escapedLibrary</HintPath></Reference>"
    }
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <WarningsAsErrors>SEM0001;SEM0002;SEM0003;SEM0004</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$packageName" Version="$version" />
    $runtimeReference
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj')
    $source = Join-Path $consumer 'Usage.cs'
    @'
public class Usage
{
    public async System.Threading.Tasks.Task Run(Semaphores.AsyncSemaphore semaphore)
    {
        using var handle = await semaphore.WaitAsync();
    }
}
'@ | Set-Content -LiteralPath $source
    Invoke-DotNet -Arguments @('restore', $consumer, '--configfile', $nugetConfig)
    Invoke-DotNet -Arguments @('build', $consumer, '-c', 'Release', '--no-restore')

    @'
public class Usage
{
    public void Run(Semaphores.AsyncSemaphore semaphore) { semaphore.WaitAsync(); }
}
'@ | Set-Content -LiteralPath $source
    $output = & dotnet build $consumer -c Release --no-restore 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'error SEM0001') {
        throw "The $packageName consumer did not enforce SEM0001:`n$output"
    }
    Write-Host "${packageName}: valid usage compiles and invalid usage fails with SEM0001."
}
Write-Host "Package smoke tests passed. Artifacts: $smokeRoot"
# The last consumer build intentionally failed; report the smoke test's success to CI.
exit 0
