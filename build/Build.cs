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

    Target Test => _ => _
        .DependsOn(UnitTests, IntegrationTests, CoverageGate, E2ETests);

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
