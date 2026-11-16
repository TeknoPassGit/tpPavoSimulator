using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("Pass the simulator DLL or published EXE path.");
string dll = Path.GetFullPath(args[0]);
string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string artifact = Path.Combine(project, "tests", "artifacts", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(artifact);
const string Canary = "SYNTHETIC_PRIVACY_CANARY_892461";
using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
var results = new List<object>();
Process? server = null;
int seq = 1000, passed = 0, failed = 0;
string savedTransaction = "", savedSale = "";
JsonObject? replayRequest = null;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static string Base(int port) => $"http://127.0.0.1:{port}";
async Task<JsonNode> State()
    => JsonNode.Parse(await client.GetStringAsync(Base(18080) + "/sim/state"))!;
async Task<HttpResponseMessage> Post(int port, string path, object body, CancellationToken token = default)
{
    // Addresses are constants, proxy/redirects are disabled; external URLs are never accepted.
    return await client.PostAsync(Base(port) + path,
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), token);
}
async Task Configure(string scenario, string repeat = "ReplaySavedOutcome", int delay = 0)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, Base(18080) + "/sim/config");
    request.Headers.Add("X-Simulator-Control", "local");
    request.Content = JsonContent.Create(new { scenario, repeatPolicy = repeat, delayMilliseconds = delay });
    using var response = await client.SendAsync(request);
    Check(response.IsSuccessStatusCode, "Configuration rejected.");
}
JsonObject Sale()
{
    return new JsonObject
    {
        ["TransactionHandle"] = new JsonObject
        {
            ["SerialNumber"] = Canary, ["Fingerprint"] = Canary,
            ["TransactionDate"] = "2026-11-04T12:00:00.000001", ["TransactionSequence"] = ++seq
        },
        ["Sale"] = new JsonObject
        {
            ["TotalPrice"] = 12.50m, ["GrossPrice"] = 12.50m,
            ["PaymentInformations"] = new JsonArray(new JsonObject { ["Mediator"] = 2, ["Amount"] = 12.50m }),
            ["AddedSaleItems"] = new JsonArray(new JsonObject
            {
                ["Name"] = "SENTETIK TEST", ["ReservedText"] = "TEST-ITEM", ["ItemQuantity"] = 1m,
                ["UnitPriceAmount"] = 12.50m, ["TotalPriceAmount"] = 12.50m, ["VATRate"] = 20m
            }),
            ["CustomerParty"] = new JsonObject { ["TaxNumber"] = Canary, ["Phone"] = Canary, ["EMail"] = Canary },
            ["CardNo"] = Canary
        },
        ["password"] = Canary, ["token"] = Canary
    };
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.GetType().Name }); Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message); }
}
async Task Start()
{
    if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == 18081)) throw new InvalidOperationException("Removed service port is occupied.");
    foreach (int port in new[] { 18080 })
    {
        var probe = new TcpListener(IPAddress.Loopback, port);
        try { probe.Start(); } finally { probe.Stop(); }
    }
    bool executable = Path.GetExtension(dll).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    var start = new ProcessStartInfo(executable ? dll : "dotnet")
    {
        WorkingDirectory = artifact, CreateNoWindow = true, UseShellExecute = false,
        RedirectStandardError = true, RedirectStandardOutput = true
    };
    if (!executable) start.ArgumentList.Add(dll);
    start.Environment["SIMULATOR_DATA_ROOT"] = artifact;
    start.Environment["ASPNETCORE_URLS"] = "http://0.0.0.0:19999"; // must be ignored
    start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    server = Process.Start(start) ?? throw new Exception("Server did not start.");
    _ = server.StandardOutput.ReadToEndAsync();
    _ = server.StandardError.ReadToEndAsync();
    for (int i = 0; i < 100; i++)
    {
        if (server.HasExited) throw new Exception("Server exited before readiness.");
        try
        {
            using var first = await client.GetAsync(Base(18080) + "/health");

            if (first.IsSuccessStatusCode) return;
        }
        catch (HttpRequestException) { }
        await Task.Delay(50);
    }
    throw new TimeoutException("Local server did not become ready.");
}
async Task Stop()
{
    if (server == null) return;
    if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
    server.Dispose();
    server = null;
}


JsonObject CorrectionSale()
{
    var sale = Sale();
    sale["TransactionHandle"]!["Fingerprint"] = Canary + "-CORR-" + Guid.NewGuid().ToString("N");
    sale["Sale"]!["AddedSaleItems"] = new JsonArray(new JsonObject
    {
        ["Name"] = Canary, ["IsGeneric"] = false, ["UnitCode"] = "C62", ["TaxGroupCode"] = "KDV20",
        ["ItemQuantity"] = 1, ["UnitPriceAmount"] = 12.50m, ["GrossPriceAmount"] = 12.50m,
        ["TotalPriceAmount"] = 12.50m, ["ReservedText"] = Canary, ["PriceEffect"] = new JsonObject()
    });
    return sale;
}
async Task<(JsonObject Sale, JsonNode Reply, string Id)> StartCorrection(int code, JsonObject? request = null)
{
    await Configure("Correction" + code);
    var sale = request ?? CorrectionSale();
    using var response = await Post(18080, "/CompleteSale", sale);
    var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    Check(response.IsSuccessStatusCode && reply["HasError"]!.GetValue<bool>() &&
        reply["ErrorCode"]!.GetValue<int>() == code, "Correction challenge missing.");
    Check(response.Headers.GetValues("X-Simulator").Single() == "SENTETIK", "Correction provenance missing.");
    string logical = response.Headers.GetValues("X-Simulator-Logical-Transaction").Single();
    return (sale, reply, logical);
}
JsonObject Fixed(JsonObject sale, JsonNode reply)
{
    var corrected = (JsonObject)sale.DeepClone();
    corrected["TransactionHandle"]!["TransactionDate"] = reply["TransactionHandle"]!["TransactionDate"]!.GetValue<string>();
    corrected["TransactionHandle"]!["TransactionSequence"] = reply["TransactionHandle"]!["TransactionSequence"]!.GetValue<int>();
    return corrected;
}
async Task<JsonNode> CorrectionEvidence(string id)
{
    return (await State())["corrections"]!.AsArray().Single(c => c!["logicalTransactionId"]!.GetValue<string>() == id)!;
}
async Task<JsonNode> CorrectionPayment(string id)
{
    return (await State())["payments"]!.AsArray().Single(p => p!["transactionId"]!.GetValue<string>() == id)!;
}

try
{
    await Start();
    await Test("PAVO is the only loopback listener and port 18081 stays closed", async () =>
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(x => x.Port is 18080 or 18081).ToArray();
        Check(listeners.Length == 1 && listeners[0].Port == 18080 && listeners[0].Address.Equals(IPAddress.Loopback), "Unexpected or removed-service binding.");
        using var health = await client.GetAsync(Base(18080) + "/health");
        Check(health.Headers.GetValues("X-Simulator").Single() == "SENTETIK", "Synthetic marker missing.");
    });
    await Test("cross-origin, rebinding host and unguarded control POST rejected", async () =>
    {
        using var origin = new HttpRequestMessage(HttpMethod.Get, Base(18080) + "/sim/state");
        origin.Headers.Add("Origin", "https://untrusted.invalid");
        using var a = await client.SendAsync(origin); Check(a.StatusCode == HttpStatusCode.Forbidden, "Origin accepted.");
        using var host = new HttpRequestMessage(HttpMethod.Get, Base(18080) + "/sim/state");
        host.Headers.Host = "untrusted.invalid:18080";
        using var b = await client.SendAsync(host); Check(b.StatusCode == HttpStatusCode.Forbidden, "Host accepted.");
        using var c = await Post(18080, "/sim/config", new { scenario = "Success" });
        Check(c.StatusCode == HttpStatusCode.Forbidden, "Control header not enforced.");
    });
    await Test("Pairing matches observed response shape with device fingerprint and next sequence", async () =>
    {
        var sale = Sale();
        using var response = await Post(18080, "/Pairing", new { TransactionHandle = sale["TransactionHandle"] });
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Check(response.IsSuccessStatusCode && json["HasError"]!.GetValue<bool>() == false &&
            json["Message"]!.GetValue<string>() == "", "Pairing failed.");
        Check(string.Join(",", json.Select(p => p.Key)) == "HasError,Message,TransactionHandle", "Pairing fields differ from observed response.");
        var handle = json["TransactionHandle"]!.AsObject();
        Check(string.Join(",", handle.Select(p => p.Key)) == "SerialNumber,Fingerprint,TransactionSequence,TransactionDate", "Handle fields differ.");
        Check(handle["SerialNumber"]!.GetValue<string>() == Canary, "Serial not echoed.");
        Check(handle["TransactionSequence"]!.GetValue<int>() == seq + 1, "Sequence must be incremented.");
        Check(handle["Fingerprint"]!.GetValue<string>().EndsWith(":user/release-keys") &&
            handle["Fingerprint"]!.GetValue<string>() != Canary, "Device fingerprint expected.");
        Check(System.Text.RegularExpressions.Regex.IsMatch(handle["TransactionDate"]!.GetValue<string>(), @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{6}$"), "Date format differs.");
        Check(response.Headers.GetValues("X-Contract-Source").Single().Contains("OBSERVED_PAIRING"), "Provenance missing.");
    });
    await Test("success matches observed fiscal and bank fields", async () =>
    {
        await Configure("Success");
        replayRequest = Sale();
        using var response = await Post(18080, "/CompleteSale", replayRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Check(!json["HasError"]!.GetValue<bool>() && json["Data"]!["StatusId"]!.GetValue<int>() == 4, "Success contract wrong.");
        Check(json["Data"]!["AddedPayments"]![0]!["StatusId"]!.GetValue<int>() == 2 &&
            json["Data"]!["AddedPayments"]![0]!["OnlinePayment"]!["ResponseCode"]!.GetValue<string>() == "00", "Bank fields wrong.");
        savedSale = json["Data"]!["SaleNumber"]!.GetValue<string>();
        Check(json["Data"]!["FinancialDocuments"]![0]!["InvoiceNo"]!.GetValue<string>().StartsWith("SIM-"), "Fiscal number not synthetic.");
        var state = await State();
        var entry = state["payments"]!.AsArray().Single(p => p!["saleNumber"]!.GetValue<string>() == savedSale)!;
        savedTransaction = entry["transactionId"]!.GetValue<string>();
        Check(entry["captured"]!.GetValue<bool>() && savedTransaction.Length == 64, "Safe identity not persisted.");
    });
    foreach (var testCase in new[] { ("Busy20", 20), ("Rejected", 9100), ("Error72", 72), ("Error73", 73), ("Error119", 119), ("Error138", 138), ("Error199", 199) })
    {
        var item = testCase;
        await Test("synthetic error " + item.Item1 + " does not capture", async () =>
        {
            await Configure(item.Item1);
            int before = (await State())["captureCount"]!.GetValue<int>();
            using var response = await Post(18080, "/CompleteSale", Sale());
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(json["HasError"]!.GetValue<bool>() && json["ErrorCode"]!.GetValue<int>() == item.Item2, "Error response wrong.");
            Check(response.Headers.GetValues("X-Simulator").Single() == "SENTETIK", "Simulator header missing.");
            Check((await State())["captureCount"]!.GetValue<int>() == before, "Error created capture.");
        });
    }
    await Test("response delay applies to all requests except Pairing", async () =>
    {
        await Configure("Success", delay: 300);
        var watch = Stopwatch.StartNew();
        using (var response = await Post(18080, "/CompleteSale", Sale()))
            Check(response.IsSuccessStatusCode && watch.ElapsedMilliseconds >= 250, "Delay not applied to CompleteSale.");
        watch.Restart();
        using (var response = await Post(18080, "/Pairing", Sale()))
            Check(response.IsSuccessStatusCode && watch.ElapsedMilliseconds < 250, "Pairing must respond immediately.");
        await Configure("Success", delay: 0);
    });
    await Test("timeout produces no response and no capture", async () =>
    {
        await Configure("Timeout");
        int before = (await State())["captureCount"]!.GetValue<int>();
        using var cancel = new CancellationTokenSource(200);
        bool timedOut = false;
        try { using var response = await Post(18080, "/CompleteSale", Sale(), cancel.Token); }
        catch (OperationCanceledException) { timedOut = true; }
        Check(timedOut && (await State())["captureCount"]!.GetValue<int>() == before, "Timeout behavior wrong.");
    });
    await Test("malformed JSON is intentionally unparsable", async () =>
    {
        await Configure("Malformed");
        using var response = await Post(18080, "/CompleteSale", Sale());
        bool malformed = false;
        try { JsonNode.Parse(await response.Content.ReadAsStringAsync()); } catch (JsonException) { malformed = true; }
        Check(malformed, "Response was valid JSON.");
    });
    await Test("capture with lost response is recovered by duplicate without second capture", async () =>
    {
        await Configure("LostResponse");
        var sale = Sale();
        int before = (await State())["captureCount"]!.GetValue<int>();
        bool lost = false;
        try { using var response = await Post(18080, "/CompleteSale", sale); }
        catch (HttpRequestException) { lost = true; }
        Check(lost && (await State())["captureCount"]!.GetValue<int>() == before + 1, "Lost response did not preserve capture.");
        using var repeat = await Post(18080, "/CompleteSale", sale);
        var json = JsonNode.Parse(await repeat.Content.ReadAsStringAsync())!;
        Check(repeat.IsSuccessStatusCode && !json["HasError"]!.GetValue<bool>(), "Saved outcome not replayed.");
        Check((await State())["captureCount"]!.GetValue<int>() == before + 1, "Duplicate captured twice.");
    });
    await Test("concurrent duplicates create exactly one capture", async () =>
    {
        await Configure("Duplicate");
        var sale = Sale();
        int before = (await State())["captureCount"]!.GetValue<int>();
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Post(18080, "/CompleteSale", sale)));
        try { Check(responses.All(r => r.IsSuccessStatusCode), "Concurrent call failed."); }
        finally { foreach (var response in responses) response.Dispose(); }
        var state = await State();
        Check(state["captureCount"]!.GetValue<int>() == before + 1, "Concurrent double capture.");
        Check(state["payments"]!.AsArray().Any(p => p!["requestCount"]!.GetValue<int>() == 16), "Repeat count lost.");
    });
    await Test("duplicate response scenario can change without recapture", async () =>
    {
        await Configure("Error72", "ApplySelectedScenario");
        var sale = Sale();
        using var first = await Post(18080, "/CompleteSale", sale);
        int before = (await State())["captureCount"]!.GetValue<int>();
        await Configure("Success", "ApplySelectedScenario");
        using var second = await Post(18080, "/CompleteSale", sale);
        await Configure("Error73", "ApplySelectedScenario");
        using var third = await Post(18080, "/CompleteSale", sale);
        var repeatError = JsonNode.Parse(await third.Content.ReadAsStringAsync())!;
        Check(repeatError["ErrorCode"]!.GetValue<int>() == 73, "Selected repeat response not applied.");
        var changedState = await State();
        Check(changedState["captureCount"]!.GetValue<int>() == before + 1, "Scenario switch recaptured.");
        Check(changedState["payments"]!.AsArray().Any(p => p!["captured"]!.GetValue<bool>() && p["result"]!.GetValue<string>() == "ERROR_RESPONSE_ALREADY_CAPTURED"), "Prior capture was mislabeled.");
    });
    await Test("same transaction with different amount rejected", async () =>
    {
        await Configure("Success");
        var sale = Sale();
        using var first = await Post(18080, "/CompleteSale", sale);
        int before = (await State())["captureCount"]!.GetValue<int>();
        sale["Sale"]!["TotalPrice"] = 99m;
        using var second = await Post(18080, "/CompleteSale", sale);
        Check(second.StatusCode == HttpStatusCode.Conflict && (await State())["captureCount"]!.GetValue<int>() == before, "Amount mismatch accepted.");
    });
    await Test("pending, finalization, abort and EOD use local-only contracts", async () =>
    {
        var sale = Sale();
        using var pending = await Post(18080, "/ListPendingSaleWithDetail", sale);
        var list = JsonNode.Parse(await pending.Content.ReadAsStringAsync())!;
        Check(list["Data"]!["SalesCount"]!.GetValue<int>() > 0, "Pending list empty.");
        sale["Sale"] = new JsonObject { ["SaleNumber"] = savedSale };
        using var finalize = await Post(18080, "/FinalizeSale", sale); Check(finalize.IsSuccessStatusCode, "Finalize failed.");
        int before = (await State())["captureCount"]!.GetValue<int>();
        using var abort = await Post(18080, "/AbortCurrentPayment", sale);
        using var eod = await Post(18080, "/PerformEOD", sale);
        var json = JsonNode.Parse(await eod.Content.ReadAsStringAsync())!;
        Check(abort.IsSuccessStatusCode && json["Data"]!["eodData"]!["grandTotals"] is JsonArray, "Command response wrong.");
        Check(json["Data"]!["eodData"]!["grandTotals"]![0]!["transactionCount"]!.GetValue<string>() == before.ToString(), "EOD count mismatch.");
        Check((await State())["captureCount"]!.GetValue<int>() == before, "Command changed capture count.");
    });
    await Test("unknown PAVO commands and removed routes explicitly unsupported", async () =>
    {
        using var p = await Post(18080, "/UnknownPavoCommand", Sale());

        Check(p.StatusCode == HttpStatusCode.NotImplemented, "Unsupported command accepted.");
        foreach (string path in new[] { "/api/ServiceProducts/select", "/api/subcustomers/list", "/api/Barcode/ListSubCustomerRegions",
            "/api/Invoice/CheckInvoiceNumber", "/api/Barcode/BarcodeCreateV3", "/api/Eod/InsertEOD" })
        {
            using var removed = await Post(18080, path, new { });
            Check(removed.StatusCode == HttpStatusCode.NotImplemented, "Removed route still has a handler.");
        }
    });
    await Test("invalid CompleteSale body is safely recorded", async () =>
    {
        using var response = await client.PostAsync(Base(18080) + "/CompleteSale",
            new StringContent("{" + Canary, Encoding.UTF8, "application/json"));
        Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid body accepted.");
        Check((await State())["events"]!.AsArray().Any(e => e!["event"]!.GetValue<string>() == "COMPLETE_SALE_INVALID"), "Invalid attempt not recorded.");
    });
    await Test("control screen and local assets served with restrictive CSP", async () =>
    {
        using var response = await client.GetAsync(Base(18080) + "/");
        Check((await response.Content.ReadAsStringAsync()).Contains("PAVO LAB"), "Screen missing.");
        Check(response.Headers.GetValues("Content-Security-Policy").Single().Contains("connect-src 'self'"), "CSP missing.");
        using var js = await client.GetAsync(Base(18080) + "/app.js");
        using var css = await client.GetAsync(Base(18080) + "/styles.css");
        Check(js.IsSuccessStatusCode && css.IsSuccessStatusCode, "Local asset missing.");
    });

    // All original 23 tests remain intact. These independently emulate the
    // WinForms response-to-next-request field copy, without launching WinForms.
    foreach (int correctionCode in new[] { 72, 73 })
    {
        int code = correctionCode;
        await Test("correction " + code + " changes only intended field and links success/replay", async () =>
        {
            int before = (await State())["captureCount"]!.GetValue<int>();
            var flow = await StartCorrection(code);
            var sent = flow.Sale["TransactionHandle"]!;
            var expected = flow.Reply["TransactionHandle"]!;
            Check((sent["TransactionDate"]!.GetValue<string>() != expected["TransactionDate"]!.GetValue<string>()) == (code == 72),
                "Date challenge wrong.");
            Check((sent["TransactionSequence"]!.GetValue<int>() != expected["TransactionSequence"]!.GetValue<int>()) == (code == 73),
                "Sequence challenge wrong.");
            Check(!(await CorrectionPayment(flow.Id))["captured"]!.GetValue<bool>(), "First request captured.");
            var fixedSale = Fixed(flow.Sale, flow.Reply);
            using var response = await Post(18080, "/CompleteSale", fixedSale);
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(!reply["HasError"]!.GetValue<bool>() && reply["Data"]!["StatusId"]!.GetValue<int>() == 4, "Corrected retry not successful.");
            Check(reply["Data"]!["FinancialDocuments"]![0]!["InvoiceNo"]!.GetValue<string>().StartsWith("SIM-") &&
                reply["Data"]!["AddedPayments"]![0]!["OnlinePayment"]!["ResponseCode"]!.GetValue<string>() == "00", "Success schema changed.");
            Check(response.Headers.GetValues("X-Simulator-Logical-Transaction").Single() == flow.Id, "Logical identity changed.");
            using var repeat = await Post(18080, "/CompleteSale", fixedSale);
            Check(repeat.IsSuccessStatusCode && (await State())["captureCount"]!.GetValue<int>() == before + 1, "Corrected replay captured twice.");
            var payment = await CorrectionPayment(flow.Id);
            var evidence = await CorrectionEvidence(flow.Id);
            Check(payment["requestCount"]!.GetValue<int>() == 3 && evidence["completed"]!.GetValue<bool>() &&
                evidence["steps"]!.AsArray().Count == 3, "Trace or logical count lost.");
            Check(reply["Data"]!["SaleNumber"]!.GetValue<string>() == payment["saleNumber"]!.GetValue<string>(), "Logical sale split.");
        });
        await Test("correction " + code + " unchanged retry blocked even after scenario switch, then recovers", async () =>
        {
            var flow = await StartCorrection(code);
            await Configure("Success", "ApplySelectedScenario");
            using var response = await Post(18080, "/CompleteSale", flow.Sale);
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(reply["HasError"]!.GetValue<bool>() && reply["ErrorCode"]!.GetValue<int>() == code, "Scenario switch bypassed validation.");
            Check(!(await CorrectionPayment(flow.Id))["captured"]!.GetValue<bool>(), "Unfixed retry captured.");
            var evidence = await CorrectionEvidence(flow.Id);
            Check(evidence["steps"]![1]!["result"]!.GetValue<string>() == "CORRECTION_HANDLE_NOT_FIXED", "Failure not recorded.");
            using var corrected = await Post(18080, "/CompleteSale", Fixed(flow.Sale, flow.Reply));
            Check(!(JsonNode.Parse(await corrected.Content.ReadAsStringAsync())!)["HasError"]!.GetValue<bool>(), "Valid recovery failed.");
        });
        await Test("correction " + code + " wrong new value correlates to same uncaptured operation", async () =>
        {
            var flow = await StartCorrection(code);
            var wrong = Fixed(flow.Sale, flow.Reply);
            if (code == 72) wrong["TransactionHandle"]!["TransactionDate"] = "2040-02-03T04:05:06.000001";
            else wrong["TransactionHandle"]!["TransactionSequence"] = flow.Reply["TransactionHandle"]!["TransactionSequence"]!.GetValue<int>() + 11;
            using var response = await Post(18080, "/CompleteSale", wrong);
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(reply["HasError"]!.GetValue<bool>() && !(await CorrectionPayment(flow.Id))["captured"]!.GetValue<bool>(), "Wrong correction captured.");
            Check(response.Headers.GetValues("X-Simulator-Logical-Transaction").Single() == flow.Id &&
                (await CorrectionPayment(flow.Id))["requestCount"]!.GetValue<int>() == 2, "Wrong retry split logical operation.");
        });
        await Test("correction " + code + " changed amount rejected", async () =>
        {
            var flow = await StartCorrection(code);
            var wrong = Fixed(flow.Sale, flow.Reply);
            wrong["Sale"]!["TotalPrice"] = 99m;
            using var response = await Post(18080, "/CompleteSale", wrong);
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(response.StatusCode == HttpStatusCode.Conflict && reply["ErrorCode"]!.GetValue<int>() == 9104, "Changed amount accepted.");
            Check(!(await CorrectionPayment(flow.Id))["captured"]!.GetValue<bool>() &&
                !(await CorrectionEvidence(flow.Id))["steps"]![1]!["payloadMatches"]!.GetValue<bool>(), "Payload failure missing.");
        });
        await Test("correction " + code + " changed sale item rejected", async () =>
        {
            var flow = await StartCorrection(code);
            var wrong = Fixed(flow.Sale, flow.Reply);
            wrong["Sale"]!["AddedSaleItems"]![0]!["ItemQuantity"] = 2;
            wrong["Sale"]!["AddedSaleItems"]![0]!["Name"] = Canary + "-changed";
            using var response = await Post(18080, "/CompleteSale", wrong);
            Check(response.StatusCode == HttpStatusCode.Conflict &&
                !(await CorrectionPayment(flow.Id))["captured"]!.GetValue<bool>(), "Changed item accepted.");
        });
        await Test("correction " + code + " unrelated handle field must remain unchanged", async () =>
        {
            var flow = await StartCorrection(code);
            var wrong = Fixed(flow.Sale, flow.Reply);
            if (code == 72) wrong["TransactionHandle"]!["TransactionSequence"] = flow.Reply["TransactionHandle"]!["TransactionSequence"]!.GetValue<int>() + 7;
            else wrong["TransactionHandle"]!["TransactionDate"] = "2041-01-02T03:04:05.000001";
            using var response = await Post(18080, "/CompleteSale", wrong);
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(reply["HasError"]!.GetValue<bool>() && !(await CorrectionPayment(flow.Id))["captured"]!.GetValue<bool>(), "Unrelated handle change accepted.");
        });
        await Test("correction " + code + " concurrent corrected retries capture once", async () =>
        {
            int before = (await State())["captureCount"]!.GetValue<int>();
            var flow = await StartCorrection(code);
            var fixedSale = Fixed(flow.Sale, flow.Reply);
            var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Post(18080, "/CompleteSale", fixedSale)));
            try
            {
                foreach (var response in responses)
                {
                    var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                    Check(response.IsSuccessStatusCode && !reply["HasError"]!.GetValue<bool>(), "Concurrent correction failed.");
                }
            }
            finally { foreach (var response in responses) response.Dispose(); }
            Check((await State())["captureCount"]!.GetValue<int>() == before + 1 &&
                (await CorrectionPayment(flow.Id))["requestCount"]!.GetValue<int>() == 17, "Concurrent correction recaptured or lost attempts.");
        });
        await Test("correction " + code + " pending challenge survives server restart", async () =>
        {
            int before = (await State())["captureCount"]!.GetValue<int>();
            var flow = await StartCorrection(code);
            await Stop(); await Start();
            using var response = await Post(18080, "/CompleteSale", Fixed(flow.Sale, flow.Reply));
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(!reply["HasError"]!.GetValue<bool>() && response.Headers.GetValues("X-Simulator-Logical-Transaction").Single() == flow.Id,
                "Restart lost correction.");
            Check((await State())["captureCount"]!.GetValue<int>() == before + 1 &&
                (await CorrectionPayment(flow.Id))["requestCount"]!.GetValue<int>() == 2, "Restart split correction transaction.");
        });
    }
    await Test("correction canonical comparison tolerates property order and numeric formatting", async () =>
    {
        var flow = await StartCorrection(72);
        var fixedSale = Fixed(flow.Sale, flow.Reply);
        var reordered = new JsonObject();
        foreach (var property in fixedSale["Sale"]!.AsObject().Reverse())
            reordered[property.Key] = property.Value?.DeepClone();
        reordered["TotalPrice"] = 12.5000m;
        var item = reordered["AddedSaleItems"]![0]!.AsObject();
        var reorderedItem = new JsonObject();
        foreach (var property in item.Reverse()) reorderedItem[property.Key] = property.Value?.DeepClone();
        reorderedItem["ItemQuantity"] = 1.000m;
        reordered["AddedSaleItems"] = new JsonArray(reorderedItem);
        fixedSale["Sale"] = reordered;
        using var response = await Post(18080, "/CompleteSale", fixedSale);
        Check(!(JsonNode.Parse(await response.Content.ReadAsStringAsync())!)["HasError"]!.GetValue<bool>(), "Canonical comparison rejected equivalent JSON.");
    });
    await Test("completed correction never accepts original uncorrected handle as successful replay", async () =>
    {
        var flow = await StartCorrection(73);
        using var corrected = await Post(18080, "/CompleteSale", Fixed(flow.Sale, flow.Reply));
        int before = (await State())["captureCount"]!.GetValue<int>();
        using var stale = await Post(18080, "/CompleteSale", flow.Sale);
        var reply = JsonNode.Parse(await stale.Content.ReadAsStringAsync())!;
        Check(reply["HasError"]!.GetValue<bool>() && reply["ErrorCode"]!.GetValue<int>() == 73 &&
            (await State())["captureCount"]!.GetValue<int>() == before, "Stale handle accepted after completion.");
    });
    await Test("correction rejects removed sale items", async () =>
    {
        var flow = await StartCorrection(72);
        var wrong = Fixed(flow.Sale, flow.Reply);
        wrong["Sale"]!.AsObject().Remove("AddedSaleItems");
        using var response = await Post(18080, "/CompleteSale", wrong);
        Check(response.StatusCode == HttpStatusCode.Conflict &&
            !(await CorrectionPayment(flow.Id))["captured"]!.GetValue<bool>(), "Removed items accepted.");
    });
    await Test("correction 73 handles maximum sequence without overflow", async () =>
    {
        var sale = CorrectionSale();
        sale["TransactionHandle"]!["TransactionSequence"] = int.MaxValue;
        var flow = await StartCorrection(73, sale);
        Check(flow.Reply["TransactionHandle"]!["TransactionSequence"]!.GetValue<int>() != int.MaxValue, "Sequence unchanged.");
        using var response = await Post(18080, "/CompleteSale", Fixed(flow.Sale, flow.Reply));
        Check(!(JsonNode.Parse(await response.Content.ReadAsStringAsync())!)["HasError"]!.GetValue<bool>(), "Maximum sequence recovery failed.");
    });
    await Test("invalid retry date is rejected and safely redacted in correction trace", async () =>
    {
        var flow = await StartCorrection(72);
        var wrong = Fixed(flow.Sale, flow.Reply);
        wrong["TransactionHandle"]!["TransactionDate"] = Canary;
        using var response = await Post(18080, "/CompleteSale", wrong);
        Check((JsonNode.Parse(await response.Content.ReadAsStringAsync())!)["HasError"]!.GetValue<bool>(), "Invalid date accepted.");
        Check((await CorrectionEvidence(flow.Id))["steps"]![1]!["requestDate"]!.GetValue<string>() == "[INVALID_DATE]", "Untrusted date persisted.");
    });
    await Test("ordinary captured transaction cannot be repurposed as correction", async () =>
    {
        await Configure("Success");
        var sale = CorrectionSale();
        using var first = await Post(18080, "/CompleteSale", sale);
        int before = (await State())["captureCount"]!.GetValue<int>();
        await Configure("Correction72", "ApplySelectedScenario");
        using var second = await Post(18080, "/CompleteSale", sale);
        Check(second.StatusCode == HttpStatusCode.Conflict &&
            (await State())["captureCount"]!.GetValue<int>() == before, "Existing sale was reused as challenge.");
    });
    await Test("correction scenarios remain selectable; trace list and demo buttons removed from screen", async () =>
    {
        string html = await client.GetStringAsync(Base(18080) + "/");
        string js = await client.GetStringAsync(Base(18080) + "/app.js");
        Check(html.Contains("Correction72") && html.Contains("Correction73") &&
            !html.Contains("correction-traces") && !html.Contains("id=\"correct\"") &&
            !js.Contains("renderCorrections"), "Correction scenarios missing or removed UI still present.");
    });
    await Test("unknown handle from same pending device cannot create a new success", async () =>
    {
        var flow = await StartCorrection(72);
        var wrong = (JsonObject)flow.Sale.DeepClone();
        wrong["TransactionHandle"]!["TransactionDate"] = "2042-01-01T00:00:00.000001";
        wrong["TransactionHandle"]!["TransactionSequence"] = 12345678;
        await Configure("Success", "ApplySelectedScenario");
        using var response = await Post(18080, "/CompleteSale", wrong);
        Check((JsonNode.Parse(await response.Content.ReadAsStringAsync())!)["HasError"]!.GetValue<bool>() &&
            response.Headers.GetValues("X-Simulator-Logical-Transaction").Single() == flow.Id &&
            !(await CorrectionPayment(flow.Id))["captured"]!.GetValue<bool>(), "Unknown handle bypassed pending challenge.");
    });
    await Test("correction also rejects changed payment information", async () =>
    {
        var flow = await StartCorrection(73);
        var wrong = Fixed(flow.Sale, flow.Reply);
        wrong["Sale"]!["PaymentInformations"]![0]!["Mediator"] = 1;
        using var response = await Post(18080, "/CompleteSale", wrong);
        Check(response.StatusCode == HttpStatusCode.Conflict &&
            !(await CorrectionPayment(flow.Id))["captured"]!.GetValue<bool>(), "Payment content change accepted.");
    });

    await RealTemplateTests.Run(project, Sale, scenario => Configure(scenario),
        body => Post(18080, "/CompleteSale", body), State, Test);

    await ErrorTemplateTests.Run(project, Sale, Configure, body => Post(18080, "/CompleteSale", body), State, Test);

    await Test("raw card, credential, customer and device canaries never persist", async () =>
    {
        await Task.Delay(50);
        foreach (string file in Directory.EnumerateFiles(artifact, "*", SearchOption.AllDirectories))
            Check(!File.ReadAllText(file).Contains(Canary), "Sensitive canary persisted.");
    });
    await Test("restart retains idempotency and captured outcome", async () =>
    {
        int before = (await State())["captureCount"]!.GetValue<int>();
        await Stop();
        // Old ledgers may contain the removed empty field. PAVO capture and
        // pending-correction ownership must survive opening that format.
        string savedPath = Path.Combine(artifact, "data", "SYNTHETIC-state.json");
        var legacyState = JsonNode.Parse(File.ReadAllText(savedPath))!.AsObject();
        legacyState["tickets"] = new JsonArray();
        File.WriteAllText(savedPath, legacyState.ToJsonString());
        await Start();
        using var repeat = await Post(18080, "/CompleteSale", replayRequest!);
        var json = JsonNode.Parse(await repeat.Content.ReadAsStringAsync())!;
        Check(json["Data"]!["SaleNumber"]!.GetValue<string>() == savedSale, "Sale identity changed across restart.");
        var state = await State();
        Check(state["captureCount"]!.GetValue<int>() == before &&
            state["payments"]!.AsArray().Any(p => p!["transactionId"]!.GetValue<string>() == savedTransaction), "Restart recaptured.");
        Check(!JsonNode.Parse(File.ReadAllText(savedPath))!.AsObject().ContainsKey("tickets"), "Removed data persisted again.");
    });
    await Test("response delay is remembered across restart", async () =>
    {
        await Configure("Success", delay: 300);
        await Stop();
        await Start();
        var config = JsonNode.Parse(await client.GetStringAsync(Base(18080) + "/sim/config"))!;
        Check(config["delayMilliseconds"]!.GetValue<int>() == 300, "Delay not remembered across restart.");
        Check(config["scenario"]!.GetValue<string>() == "Success", "Scenario must reset to default on restart.");
        var watch = Stopwatch.StartNew();
        using (var response = await Post(18080, "/CompleteSale", Sale()))
            Check(response.IsSuccessStatusCode && watch.ElapsedMilliseconds >= 250, "Remembered delay not applied.");
        await Configure("Success", delay: 0);
    });
    await Test("serial number mismatch returns 71 without capture and is remembered across restart", async () =>
    {
        async Task SetSerial(string serial)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Base(18080) + "/sim/config");
            request.Headers.Add("X-Simulator-Control", "local");
            request.Content = JsonContent.Create(new { scenario = "Success", repeatPolicy = "ReplaySavedOutcome", delayMilliseconds = 0, serialNumber = serial });
            using var response = await client.SendAsync(request);
            Check(response.IsSuccessStatusCode, "Serial configuration rejected.");
        }
        await SetSerial("SIM-SERIAL-71");
        int before = (await State())["captureCount"]!.GetValue<int>();
        foreach (string path in new[] { "/CompleteSale", "/PerformEOD", "/Pairing" })
        {
            using var response = await Post(18080, path, Sale());
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(json["HasError"]!.GetValue<bool>() && json["ErrorCode"]!.GetValue<int>() == 71, "Serial mismatch not reported on " + path);
        }
        Check((await State())["captureCount"]!.GetValue<int>() == before, "Serial mismatch created capture.");
        // The general (non-serial) config call must keep the saved serial.
        await Configure("Success");
        await Stop();
        await Start();
        var config = JsonNode.Parse(await client.GetStringAsync(Base(18080) + "/sim/config"))!;
        Check(config["serialNumber"]!.GetValue<string>() == "SIM-SERIAL-71", "Serial not remembered across restart.");
        var matching = Sale();
        matching["TransactionHandle"]!["SerialNumber"] = "SIM-SERIAL-71";
        using (var ok = await Post(18080, "/CompleteSale", matching))
            Check(!JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["HasError"]!.GetValue<bool>(), "Matching serial rejected.");
        await SetSerial("");
        using (var open = await Post(18080, "/CompleteSale", Sale()))
            Check(!JsonNode.Parse(await open.Content.ReadAsStringAsync())!["HasError"]!.GetValue<bool>(), "Empty serial must disable the check.");
    });
}
finally { await Stop(); }
if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port is 18080 or 18081)) throw new InvalidOperationException("Service port not released after test process exit.");
File.WriteAllText(Path.Combine(artifact, "test-results.json"), JsonSerializer.Serialize(new { passed, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT passed={passed} failed={failed}");
Console.WriteLine("ARTIFACTS " + artifact);
return failed == 0 ? 0 : 1;
