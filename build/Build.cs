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

            WriteSummary(summary.ToString(), "coverage-summary.md");
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        });

    [Parameter("How many times Stability runs each test project")]
    readonly int Repeat = 3;

    [Parameter("Test projects for Stability (default: all four)")]
    readonly string[] StabilityProjects = { "Orders.UnitTests", "Orders.ContractTests", "Orders.IntegrationTests", "Orders.E2E" };

    // With Orders.E2E in StabilityProjects, run InstallBrowser first (ci-nightly.yml does).
    Target Stability => _ => _
        .DependsOn(Compile)
        .After(InstallBrowser)
        .Executes(() =>
        {
            // Runs each project several times, each time in a new random order (TestSupport), and
            // reports every test that failed in some runs: flaky or order-dependent. Nightly.
            var failures = new Dictionary<string, int>();
            foreach (var project in StabilityProjects)
                for (var run = 1; run <= Repeat; run++)
                {
                    var results = ResultsDir / "stability" / project / $"run-{run}";
                    results.CreateOrCleanDirectory();
                    try
                    {
                        DotNetTest(s => s
                            .SetProjectFile(RootDirectory / "tests" / project)
                            .SetConfiguration(Configuration)
                            .EnableNoBuild()
                            .SetLoggers("trx")
                            .SetResultsDirectory(results));
                    }
                    catch (ProcessException)
                    {
                        // Recorded below from the .trx; keep going so every run is counted.
                    }
                    var trx = ReadTrx(results);
                    Assert.True(trx.Total > 0, $"{project} run {run} produced no test results");
                    foreach (var name in trx.FailedTests)
                        failures[$"{project}: {name}"] = failures.GetValueOrDefault($"{project}: {name}") + 1;
                }

            var summary = new System.Text.StringBuilder($"## Stability: {Repeat} runs per project, random order each run\n\n");
            if (failures.Count == 0)
                summary.Append("No test failed in any run.\n");
            else
            {
                summary.Append("| Test | Failed runs | Verdict |\n|---|---|---|\n");
                foreach (var (test, count) in failures.OrderByDescending(f => f.Value))
                    summary.Append($"| `{test}` | {count}/{Repeat} | {(count == Repeat ? "broken" : "FLAKY or order-dependent")} |\n");
            }
            WriteSummary(summary.ToString(), "stability-summary.md");
            Assert.True(failures.Count == 0, $"{failures.Count} test(s) failed in at least one run, see TestResults/stability-summary.md");
        });

    [Parameter("kind cluster name for DeploySmoke")]
    readonly string SmokeCluster = "orders-smoke";

    Target DeploySmoke => _ => _
        .DependsOn(Images, Publish)
        .Executes(() =>
        {
            // Installs the real chart in a throwaway kind cluster (nightly CI; needs Docker, kind,
            // kubectl, helm). helm --wait includes the realm-import hook Job, so a failing Job or a
            // crashing pod fails the install. Then checks the realm and the API from inside.
            var kind = ToolResolver.GetPathTool("kind");
            var kubectl = ToolResolver.GetPathTool("kubectl");
            var helm = ToolResolver.GetPathTool("helm");
            var values = RootDirectory / "deploy" / $"values.{Environment}.yaml";
            kind($"create cluster --name {SmokeCluster} --wait 180s");
            try
            {
                foreach (var (_, image) in ContainerImages)
                    kind($"load docker-image {image}:{Version} --name {SmokeCluster}");

                helm($"install orders {ChartDir} -f {values} --set parameters.api.api_image=orders-api:{Version} --set parameters.payment.payment_image=orders-payment:{Version} --wait --timeout 10m");

                // Realm created by the hook Job, with the production settings.
                var realm = string.Join("\n", kubectl($"exec deploy/keycloak-deployment -- bash -c \"/opt/keycloak/bin/kcadm.sh config credentials --config /tmp/k --server http://localhost:8080 --realm master --user $KC_BOOTSTRAP_ADMIN_USERNAME --password $KC_BOOTSTRAP_ADMIN_PASSWORD && /opt/keycloak/bin/kcadm.sh get realms/orders --config /tmp/k --fields realm,sslRequired && /opt/keycloak/bin/kcadm.sh get users -r orders --config /tmp/k --fields username\"", logOutput: false)
                    .Where(o => o.Type == OutputType.Std).Select(o => o.Text));
                Assert.True(realm.Contains("\"sslRequired\" : \"all\""), $"realm 'orders' missing or not HTTPS-only: {realm}");
                Assert.True(!realm.Contains("\"username\""), "production realm must have no users");

                // API is up (through the Kubernetes API server's service proxy, no port-forward).
                var health = string.Join("", kubectl("get --raw /api/v1/namespaces/default/services/api-service:http/proxy/health")
                    .Where(o => o.Type == OutputType.Std).Select(o => o.Text));
                Assert.True(health.Trim() == "Healthy", $"API /health answered '{health}'");
                Serilog.Log.Information("Deployment smoke passed: chart installed, realm created (HTTPS-only, no users), API healthy");
            }
            finally
            {
                kind($"delete cluster --name {SmokeCluster}");
            }
        });

    Target Mutation => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            // Stryker.NET breaks the critical code on purpose and checks the tests notice. The
            // "break" threshold in each stryker/*.json fails the build when too many survive.
            // Run nightly (ci-nightly.yml); a few minutes, so not on every pull request.
            // Stryker has its own tool manifest (stryker/.config): restoring it together with
            // aspire.cli from the root manifest fails ("package contains aspire").
            DotNet("tool restore", RootDirectory / "stryker");
            foreach (var config in new[] { "api", "shared" })
                DotNet($"stryker --config-file {config}.json --output {ResultsDir / "mutation" / config}", RootDirectory / "stryker");
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
        try
        {
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
        finally
        {
            WriteSummary(TestSummary(project, results));   // also (especially) when tests failed
        }
    }

    record TrxResult(int Total, int Passed, int Failed, int Skipped, List<string> FailedTests);

    static TrxResult ReadTrx(AbsolutePath dir)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var (total, passed, failed, skipped, names) = (0, 0, 0, 0, new List<string>());
        foreach (var trx in dir.GlobFiles("*.trx"))
        {
            var doc = XDocument.Load(trx);
            var counters = doc.Descendants(ns + "Counters").Single();
            int Read(string name) => int.Parse(counters.Attribute(name)!.Value, CultureInfo.InvariantCulture);
            total += Read("total"); passed += Read("passed"); failed += Read("failed");
            skipped += Read("notExecuted");
            names.AddRange(doc.Descendants(ns + "UnitTestResult")
                .Where(r => r.Attribute("outcome")?.Value == "Failed")
                .Select(r => r.Attribute("testName")!.Value));
        }
        return new(total, passed, failed, skipped, names);
    }

    static string TestSummary(string project, AbsolutePath results)
    {
        var r = ReadTrx(results);
        var status = r.Total == 0 ? "no results" : r.Failed == 0 ? "passed" : "FAILED";
        var text = $"### {project}: {status}\n\n{r.Passed} passed, {r.Failed} failed, {r.Skipped} skipped (of {r.Total})\n";
        foreach (var name in r.FailedTests.Take(50))
            text += $"- FAILED: `{name}`\n";
        return text;
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
    void WriteSummary(string markdown, string file = null)   // build project has nullable disabled
    {
        if (file is not null) (ResultsDir / file).WriteAllText(markdown);
        var jobSummary = System.Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(jobSummary))
            System.IO.File.AppendAllText(jobSummary, markdown + "\n");
    }
}
