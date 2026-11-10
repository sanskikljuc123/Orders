using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

/// <summary>
/// The API contract (OpenAPI document) is committed in docs/api/openapi.v1.json. Any change to
/// routes, status codes or fields shows up here and in the pull request diff, so it is a decision,
/// not an accident. Real Keycloak + PostgreSQL (see StackFixture). Needs Docker.
/// </summary>
[Collection("Stack")]
public class OpenApiContractTests(StackFixture fixture)
{
    private const string UpdateVariable = "UPDATE_OPENAPI_SNAPSHOT";

    private static string SnapshotPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orders.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root (Orders.slnx) not found"),
            "docs", "api", "openapi.v1.json");
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    [Fact]
    public async Task Api_contract_matches_the_committed_openapi_document()
    {
        var res = await fixture.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var actual = Normalize(await res.Content.ReadAsStringAsync());
        var path = SnapshotPath();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"{path} is missing. Create it with {UpdateVariable}=1.");
        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.True(expected == actual,
            $"The API contract changed. Review the difference; if it is intended, run the test with {UpdateVariable}=1 "
            + $"and commit docs/api/openapi.v1.json.\nCurrent document:\n{actual}");
    }

    [Fact]
    public async Task Contract_documents_what_callers_depend_on()
    {
        // A few anchors, so a snapshot accepted by mistake cannot drop them silently.
        var doc = JsonNode.Parse(await fixture.CreateClient().GetStringAsync("/openapi/v1.json"))!;
        var post = doc["paths"]!["/orders"]!["post"]!["responses"]!;

        Assert.NotNull(post["201"]);
        Assert.NotNull(post["400"]);
        Assert.NotNull(post["401"]);
        Assert.NotNull(post["422"]);
        Assert.NotNull(post["202"]);   // Pending: retry with the same key settles it
        Assert.NotNull(post["503"]);   // not placed, nothing charged
        var order = doc["components"]!["schemas"]!["Order"]!["properties"]!.AsObject().Select(p => p.Key).Order();
        Assert.Equal(["createdAt", "id", "productName", "quantity", "status"], order);
    }

    [Fact]
    public async Task Production_does_not_publish_the_document()
    {
        using var api = new ApiInEnvironment("Production", fixture.DbConnectionString);

        var res = await api.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
