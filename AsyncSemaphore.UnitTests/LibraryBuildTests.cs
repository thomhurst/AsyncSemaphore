using System.Reflection;
using System.Runtime.Versioning;

namespace AsyncSemaphore.UnitTests;

/// <summary>
/// The library compiles different code per target framework, so the suite runs once per build. This
/// fails the run if a project ends up testing a build other than the one it is there for.
/// </summary>
public class LibraryBuildTests
{
    [Test]
    public async Task Suite_Runs_Against_The_Library_Build_It_Is_Meant_To_Cover()
    {
        var libraryFramework = typeof(Semaphores.AsyncSemaphore).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()!
            .FrameworkName;

#if LIBRARY_NETSTANDARD
        await Assert.That(libraryFramework).IsEqualTo(".NETStandard,Version=v2.0");
#else
        var testFramework = typeof(LibraryBuildTests).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()!
            .FrameworkName;

        await Assert.That(libraryFramework).IsEqualTo(testFramework);
#endif
    }
}
