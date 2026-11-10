using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Runs Orders.Api in-process. The payment provider is always a FakeItEasy fake: it is
/// registered with builder.Services in Program.cs, so it is replaced with ConfigureTestServices.
/// Without settings the API uses its in-memory store and no auth.
/// </summary>
public class OrdersApiFactory : WebApplicationFactory<Program>
{
    public IPaymentClient Payment { get; } = A.Fake<IPaymentClient>();

    /// <summary>Every log event the API writes (all categories, as configured in Program.cs).</summary>
    public LogCapture Logs { get; } = new();

    /// <summary>Settings read by Program.cs before Build(), so they go in via UseSetting.</summary>
    protected virtual IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();

    /// <summary>Call at the start of every test: the fake lives on a shared fixture.</summary>
    public void ResetPayment(bool approve = true)
    {
        Fake.Reset(Payment);   // clears configured behaviour and recorded calls
        A.CallTo(() => Payment.ChargeAsync(A<decimal>._)).Returns(approve);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPaymentClient>();
            services.AddSingleton(Payment);
            services.AddSingleton<Serilog.Core.ILogEventSink>(Logs);
        });
    }
}
