using System;
using System.Collections.Generic;
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

    // Coverage floors on the MERGED unit + contract + integration report (generated migrations
    // excluded, see coverlet.runsettings). Set just below the measured level so coverage can't
    // silently drop; raise them with real tests, never write tests just to hit them.
    [Parameter("Minimum merged branch coverage % for all production code")]
    readonly double TotalBranchFloor = 90;

    [Parameter("Minimum merged branch coverage % for each critical class")]
    readonly double CriticalBranchFloor = 80;

    // Code where a regression costs money, data or security. Each must stay above CriticalBranchFloor.
    static readonly string[] CriticalClasses =
    {
        "OrderService", "IdempotencyLocks", "CreateOrderValidator", "HttpPaymentClient",
        "SessionStore", "EfOrderRepository", "UserIds",
    };

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
        .After(UnitTests, ContractTests, IntegrationTests)
        .Executes(() =>
        {
            var coverage = MergedCoverage("Orders.UnitTests", "Orders.ContractTests", "Orders.IntegrationTests");
            var failures = new List<string>();
            var summary = new System.Text.StringBuilder("## Coverage (merged unit + contract + integration)\n\n| Code | Lines | Branches | Floor (lines and branches) |\n|---|---|---|---|\n");

            void Check(string name, CoverageCount c, double floor)
            {
                summary.Append($"| {name} | {c.LinePercent:F1}% | {c.BranchPercent:F1}% ({c.BranchesCovered}/{c.BranchesTotal}) | {floor}% |\n");
                Serilog.Log.Information("{Name}: lines {Lines:F1}%, branches {Branches:F1}% (floor {Floor}%)", name, c.LinePercent, c.BranchPercent, floor);
                if (c.BranchPercent < floor) failures.Add($"{name}: branch coverage {c.BranchPercent:F1}% is below the floor of {floor}%");
                // Classes without branches (e.g. the validator) would pass any branch floor; lines count too.
                if (c.LinePercent < floor) failures.Add($"{name}: line coverage {c.LinePercent:F1}% is below the floor of {floor}%");
            }

            Check("All production code", coverage.Total, TotalBranchFloor);
            foreach (var cls in CriticalClasses)
            {
                Assert.True(coverage.ByClass.ContainsKey(cls), $"Critical class {cls} has no coverage data (renamed? update CriticalClasses)");
                Check(cls, coverage.ByClass[cls], CriticalBranchFloor);
            }

            WriteSummary(summary.ToString());
            Assert.True(failures.Count == 0, string.Join("; ", failures));
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
            .When(_ => coverage, x => x
                .SetDataCollector("XPlat Code Coverage")
                .SetSettingsFile(RootDirectory / "coverlet.runsettings")));
    }

    record CoverageCount(int LinesCovered, int LinesTotal, int BranchesCovered, int BranchesTotal)
    {
        public double LinePercent => LinesTotal == 0 ? 100 : 100.0 * LinesCovered / LinesTotal;
        public double BranchPercent => BranchesTotal == 0 ? 100 : 100.0 * BranchesCovered / BranchesTotal;
    }

    record MergedReport(CoverageCount Total, Dictionary<string, CoverageCount> ByClass);

    /// <summary>
    /// Merges coverlet reports line by line: a line or branch counts as covered if any test layer
    /// covers it. Classes are grouped by their top-level name (lambdas/nested classes included).
    /// </summary>
    MergedReport MergedCoverage(params string[] projects)
    {
        var lines = new Dictionary<(string File, int Line), (string Class, bool Hit, int BranchesCovered, int BranchesTotal)>();
        foreach (var project in projects)
        {
            var report = (ResultsDir / project).GlobFiles("**/coverage.cobertura.xml").FirstOrDefault()   // coverlet also copies it next to the .trx
                ?? throw new Exception($"No coverage report for {project}. Run its test target first.");
            foreach (var cls in XDocument.Load(report).Descendants("class"))
            {
                var name = cls.Attribute("name")!.Value.Split('/')[0];
                var file = cls.Attribute("filename")!.Value;
                foreach (var line in cls.Element("lines")!.Elements("line"))
                {
                    var key = (file, int.Parse(line.Attribute("number")!.Value, CultureInfo.InvariantCulture));
                    var hit = int.Parse(line.Attribute("hits")!.Value, CultureInfo.InvariantCulture) > 0;
                    var (covered, total) = (0, 0);
                    var condition = line.Attribute("condition-coverage")?.Value;   // e.g. "50% (1/2)"
                    if (condition is not null)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(condition, @"\((\d+)/(\d+)\)");
                        (covered, total) = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                    }
                    lines[key] = lines.TryGetValue(key, out var seen)
                        ? (seen.Class, seen.Hit || hit, Math.Max(seen.BranchesCovered, covered), Math.Max(seen.BranchesTotal, total))
                        : (name, hit, covered, total);
                }
            }
        }

        static CoverageCount Count(IEnumerable<(string Class, bool Hit, int BranchesCovered, int BranchesTotal)> ls)
        {
            var list = ls.ToList();
            return new(list.Count(l => l.Hit), list.Count, list.Sum(l => l.BranchesCovered), list.Sum(l => l.BranchesTotal));
        }

        return new(Count(lines.Values),
            lines.Values.GroupBy(l => l.Class).ToDictionary(g => g.Key, g => Count(g)));
    }

    /// <summary>Saves a Markdown summary and, on GitHub Actions, adds it to the job summary page.</summary>
    void WriteSummary(string markdown)
    {
        (ResultsDir / "coverage-summary.md").WriteAllText(markdown);
        var jobSummary = System.Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(jobSummary))
            System.IO.File.AppendAllText(jobSummary, markdown + "\n");
    }
}
