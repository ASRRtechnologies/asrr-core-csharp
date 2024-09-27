using System;
using System.Linq;
using System.Security.Cryptography.Pkcs;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
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

[GitHubActions(
    name: "continuous",
    image: GitHubActionsImage.WindowsLatest,
    AutoGenerate = true,
    FetchDepth = 0,
    OnPushBranches = ["main", "dev", "releases/**"],
    OnPullRequestBranches = ["releases/**"],
    InvokedTargets = [ nameof(Pack) ],
    EnableGitHubToken = true,
    ImportSecrets = [nameof(NuGetApiKey)]
)]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Pack);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    [Parameter] string NuGetFeed = "https://api.nuget.org/v3/index.json";
    [Parameter("NuGet API Key"), Secret] string NuGetApiKey;
    [Parameter("Artifacts Type")] readonly string ArtifactsType;
    [Parameter("Excluded Artifacts Type")] readonly string ExcludedArtifactsType;
    
    [Solution(GenerateProjects = true)] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion(NoFetch = true)] readonly GitVersion GitVersion;
    
    static GitHubActions GitHubActions => GitHubActions.Instance;
    static AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    string GitHubNuGetFeed => GitHubActions != null
        ? $"https://nuget.pkg.github.com/{GitHubActions.RepositoryOwner}/index.json"
        : null;

    Target Print => _ => _
        .Executes(() =>
        {
            Log.Information("GitVersion = {Value}", GitVersion.MajorMinorPatch ?? "Not available");
            Log.Information("Current Config = {Value}", Configuration.ToString());
            Log.Information("GitHub NuGet feed = {Value}", GitHubNuGetFeed);
            Log.Information("GitVer = {Value}", GitVersion);
        });

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            DotNetClean(c => c.SetProject(Solution.ASRR_Core));
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution.ASRR_Core)
            );
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution.ASRR_Core)
                .SetConfiguration(Configuration)
                .EnableNoRestore()
            );
        });

    Target Pack => _ => _
        .Requires(() => Configuration.Equals(Configuration.Release))
        .Produces(ArtifactsDirectory / ArtifactsType)
        .DependsOn(Compile)
        .Triggers(PublishToGithub, PublishToNuGet)
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetProject(Solution.ASRR_Core)
                .SetConfiguration(Configuration)
                .SetOutputDirectory(ArtifactsDirectory)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetVersion(GitVersion.NuGetVersionV2)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
            );
        });
    
    Target PublishToGithub => _ => _
        .Description($"Publish to Github for Development builds.")
        .Requires(() => Configuration.Equals(Configuration.Release))
        .OnlyWhenStatic(() => GitRepository.IsOnDevelopBranch() || GitHubActions.IsPullRequest)
        .Executes(() =>
        {
            ArtifactsDirectory.GlobFiles(ArtifactsType)
                .Where(x => !x.ToString().EndsWith(ExcludedArtifactsType))
                .ForEach(x =>
                {
                    DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource(GitHubNuGetFeed)
                        .SetApiKey(GitHubActions.Token)
                        .EnableSkipDuplicate()
                    );
                });
        });

    Target PublishToNuGet => _ => _
        .Description($"Publishing to NuGet with the version.")
        .Requires(() => Configuration.Equals(Configuration.Release))
        .OnlyWhenStatic(() => GitRepository.IsOnMainOrMasterBranch())
        .Executes(() =>
        {
            ArtifactsDirectory.GlobFiles(ArtifactsType)
                .Where(x => !x.ToString().EndsWith(ExcludedArtifactsType))
                .ForEach(x =>
                {
                    DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource(NuGetFeed)
                        .SetApiKey(NuGetApiKey)
                        .EnableSkipDuplicate()
                    );
                });
        });
}