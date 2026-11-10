using System.Collections.Concurrent;

var app = WebApplication.CreateBuilder(args).Build();
var mode = "approve"; // approve | decline | error

// FAKED for this example, UNVERIFIED ASSUMPTION (see README): provider-side idempotency modelled on
// Stripe's public documentation. The first answer for an Idempotency-Key is stored and returned
// again for the same key, without charging again. A provider error (mode "error") is not stored,
// as if the provider failed before processing, so a retry with the same key is processed normally.
var answers = new ConcurrentDictionary<string, ChargeResponse>();
var charges = 0;   // charges actually processed (an approval or a decline), for tests

app.MapPost("/charge", (ChargeRequest req, HttpRequest http) =>
{
    if (mode == "error")
        return Results.StatusCode(500);

    string? key = http.Headers["Idempotency-Key"];
    if (key is not null && answers.TryGetValue(key, out var first))
        return Results.Ok(first);   // replay: same answer, no new charge

    var answer = new ChargeResponse(mode != "decline");
    if (key is null || answers.TryAdd(key, answer))
    {
        Interlocked.Increment(ref charges);
        return Results.Ok(answer);
    }
    return Results.Ok(answers[key]);   // lost a race with the same key: the winner's answer
});

app.MapPost("/__admin/mode", (ModeRequest r) => { mode = r.Mode; return Results.Ok(); });
app.MapPost("/__admin/reset", () =>   // called after every E2E scenario
{
    mode = "approve";
    answers.Clear();
    Interlocked.Exchange(ref charges, 0);
    return Results.Ok();
});
app.MapGet("/__admin/charges", () => Results.Ok(new { count = Volatile.Read(ref charges) }));
app.MapGet("/", () => $"FakePaymentService, mode: {mode}");
app.Run();

// ChargeRequest / ChargeResponse come from Orders.Shared (the same types the API's client uses).
record ModeRequest(string Mode);

// Lets WebApplicationFactory<Program> in tests/Orders.ContractTests start the fake in-process.
public partial class Program { }
