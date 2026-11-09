using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// Only when generating deployment artifacts. Local runs and E2E tests are unaffected.
if (builder.ExecutionContext.IsPublishMode)
{
    builder.AddKubernetesEnvironment("k8s");
}

var keycloak = builder.AddKeycloak("keycloak", 8080);

// The test realm contains users with known passwords (ana/bob), sslRequired=none and
// redirectUris="*". It is for local runs and tests only and must never reach a deployment.
// A deployed environment needs its own realm, provisioned separately.
if (!builder.ExecutionContext.IsPublishMode)
{
    keycloak.WithRealmImport("Realms");
}

var postgres = builder.AddPostgres("postgres");
var ordersDb = postgres.AddDatabase("ordersdb");

var payment = builder.AddProject<Projects.FakePaymentService>("payment");

builder.AddProject<Projects.Orders_Api>("api")
    .WithReference(ordersDb)
    .WithEnvironment("Payment__BaseUrl", payment.GetEndpoint("http"))
    .WithEnvironment("Auth__Authority",
        ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/orders"))
    .WaitFor(ordersDb)
    .WaitFor(payment)
    .WaitFor(keycloak)
    .WithExternalHttpEndpoints();

builder.Build().Run();