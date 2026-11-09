using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// Only when generating deployment artifacts. Local runs and E2E tests are unaffected.
if (builder.ExecutionContext.IsPublishMode)
{
    builder.AddKubernetesEnvironment("k8s");
}

var keycloak = builder.AddKeycloak("keycloak", 8080)
    .WithRealmImport("Realms");

var payment = builder.AddProject<Projects.FakePaymentService>("payment");

builder.AddProject<Projects.Orders_Api>("api")
    .WithEnvironment("Payment__BaseUrl", payment.GetEndpoint("http"))
    .WithEnvironment("Auth__Authority",
        ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/orders"))
    .WaitFor(payment)
    .WaitFor(keycloak)
    .WithExternalHttpEndpoints();

builder.Build().Run();