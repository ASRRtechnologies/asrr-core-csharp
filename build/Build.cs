using System;
using System.Linq;
using System.Security.Cryptography.Pkcs;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    [Parameter] string NuGetApiUrl = "https://api.nuget.org/v3/index.json";
    [Parameter] string NuGetApiKey;
    
    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion] readonly GitVersion GitVersion;
    
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath NuGetDirectory => ArtifactsDirectory / "nuget";

    Target Print => _ => _
        .Executes(() =>
        {
            Log.Information("GitVersion = {Value}", GitVersion?.MajorMinorPatch ?? "Bloop");
        });

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            BuildProjectDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution.GetProject("ASRR.Core"))
            );
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution.GetProject("ASRR.Core"))
                .SetConfiguration(Configuration)
                .EnableNoRestore()
            );
        });

    Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetProject(Solution.GetProject("ASRR.Core"))
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetDescription("ASRR Core functionality library")
                .SetPackageTags("asrr core c# library")
                .SetVersion(GitVersion?.NuGetVersionV2 ?? "0.0.0")
                .SetNoDependencies(true)
                .SetOutputDirectory(NuGetDirectory)
            );
        });

    Target Push => _ => _
        .DependsOn(Pack)
        .Requires(() => NuGetApiUrl)
        .Requires(() => NuGetApiKey)
        .Requires(() => Configuration.Equals(Configuration.Release))
        .Executes(() =>
        {
            var nugetDirFiles = NuGetDirectory.GlobFiles("*.nupkg");
            Assert.NotEmpty(nugetDirFiles);
            nugetDirFiles
                .Where(x => !x.ToString().EndsWith("symbols.nupkg"))
                .ForEach(x =>
                {
                    DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource(NuGetApiUrl)
                        .SetApiKey(NuGetApiKey)
                    );
                });
        });
}