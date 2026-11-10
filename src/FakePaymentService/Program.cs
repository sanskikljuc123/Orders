var app = WebApplication.CreateBuilder(args).Build();
var mode = "approve"; // approve | decline | error

app.MapPost("/charge", (ChargeRequest req) => mode switch
{
    "decline" => Results.Ok(new ChargeResponse(false)),
    "error"   => Results.StatusCode(500),
    _         => Results.Ok(new ChargeResponse(true))
});

app.MapPost("/__admin/mode", (ModeRequest r) => { mode = r.Mode; return Results.Ok(); });
app.MapPost("/__admin/reset", () => { mode = "approve"; return Results.Ok(); });   // called after every E2E scenario
app.MapGet("/", () => $"FakePaymentService, mode: {mode}");
app.Run();

// ChargeRequest / ChargeResponse come from Orders.Shared (the same types the API's client uses).
record ModeRequest(string Mode);

// Lets WebApplicationFactory<Program> in tests/Orders.ContractTests start the fake in-process.
public partial class Program { }