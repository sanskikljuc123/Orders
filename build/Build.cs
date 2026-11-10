using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Test);

    [Parameter("Build configuration")]
    readonly string Configuration = "Debug";

    // Coverage floors (branch coverage of Orders.Api, from coverlet). Set at the measured level so
    // coverage can't silently drop; raise them when real tests are added, never write tests to hit them.
    [Parameter("Minimum branch coverage % from unit tests")]
    readonly double UnitBranchFloor = 25;

    [Parameter("Minimum branch coverage % from integration tests")]
    readonly double IntegrationBranchFloor = 70;

    string Version = "0.0.0-local";

    AbsolutePath Solution => RootDirectory / "Orders.slnx";
    AbsolutePath ResultsDir => RootDirectory / "TestResults";
    AbsolutePath ChartDir => RootDirectory / "k8s-artifacts";   // generated, git-ignored (contains generated passwords)

    [Parameter("Push container images to ghcr.io (CI sets this on pushes to main only)")]
    readonly bool PushImages;

    // GitHub Container Registry: ghcr.io/<owner>/<image>, lowercase.
    [Parameter("Registry for --push-images (default ghcr.io; a local registry can be used to test the push)")]
    readonly string ImageRegistry = "ghcr.io";
    const string ImageOwner = "sanskikljuc123";
    static readonly (string Project, string Image)[] ContainerImages =
    {
        ("Orders.Api", "orders-api"),
        ("FakePaymentService", "orders-payment"),
    };

    [Parameter("Environment for deploy/values.<env>.yaml")]
    readonly string Environment = "dev";

    Target ComputeVersion => _ => _
        .Executes(() =>
        {
            DotNet("tool restore", RootDirectory);
            var output = DotNet("tool run dotnet-gitversion /showvariable SemVer", RootDirectory);
            Version = string.Join("", output
                .Where(o => o.Type == OutputType.Std)
                .Select(o => o.Text)).Trim();
            Serilog.Log.Information("Version: {Version}", Version);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s.SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore, ComputeVersion)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetVersion(Version)
                .EnableNoRestore());
        });

    Target UnitTests => _ => _
        .DependsOn(Compile)
        .Executes(() => RunTests("Orders.UnitTests", coverage: true));

    Target ContractTests => _ => _
        .DependsOn(Compile)
        .Executes(() => RunTests("Orders.ContractTests", coverage: true));

    Target IntegrationTests => _ => _
        .DependsOn(Compile)
        .Executes(() => RunTests("Orders.IntegrationTests", coverage: true));

    Target InstallBrowser => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            // Browser and ffmpeg (needed for video) matching the Microsoft.Playwright package.
            var script = RootDirectory / "tests" / "Orders.E2E" / "bin" / Configuration / "net10.0" / "playwright.ps1";
            var pwsh = ToolResolver.GetPathTool("pwsh");
            // Don't interpolate a string that contains spaces: NUKE quotes it as one argument
            // (" --with-deps" became `install" --with-deps"` and Playwright rejected it).
            if (IsServerBuild)
                pwsh($"{script} install --with-deps chromium ffmpeg");
            else
                pwsh($"{script} install chromium ffmpeg");
        });

    Target E2ETests => _ => _
        .DependsOn(Compile, InstallBrowser)
        .Executes(() => RunTests("Orders.E2E", coverage: false));

    Target CoverageGate => _ => _
        .After(UnitTests, IntegrationTests)
        .Executes(() =>
        {
            CheckBranchCoverage("Orders.UnitTests", UnitBranchFloor);
            CheckBranchCoverage("Orders.IntegrationTests", IntegrationBranchFloor);
        });

    Target Publish => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            ChartDir.CreateOrCleanDirectory();
            DotNet($"aspire publish -o {ChartDir} --non-interactive", RootDirectory);

            // Aspire's Kubernetes publisher cannot carry the realm import, so add it to the
            // generated chart: a post-install/upgrade Job that creates the production realm.
            var extras = RootDirectory / "deploy" / "chart-extras";
            foreach (var file in (extras / "templates").GlobFiles("*.yaml"))
                file.Copy(ChartDir / "templates" / file.Name, ExistsPolicy.FileOverwrite);
            (extras / "files" / "import-realm.sh").Copy(ChartDir / "files" / "import-realm.sh", ExistsPolicy.FileOverwrite);
            (RootDirectory / "deploy" / "realms" / "orders-realm.prod.json")
                .Copy(ChartDir / "files" / "orders-realm.prod.json", ExistsPolicy.FileOverwrite);

            // The import Job must use the same Keycloak image as the generated deployment.
            var keycloakImage = System.Text.RegularExpressions.Regex.Match(
                (ChartDir / "templates" / "keycloak" / "deployment.yaml").ReadAllText(),
                "image: \"([^\"]+)\"").Groups[1].Value;
            Assert.True((ChartDir / "templates" / "keycloak-realm-import.yaml").ReadAllText().Contains($"image: \"{keycloakImage}\""),
                $"keycloak-realm-import.yaml must use the chart's Keycloak image {keycloakImage}");
        });

    Target Helm => _ => _
        .DependsOn(Publish)
        .Executes(() =>
        {
            // Phase 7 gate: the generated chart lints and renders with the environment's values.
            var values = RootDirectory / "deploy" / $"values.{Environment}.yaml";
            Assert.FileExists(values);
            var helm = ToolResolver.GetPathTool("helm");
            helm($"lint {ChartDir} -f {values}");
            helm($"template orders {ChartDir} -f {values}", logOutput: false);
        });

    Target Images => _ => _
        .DependsOn(ComputeVersion)
        .Executes(() =>
        {
            // .NET SDK container support (no Dockerfile). Without --push-images the images go to the
            // local Docker daemon, which proves they build; with it they are pushed to ghcr.io.
            // Registry login comes from DOTNET_CONTAINER_REGISTRY_UNAME / _PWORD (set by CI).
            foreach (var (project, image) in ContainerImages)
            {
                var repository = PushImages ? $"{ImageOwner}/{image}" : image;
                var projectDir = RootDirectory / "src" / project;
                // Never interpolate a string that contains spaces: NUKE quotes it as ONE argument
                // (" -p:ContainerRegistry=..." was glued onto the image tag and broke the push).
                if (PushImages)
                    DotNet($"publish {projectDir} -c Release -t:PublishContainer -p:ContainerRepository={repository} -p:ContainerImageTag={Version} -p:ContainerRegistry={ImageRegistry}");
                else
                    DotNet($"publish {projectDir} -c Release -t:PublishContainer -p:ContainerRepository={repository} -p:ContainerImageTag={Version}");
                Serilog.Log.Information("Image: {Image}",
                    PushImages ? $"{ImageRegistry}/{repository}:{Version}" : $"{repository}:{Version} (local)");
            }
        });

    Target Test => _ => _
        .DependsOn(UnitTests, ContractTests, IntegrationTests, CoverageGate, E2ETests);

    void RunTests(string project, bool coverage)
    {
        var results = ResultsDir / project;
        results.CreateOrCleanDirectory();
        DotNetTest(s => s
            .SetProjectFile(RootDirectory / "tests" / project)
            .SetConfiguration(Configuration)
            .EnableNoBuild()
            .SetLoggers("trx")
            .SetResultsDirectory(results)
            .When(_ => coverage, x => x.SetDataCollector("XPlat Code Coverage")));
    }

    void CheckBranchCoverage(string project, double floor)
    {
        var report = (ResultsDir / project).GlobFiles("**/coverage.cobertura.xml").FirstOrDefault()   // coverlet also copies it next to the .trx
            ?? throw new Exception($"No coverage report for {project}. Run its test target first.");
        var rate = double.Parse(XDocument.Load(report).Root!.Attribute("branch-rate")!.Value,
            CultureInfo.InvariantCulture) * 100;
        Serilog.Log.Information("{Project}: branch coverage {Rate:F1}% (floor {Floor}%)", project, rate, floor);
        Assert.True(rate >= floor, $"{project}: branch coverage {rate:F1}% is below the floor of {floor}%");
    }
}
