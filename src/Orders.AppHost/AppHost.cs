var builder = DistributedApplication.CreateBuilder(args);

var keycloak = builder.AddKeycloak("keycloak", 8080);

var payment = builder.AddProject<Projects.FakePaymentService>("payment");

builder.AddProject<Projects.Orders_Api>("api")
    .WithEnvironment("Payment__BaseUrl", payment.GetEndpoint("http"))
    .WaitFor(payment)
    .WithExternalHttpEndpoints();

builder.Build().Run();