using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

// The payment provider contract, shared by Orders.Api (client) and FakePaymentService (fake), so
// the fake cannot drift from what the client sends and expects. The real provider's contract is an
// UNVERIFIED ASSUMPTION (see src/FakePaymentService/README.md).

public record ChargeRequest(decimal Amount);

public record ChargeResponse(bool Approved);

public interface IPaymentClient { Task<bool> ChargeAsync(decimal amount); }

/// <summary>
/// Calls the payment provider. Anything other than a clear "approved" answer counts as not paid:
/// HTTP errors, unreadable bodies, connection failures and timeouts (HttpClient.Timeout) return
/// false instead of failing the order request with a 500.
/// UNVERIFIED ASSUMPTION: after a timeout the provider might still have charged; reconciling that
/// needs the real provider's API (for example an idempotency key or a status lookup).
/// </summary>
public class HttpPaymentClient(HttpClient http, ILogger<HttpPaymentClient> log) : IPaymentClient
{
    public async Task<bool> ChargeAsync(decimal amount)
    {
        try
        {
            using var res = await http.PostAsJsonAsync("/charge", new ChargeRequest(amount));
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Payment provider answered {StatusCode}; order not paid", (int)res.StatusCode);
                return false;
            }
            var body = await res.Content.ReadFromJsonAsync<ChargeResponse>();
            return body?.Approved ?? false;
        }
        catch (TaskCanceledException)
        {
            log.LogWarning("Payment provider timed out after {Timeout}; order not paid", http.Timeout);
            return false;
        }
        catch (HttpRequestException e)
        {
            log.LogWarning(e, "Payment provider unreachable; order not paid");
            return false;
        }
        catch (JsonException e)
        {
            log.LogWarning(e, "Payment provider sent an unreadable answer; order not paid");
            return false;
        }
    }
}
