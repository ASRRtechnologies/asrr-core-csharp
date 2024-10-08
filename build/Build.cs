using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.ChangeLog;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using Octokit;
using Octokit.Internal;
using Serilog;
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

    [Nuke.Common.Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    [Nuke.Common.Parameter] string NuGetFeed = "https://api.nuget.org/v3/index.json";
    [Nuke.Common.Parameter("NuGet API Key"), Secret] string NuGetApiKey;
    [Nuke.Common.Parameter("Artifacts Type")] readonly string ArtifactsType;
    [Nuke.Common.Parameter("Excluded Artifacts Type")] readonly string ExcludedArtifactsType;
    [Nuke.Common.Parameter("Copyright Details")] readonly string Copyright;
    
    [Solution(GenerateProjects = true)] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion(NoFetch = true)] readonly GitVersion GitVersion;
    
    static GitHubActions GitHubActions => GitHubActions.Instance;
    static AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    const string PackageContentType = "application/octet-stream";
    static string ChangeLogFile => RootDirectory / "CHANGELOG.md";

    static string GitHubNuGetFeed => GitHubActions != null
        ? $"https://nuget.pkg.github.com/{GitHubActions.RepositoryOwner}/index.json"
        : null;

    Target Print => _ => _
        .Before(Clean)
        .Executes(() =>
        {
            Log.Information("GitVersion = {Value}", GitVersion.MajorMinorPatch ?? "Not available");
            Log.Information("Current Config = {Value}", Configuration.ToString());
            Log.Information("GitHub NuGet feed = {Value}", GitHubNuGetFeed);
            Log.Information("GitVer = {Value}", GitVersion);
            Log.Information("NuGet feed = {Value}", NuGetFeed);
            Log.Information("GitHub Repo = {Value}", GitRepository);
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
                .SetFramework("netstandard2.0")
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
                .SetVersion(GitVersion.MajorMinorPatch)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
            );
        });
    
    Target PublishToGithub => _ => _
        .Description($"Publish to Github for Development builds.")
        .Triggers(CreateRelease)
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
        .Triggers(CreateRelease)
        .Requires(() => Configuration.Equals(Configuration.Release))
        .OnlyWhenStatic(() => GitRepository.IsOnMainOrMasterBranch())
        .Executes(() =>
        {
            Log.Information($"Pushing package to NuGet feed...");
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
    
    Target CreateRelease => _ => _
        .Description($"Creating release for the publishable version.")
        .Requires(() => Configuration.Equals(Configuration.Release))
        .OnlyWhenStatic(() => GitRepository.IsOnMainOrMasterBranch() || GitRepository.IsOnReleaseBranch())
        .Executes(async () =>
        {
            var credentials = new Credentials(GitHubActions.Token);
            GitHubTasks.GitHubClient = new GitHubClient(new ProductHeaderValue(nameof(NukeBuild)),
                new InMemoryCredentialStore(credentials));

            var (owner, name) = (GitRepository.GetGitHubOwner(), GitRepository.GetGitHubName());

            var releaseTag = GitVersion.NuGetVersionV2;
            var changeLogSectionEntries = ChangelogTasks.ExtractChangelogSectionNotes(ChangeLogFile);
            var latestChangeLog = changeLogSectionEntries
                .Aggregate((c, n) => c + Environment.NewLine + n);

            var newRelease = new NewRelease(releaseTag)
            {
                TargetCommitish = GitVersion.Sha,
                Draft = true,
                Name = $"v{releaseTag}",
                Prerelease = !string.IsNullOrEmpty(GitVersion.PreReleaseTag),
                Body = latestChangeLog
            };

            var createdRelease = await GitHubTasks
                .GitHubClient
                .Repository
                .Release.Create(owner, name, newRelease);

            ArtifactsDirectory.GlobFiles(ArtifactsType)
                .Where(x => !x.ToString().EndsWith(ExcludedArtifactsType))
                .ForEach(async x => await UploadReleaseAssetToGithub(createdRelease, x));

            await GitHubTasks
                .GitHubClient
                .Repository
                .Release
                .Edit(owner, name, createdRelease.Id, new ReleaseUpdate { Draft = false });
        });


    private static async Task UploadReleaseAssetToGithub(Release release, string asset)
    {
        await using var artifactStream = File.OpenRead(asset);
        var fileName = Path.GetFileName(asset);
        var assetUpload = new ReleaseAssetUpload
        {
            FileName = fileName,
            ContentType = PackageContentType,
            RawData = artifactStream,
        };
        await GitHubTasks.GitHubClient.Repository.Release.UploadAsset(release, assetUpload);
    }
}