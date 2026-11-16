using System.Globalization;
using System.Net;
using System.Text.Json;
using TpPavoSimulator;

var root = Environment.GetEnvironmentVariable("SIMULATOR_DATA_ROOT")
    ?? Path.Combine(AppContext.BaseDirectory, "runtime");
var state = new SimulatorState(Path.GetFullPath(root));

WebApplication Build(int port)
{
    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
    {
        Args = [], ApplicationName = typeof(SimulatorState).Assembly.FullName,
        ContentRootPath = AppContext.BaseDirectory
    });
    builder.Configuration.Sources.Clear();
    builder.Logging.ClearProviders(); // no automatic URL/body/exception logging
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.Listen(IPAddress.Loopback, port); // environment URLs cannot expand this binding
        server.Limits.MaxRequestBodySize = 65536;
    });
    var app = builder.Build();
    if (port == 18080) app.Use(async (context, next) => await state.TraceHttp(context, () => next(context)));
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Simulator"] = "SENTETIK";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; connect-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
        var remote = context.Connection.RemoteIpAddress;
        string origin = context.Request.Headers.Origin.ToString();
        if (remote == null || !IPAddress.IsLoopback(remote) ||
            context.Request.Host.Value != $"127.0.0.1:{port}" ||
            (origin.Length > 0 && origin != $"http://127.0.0.1:{port}"))
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (context.Request.Method == "POST" &&
            (context.Request.ContentType?.Split(';')[0].Trim() != "application/json"))
        {
            context.Response.StatusCode = 415;
            return;
        }
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch
        {
            // Do not put input, credentials, exception messages or stack traces in logs/responses.
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.Headers["X-Simulator"] = "SENTETIK";
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { synthetic = true, error = "SIMULATOR_FAILURE" });
            }
            else context.Abort();
        }
    });
    app.MapGet("/health", () => Results.Json(new { synthetic = true, status = "local-only", port }));
    return app;
}

static async Task<JsonDocument?> Read(HttpContext context)
{
    try { return await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 32 }, context.RequestAborted); }
    catch (JsonException) { return null; }
}
static IResult Invalid() => Results.Json(new { synthetic = true, error = "INVALID_REQUEST" }, statusCode: 400);
static void Source(HttpContext context, string source) => context.Response.Headers["X-Contract-Source"] = source;

static IResult Asset(string name, string contentType)
{
    var stream = typeof(SimulatorState).Assembly.GetManifestResourceStream("TpPavoSimulator.wwwroot." + name)
        ?? throw new InvalidOperationException("Missing embedded control asset.");
    return Results.Stream(stream, contentType);
}

var pavo = Build(18080);
// PAVO cihaz istekleri: Pairing hariç ekrandaki süre kadar bekle, ardından seri numarasını doğrula.
pavo.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!HttpMethods.IsPost(context.Request.Method) || path.StartsWithSegments("/sim")) { await next(context); return; }
    if (!path.Equals("/Pairing", StringComparison.OrdinalIgnoreCase))
    {
        int delay = state.Options.DelayMilliseconds;
        if (delay > 0) await Task.Delay(delay, context.RequestAborted);
    }
    if ((state.Options.SerialNumber ?? "").Length > 0)
    {
        context.Request.EnableBuffering();
        JsonDocument? doc = null;
        try { doc = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 32 }, context.RequestAborted); }
        catch (JsonException) { }
        context.Request.Body.Position = 0;
        using (doc)
        {
            // Geçersiz gövdeyi ilgili uç nokta kendi kuralıyla reddeder.
            if (doc != null && Wire.ValidHandle(doc.RootElement) &&
                state.SerialMismatch(Wire.Text(Wire.Field(doc.RootElement, "TransactionHandle"), "SerialNumber")))
            {
                context.Response.Headers["X-Contract-Source"] = "SYNTHETIC_SERIAL_MISMATCH_71_NO_REAL_REFERENCE";
                context.Items["SimulatorNote"] = "Seri numarası uyuşmazlığı: gelen seri no ekranda ayarlı seri numarasından farklı. İşlem yapılmadı, tahsilat yok.";
                await Wire.Envelope(doc.RootElement, null, 71, "Seri numarası uyuşmazlığı").ExecuteAsync(context);
                return;
            }
        }
    }
    await next(context);
});
pavo.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
pavo.MapGet("/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
pavo.MapGet("/styles.css", () => Asset("styles.css", "text/css; charset=utf-8"));
pavo.MapGet("/sim/state", () => Results.Json(state.Snapshot(), Wire.Json));
pavo.MapGet("/sim/config", () => Results.Json(state.Options, Wire.Json));
pavo.MapPost("/sim/config", async (HttpContext context) =>
{
    if (context.Request.Headers["X-Simulator-Control"].ToString() != "local") return Results.StatusCode(403);
    try
    {
        var value = await JsonSerializer.DeserializeAsync<SimulatorOptions>(context.Request.Body, Wire.Json, context.RequestAborted);
        if (value == null) return Invalid();
        state.Configure(value);
        return Results.Json(state.Options, Wire.Json);
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException) { return Invalid(); }
});
pavo.MapPost("/sim/clear-last", (HttpContext context) =>
{
    if (context.Request.Headers["X-Simulator-Control"].ToString() != "local") return Results.StatusCode(403);
    state.ClearLastHttp();
    return Results.Json(new { synthetic = true, cleared = true });
});
pavo.MapGet("/sim/receipt/{id:int}", (int id) => Results.Text($"SENTETIK belge {id}; gercek tahsilat yok."));

pavo.MapPost("/Pairing", async (HttpContext context) =>
{
    using var doc = await Read(context);
    if (doc == null || !Wire.ValidHandle(doc.RootElement)) return Invalid();
    state.Pairing(doc.RootElement);
    Source(context, "OBSERVED_PAIRING_RESPONSE_SYNTHETIC_DEVICE_FINGERPRINT");
    return PairingResponse.Create(doc.RootElement, DateTimeOffset.Now);
});

pavo.MapPost("/CompleteSale", async (HttpContext context) =>
{
    using var doc = await Read(context);
    if (doc == null || !Wire.ValidHandle(doc.RootElement) ||
        !Wire.Number(Wire.Field(doc.RootElement, "Sale"), "TotalPrice", out decimal amount) || amount is < 0 or > 1000000000)
    {
        state.InvalidSale();
        return Invalid();
    }
    var json = doc.RootElement;
    var paymentInfo = Wire.Field(Wire.Field(json, "Sale"), "PaymentInformations");
    if (paymentInfo.ValueKind == JsonValueKind.Array && paymentInfo.GetArrayLength() == 1 &&
        Wire.Field(paymentInfo[0], "Mediator").TryGetInt32Safe(out int requestedPaymentType))
        context.Items["SimulatorPaymentType"] = requestedPaymentType;
    PaymentAttempt attempt;
    try { attempt = state.Dispatch(json, amount); }
    catch (ArgumentException) { return Invalid(); }
    context.Response.Headers["X-Simulator-Repeated"] = attempt.Duplicate ? "true" : "false";
    context.Response.Headers["X-Simulator-Selected-Scenario"] = attempt.SelectedScenario.ToString();
    context.Response.Headers["X-Simulator-Used-Scenario"] = attempt.Scenario.ToString();
    context.Response.Headers["X-Simulator-Repeat-Policy"] = attempt.RepeatPolicy.ToString();
    if (attempt.Duplicate)
        context.Items["SimulatorNote"] = attempt.RepeatPolicy == RepeatPolicy.ReplaySavedOutcome
            ? "Tekrar istek: kayıtlı sonuç veya bekleyen düzeltme kullanıldı. Önceki tahsilat ikinci kez oluşturulmaz."
            : "Tekrar istek: seçili senaryo uygulandı; önceki tahsilat varsa korunur ve ikinci kez oluşturulmaz.";
    Source(context, ScenarioRules.Captures(attempt.Scenario)
        ? "OBSERVED_SUCCESS_SHAPE_SYNTHETIC_VALUES" : attempt.Scenario is Scenario.Busy20 or Scenario.Error73 or Scenario.Error119 or Scenario.Error138 or Scenario.Error199 or Scenario.Error9001 ? "OBSERVED_ERROR_SHAPE_TEST_IDENTITIES" : "NO_REAL_ERROR_REFERENCE");
    if (attempt.Correction is { } correction)
    {
        context.Response.Headers["X-Simulator-Logical-Transaction"] = attempt.Payment.TransactionId;
        Source(context, "SYNTHETIC_CORRECTION_ASSERTION_OBSERVED_SUCCESS_SHAPE");
        if (!correction.Accepted)
        {
            context.Items["SimulatorNote"] = "Düzeltme kontrolü: " + correction.Outcome + ". Tarih/sıra ve satış içeriği doğrulanmadan başarı verilmez.";
            if (correction.ErrorCode == 73)
                return ErrorResponse.Create(json, 73, correction.ResponseDate, correction.ResponseSequence, correction.HttpStatus);
            return Wire.Envelope(json, null, correction.ErrorCode,
                correction.ErrorCode == 72 ? "İşlem tarihi hatası" : "İşlem düzeltmesi doğrulanamadı",
                correction.HttpStatus, CorrectionWire.Handle(json, correction.ResponseDate, correction.ResponseSequence));
        }
    }
    if (attempt.AmountConflict)
        return Wire.Envelope(json, null, 9102, "Aynı işlem farklı tutarla gönderildi", 409);
    switch (attempt.Scenario)
    {
        case Scenario.Timeout:
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            return Results.Empty;
        case Scenario.LostResponse:
            context.Abort(); // capture was persisted, but no HTTP response reaches the client
            return Results.Empty;
        case Scenario.Malformed:
            return Results.Text("{ bozuk JSON", "application/json");
        case Scenario.Delayed:
            break; // eski senaryo: gecikme artık tüm isteklerde genel olarak uygulanıyor
        case Scenario.Busy20:
            return ErrorResponse.Create(json, 20);
        case Scenario.Error119: return ErrorResponse.Create(json, 119);
        case Scenario.Error138: return ErrorResponse.Create(json, 138);
        case Scenario.Error199: return ErrorResponse.Create(json, 199);
        case Scenario.Rejected:
            return Wire.Envelope(json, null, 9100, "Ödeme reddi");
        case Scenario.Error72:
            return Wire.Envelope(json, null, 72, "İşlem tarihi hatası");
        case Scenario.Error73:
            return ErrorResponse.Create(json, 73);
    }
    if (attempt.Scenario == Scenario.Error9001) return ErrorResponse.Create(json, 9001);
    if (attempt.Scenario is Scenario.Busy20 or Scenario.Error73 or Scenario.Error119 or Scenario.Error138 or Scenario.Error199)
        throw new InvalidOperationException();
    return SuccessResponse.Create(json, attempt.Payment);
});
pavo.MapPost("/PerformEOD", async (HttpContext context) =>
{
    using var doc = await Read(context);
    if (doc == null || !Wire.ValidHandle(doc.RootElement)) return Invalid();
    var json = doc.RootElement;
    if (await DeviceCommandResponse.ApplyScenario(context, json, state.Options.Scenario) is { } early) return early;
    Source(context, "OBSERVED_EOD_RESPONSE_SANITIZED_SYNTHETIC_VALUES");
    return DeviceCommandResponse.PerformEod(json, state.CapturedPayments(), DateTimeOffset.Now);
});
pavo.MapPost("/AbortCurrentPayment", async (HttpContext context) =>
{
    using var doc = await Read(context);
    if (doc == null || !Wire.ValidHandle(doc.RootElement)) return Invalid();
    var json = doc.RootElement;
    if (await DeviceCommandResponse.ApplyScenario(context, json, state.Options.Scenario) is { } early) return early;
    Source(context, "OBSERVED_IDLE_ABORT_ERROR_102");
    return DeviceCommandResponse.AbortWithoutActivePayment(json);
});
pavo.MapPost("/UpdateTerminalData", async (HttpContext context) =>
{
    using var doc = await Read(context);
    if (doc == null || !Wire.ValidHandle(doc.RootElement)) return Invalid();
    var json = doc.RootElement;
    if (await DeviceCommandResponse.ApplyScenario(context, json, state.Options.Scenario) is { } early) return early;
    Source(context, "OBSERVED_UPDATE_TERMINAL_DATA_RESPONSE");
    return DeviceCommandResponse.UpdateTerminalData(json);
});
pavo.MapPost("/RebootDevice", async (HttpContext context) =>
{
    using var doc = await Read(context);
    if (doc == null || !Wire.ValidHandle(doc.RootElement)) return Invalid();
    var json = doc.RootElement;
    if (await DeviceCommandResponse.ApplyScenario(context, json, state.Options.Scenario) is { } early) return early;
    Source(context, "OBSERVED_REBOOT_DEVICE_RESPONSE_NO_RESTART");
    return DeviceCommandResponse.RebootDevice(json);
});
pavo.MapPost("/ListPendingSaleWithDetail", async (HttpContext context) =>
{
    using var doc = await Read(context);
    if (doc == null || !Wire.ValidHandle(doc.RootElement)) return Invalid();
    Source(context, "SYNTHETIC_SOURCE_MODEL_ONLY_REAL_LOGS_CONTAIN_FAILURE");
    var pending = state.Pending();
    return Wire.Envelope(doc.RootElement, new { Sales = pending, SalesCount = pending.Length });
});
pavo.MapPost("/FinalizeSale", async (HttpContext context) =>
{
    using var doc = await Read(context);
    if (doc == null || !Wire.ValidHandle(doc.RootElement)) return Invalid();
    Source(context, "SYNTHETIC_FROM_SOURCE_MODEL_NO_REAL_FINALIZE_RESPONSE");
    string number = Wire.Text(Wire.Field(doc.RootElement, "Sale"), "SaleNumber");
    return state.FinalizeSale(number)
        ? Wire.Envelope(doc.RootElement, null, message: "SENTETIK: sanal satis sonlandirildi.")
        : Wire.Envelope(doc.RootElement, null, 9103, "SENTETIK: sanal satis bulunamadi; 9103 test kodu.", 404);
});
pavo.MapFallback(() => Wire.Unsupported());

try
{
    await pavo.StartAsync();
    Console.WriteLine("PAVO + kontrol: http://127.0.0.1:18080");
    Console.WriteLine("Yalnizca yerel PAVO simulatoru; dis sisteme yonlendirme yok.");
    await pavo.WaitForShutdownAsync();
}
finally
{
    await pavo.StopAsync();
    await pavo.DisposeAsync();
}