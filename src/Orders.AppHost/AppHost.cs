var builder = DistributedApplication.CreateBuilder(args);

// Only when generating deployment artifacts. Local runs and E2E tests are unaffected.
if (builder.ExecutionContext.IsPublishMode)
{
    builder.AddKubernetesEnvironment("k8s");
}

var keycloak = builder.AddKeycloak("keycloak", 8080);

var payment = builder.AddProject<Projects.FakePaymentService>("payment");

builder.AddProject<Projects.Orders_Api>("api")
    .WithEnvironment("Payment__BaseUrl", payment.GetEndpoint("http"))
    .WaitFor(payment)
    .WithExternalHttpEndpoints();

builder.Build().Run();