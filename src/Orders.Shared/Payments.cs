using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

// The payment provider contract, shared by Orders.Api (client) and FakePaymentService (fake), so
// the fake cannot drift from what the client sends and expects. The real provider's contract is an
// UNVERIFIED ASSUMPTION (see src/FakePaymentService/README.md).

public record ChargeRequest(decimal Amount);

public record ChargeResponse(bool Approved);

/// <summary>
/// reference: our order id, sent to the provider as its idempotency key, so charging the same
/// reference again returns the first result instead of charging twice.
/// FAKED for this example: provider-side idempotency is not part of any verified provider contract;
/// it is modelled on Stripe's public documentation (Idempotency-Key header). See
/// src/FakePaymentService/README.md (UNVERIFIED ASSUMPTION).
/// </summary>
public interface IPaymentClient { Task<bool> ChargeAsync(decimal amount, Guid reference); }

/// <summary>
/// Calls the payment provider. Anything other than a clear "approved" answer counts as not paid:
/// HTTP errors, unreadable bodies, connection failures and timeouts (HttpClient.Timeout) return
/// false instead of failing the order request with a 500.
/// UNVERIFIED ASSUMPTION: after a timeout the provider might still have charged; charging the same
/// reference again is safe only because of the (faked) provider-side idempotency.
/// </summary>
public class HttpPaymentClient(HttpClient http, ILogger<HttpPaymentClient> log) : IPaymentClient
{
    public async Task<bool> ChargeAsync(decimal amount, Guid reference)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/charge") { Content = JsonContent.Create(new ChargeRequest(amount)) };
            request.Headers.Add("Idempotency-Key", reference.ToString());   // FAKED provider feature, see IPaymentClient
            using var res = await http.SendAsync(request);
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
