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
else
{
    // Kubernetes: Keycloak's production mode ("start") refuses to start without TLS or a hostname
    // ("Key material not provided to setup HTTPS", exit 2). It serves plain HTTP inside the cluster
    // behind an ingress that terminates HTTPS, and builds its URLs from the public hostname.
    var keycloakHostname = builder.AddParameter("keycloak-hostname");
    keycloak
        .WithEnvironment("KC_HTTP_ENABLED", "true")
        .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
        .WithEnvironment("KC_HOSTNAME", keycloakHostname);
}

var postgres = builder.AddPostgres("postgres");
var ordersDb = postgres.AddDatabase("ordersdb");

var payment = builder.AddProject<Projects.FakePaymentService>("payment")
    .WithHttpHealthCheck("/", endpointName: "http");

builder.AddProject<Projects.Orders_Api>("api")
    .WithReference(ordersDb)
    .WithEnvironment("Payment__BaseUrl", payment.GetEndpoint("http"))
    .WithEnvironment("Auth__Authority",
        ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/orders"))
    .WaitFor(ordersDb)
    .WaitFor(payment)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health", endpointName: "http")
    .WithExternalHttpEndpoints();

builder.Build().Run();