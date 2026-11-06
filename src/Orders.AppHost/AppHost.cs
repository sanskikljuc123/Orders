var builder = DistributedApplication.CreateBuilder(args);

var payment = builder.AddProject<Projects.FakePaymentService>("payment");

builder.AddProject<Projects.Orders_Api>("api")
    .WithEnvironment("Payment__BaseUrl", payment.GetEndpoint("http"))
    .WaitFor(payment)
    .WithExternalHttpEndpoints();

builder.Build().Run();