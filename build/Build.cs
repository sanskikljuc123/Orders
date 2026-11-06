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

    AbsolutePath Solution => RootDirectory / "Orders.slnx";
    AbsolutePath ResultsDir => RootDirectory / "TestResults";

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s.SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target UnitTests => _ => _
        .DependsOn(Compile)
        .Executes(() => RunTests("Orders.UnitTests"));

    Target IntegrationTests => _ => _
        .DependsOn(Compile)
        .Executes(() => RunTests("Orders.IntegrationTests"));

    Target InstallBrowser => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var script = RootDirectory / "tests" / "Orders.E2E" / "bin" / Configuration / "net10.0" / "playwright.ps1";
            var deps = IsServerBuild ? " --with-deps" : "";
            var pwsh = ToolResolver.GetPathTool("pwsh");
            pwsh($"\"{script}\" install{deps} chromium");
        });

    Target E2ETests => _ => _
        .DependsOn(Compile, InstallBrowser)
        .Executes(() => RunTests("Orders.E2E"));

    Target Test => _ => _
        .DependsOn(UnitTests, IntegrationTests, E2ETests);

    void RunTests(string project)
    {
        DotNetTest(s => s
            .SetProjectFile(RootDirectory / "tests" / project)
            .SetConfiguration(Configuration)
            .EnableNoBuild()
            .SetLoggers("trx")
            .SetResultsDirectory(ResultsDir));
    }
}