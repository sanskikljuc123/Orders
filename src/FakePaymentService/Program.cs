var app = WebApplication.CreateBuilder(args).Build();
var mode = "approve"; // approve | decline | error

app.MapPost("/charge", (ChargeRequest req) => mode switch
{
    "decline" => Results.Ok(new ChargeResponse(false)),
    "error"   => Results.StatusCode(500),
    _         => Results.Ok(new ChargeResponse(true))
});

app.MapPost("/__admin/mode", (ModeRequest r) => { mode = r.Mode; return Results.Ok(); });
app.MapGet("/", () => $"FakePaymentService, mode: {mode}");
app.Run();

record ChargeRequest(decimal Amount);
record ChargeResponse(bool Approved);
record ModeRequest(string Mode);