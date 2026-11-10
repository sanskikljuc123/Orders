using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The payment client as Program.cs wires it (not replaced by a fake): Payment:TimeoutSeconds must
/// reach the HttpClient. Without it the default 100 s would hold every order request open while the
/// provider hangs. No Docker needed (Development, in-memory store).
/// </summary>
public sealed class PaymentWiringTests : IDisposable
{
    // Accepts connections and never answers: a provider that hangs.
    private readonly TcpListener _silentProvider = new(IPAddress.Loopback, 0);

    public PaymentWiringTests() => _silentProvider.Start();

    public void Dispose() => _silentProvider.Stop();

    private sealed class ApiWithRealPaymentClient(int port) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Payment:BaseUrl", $"http://127.0.0.1:{port}");
            builder.UseSetting("Payment:TimeoutSeconds", "1");
        }
    }

    [Fact]
    public async Task Configured_timeout_ends_a_hanging_charge_as_not_paid()
    {
        using var api = new ApiWithRealPaymentClient(((IPEndPoint)_silentProvider.LocalEndpoint).Port);
        using var scope = api.Services.CreateScope();
        var payment = scope.ServiceProvider.GetRequiredService<IPaymentClient>();
        Assert.IsType<HttpPaymentClient>(payment);   // the real client, not the AlwaysApproved fallback

        var clock = Stopwatch.StartNew();
        var charge = payment.ChargeAsync(10m, Guid.NewGuid());
        // Generous upper bound for a 1 s timeout; with the 100 s default this gives up first.
        var finished = await Task.WhenAny(charge, Task.Delay(TimeSpan.FromSeconds(10))) == charge;

        Assert.True(finished, $"the charge was still waiting after {clock.Elapsed.TotalSeconds:F0} s: Payment:TimeoutSeconds is not applied");
        Assert.False(await charge);   // a timeout means not paid
    }
}
