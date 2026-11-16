using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ErrorTemplateTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Compare(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        if (expected is null) { Check(actual is null, "Null mismatch at " + path); return; }
        Check(actual != null && expected.GetValueKind() == actual.GetValueKind(), "Type mismatch at " + path);
        if (expected is JsonObject obj)
        {
            var result = actual!.AsObject();
            Check(obj.Select(p => p.Key).Order().SequenceEqual(result.Select(p => p.Key).Order()), "Exact field set/casing mismatch at " + path);
            foreach (var p in obj) Compare(p.Value, result[p.Key], path + "." + p.Key);
        }
        else if (expected is JsonArray array)
        {
            Check(array.Count == actual!.AsArray().Count, "Array shape mismatch at " + path);
            for (int i = 0; i < array.Count; i++) Compare(array[i], actual[i], path + "[" + i + "]");
        }
        else if (!path.StartsWith("$.TransactionHandle.", StringComparison.Ordinal))
            Check(JsonNode.DeepEquals(expected, actual), "Reference value mismatch at " + path);
    }
    public static async Task Run(string project, Func<JsonObject> sale, Func<string,string,int,Task> configure,
        Func<JsonObject,Task<HttpResponseMessage>> post, Func<Task<JsonNode>> state,
        Func<string,Func<Task>,Task> test)
    {
        JsonObject Unique(int kind = 2)
        {
            var request = sale();
            request["TransactionHandle"]!["Fingerprint"] = "LOCAL-ERROR-" + Guid.NewGuid().ToString("N");
            request["Sale"]!["PaymentInformations"]![0]!["Mediator"] = kind;
            return request;
        }
        async Task Trace(HttpResponseMessage response, string body, string selected, string used, bool repeated)
        {
            var last = (await state())["lastHttp"]!;
            Check(last["statusCode"]!.GetValue<int>() == (int)response.StatusCode && last["body"]!.GetValue<string>() == body,
                "Displayed response differs from actual HTTP bytes/status.");
            Check(last["selectedScenario"]!.GetValue<string>() == selected && last["usedScenario"]!.GetValue<string>() == used &&
                last["repeated"]!.GetValue<bool>() == repeated, "Selected/applied/used scenario not traceable.");
        }
        foreach (var item in new[] { ("Busy20",20,"cihaz_mesgul"), ("Error138",138,"techpos_mesgul"),
            ("Error119",119,"odeme_saglayicisi"), ("Error199",199,"gunsonu_aliniz"),
            ("Error73",73,"islem_sira_numarasi"), ("Error9001",9001,"adres_erisim_hatasi") })
        {
            var current = item;
            await test("observed error " + current.Item2 + ": exact reference and actual response trace", async () =>
            {
                var expected = JsonNode.Parse(File.ReadAllText(Directory.EnumerateFiles(Path.Combine(project,"References"),current.Item3 + "*.txt").Single()))!;
                await configure(current.Item1,"ApplySelectedScenario",150);
                var request = Unique();
                int before = (await state())["captureCount"]!.GetValue<int>();
                using var response = await post(request);
                string text = await response.Content.ReadAsStringAsync();
                var actual = JsonNode.Parse(text)!;
                Compare(expected,actual);
                Check(response.StatusCode == HttpStatusCode.OK && actual["ErrorCode"]!.GetValue<int>() == current.Item2 &&
                    !text.Contains("SENTETIK",StringComparison.OrdinalIgnoreCase), "Observed error not sent unchanged.");
                Check((await state())["captureCount"]!.GetValue<int>() == before, "Error captured a payment.");
                await Trace(response,text,current.Item1,current.Item1,false);
                // Repeat with recorded-outcome policy after changing applied scenario.
                await configure("Success","ReplaySavedOutcome",150);
                using var repeat = await post(request);
                string replay = await repeat.Content.ReadAsStringAsync();
                Check(replay == text && (await state())["captureCount"]!.GetValue<int>() == before, "Error replay became success/capture.");
                await Trace(repeat,replay,"Success",current.Item1,true);
            });
        }
        foreach (int method in new[] { 1,2 })
        {
            int kind = method;
            await test("single Success uses requested payment method " + kind + " and reports it read-only", async () =>
            {
                await configure("Success","ApplySelectedScenario",150);
                using var response = await post(Unique(kind));
                string body = await response.Content.ReadAsStringAsync();
                var payment = JsonNode.Parse(body)!["Data"]!["AddedPayments"]![0]!;
                Check(response.IsSuccessStatusCode && payment["PaymentTypeId"]!.GetValue<int>() == kind &&
                    payment[kind == 1 ? "CashPayment" : "OnlinePayment"] is JsonObject &&
                    payment[kind == 1 ? "OnlinePayment" : "CashPayment"] is null, "Request did not determine template.");
                await Trace(response,body,"Success","Success",false);
                Check((await state())["lastHttp"]!["paymentTypeId"]!.GetValue<int>() == kind,"Read-only request payment information wrong.");
                // Legacy names stay readable without forcing their former payment type.
                await configure(kind == 1 ? "SuccessCard" : "SuccessCash","ApplySelectedScenario",150);
                Check((await state())["options"]!["scenario"]!.GetValue<string>() == "Success","Legacy success choice not normalized.");
                using var legacy = await post(Unique(kind));
                Check(legacy.IsSuccessStatusCode && JsonNode.Parse(await legacy.Content.ReadAsStringAsync())!["Data"]!["AddedPayments"]![0]!["PaymentTypeId"]!.GetValue<int>() == kind,
                    "Legacy name constrained/converted requested method.");
            });
            int invalid = kind == 1 ? 0 : 3;
            await test("single Success rejects unsupported payment method " + invalid + " without silent conversion", async () =>
            {
                await configure("Success","ApplySelectedScenario",150);
                int captures = (await state())["captureCount"]!.GetValue<int>();
                int records = (await state())["payments"]!.AsArray().Count;
                using var response = await post(Unique(invalid));
                var snapshot = await state();
                Check(response.StatusCode == HttpStatusCode.BadRequest && snapshot["captureCount"]!.GetValue<int>() == captures &&
                    snapshot["payments"]!.AsArray().Count == records,"Unsupported method was converted or captured.");
                Check(snapshot["lastHttp"]!["paymentTypeId"]!.GetValue<int>() == invalid,"Unsupported incoming method not reported accurately.");
            });
        }
        await test("completed replay explains why saved success overrides selected error; selected-error policy never recaptures", async () =>
        {
            await configure("Success","ReplaySavedOutcome",150);
            var request = Unique();
            using var first = await post(request);
            string original = await first.Content.ReadAsStringAsync();
            int captures = (await state())["captureCount"]!.GetValue<int>();
            await configure("Error9001","ReplaySavedOutcome",150);
            using var replay = await post(request);
            string text = await replay.Content.ReadAsStringAsync();
            Check(text == original && (await state())["captureCount"]!.GetValue<int>() == captures, "Completed replay changed payment.");
            await Trace(replay,text,"Error9001","Success",true);
            Check((await state())["lastHttp"]!["note"]!.GetValue<string>().Length > 0, "Replay exception not explained.");
            await configure("Error9001","ApplySelectedScenario",150);
            using var selected = await post(request);
            string error = await selected.Content.ReadAsStringAsync();
            Check(JsonNode.Parse(error)!["ErrorCode"]!.GetValue<int>() == 9001 && (await state())["captureCount"]!.GetValue<int>() == captures,
                "Selected error recaptured or returned success.");
            await Trace(selected,error,"Error9001","Error9001",true);
        });
        await test("correction73 first response matches real error and corrected request still captures once", async () =>
        {
            await configure("Correction73","ReplaySavedOutcome",150);
            var request = Unique();
            int before = (await state())["captureCount"]!.GetValue<int>();
            using var response = await post(request);
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            var reference = JsonNode.Parse(File.ReadAllText(Directory.EnumerateFiles(Path.Combine(project,"References"),"islem_sira_numarasi*.txt").Single()))!;
            Compare(reference,reply);
            Check(reply["TransactionHandle"]!["TransactionSequence"]!.GetValue<int>() != request["TransactionHandle"]!["TransactionSequence"]!.GetValue<int>() &&
                (await state())["captureCount"]!.GetValue<int>() == before,"Correction challenge missing.");
            request["TransactionHandle"]!["TransactionDate"] = reply["TransactionHandle"]!["TransactionDate"]!.DeepClone();
            request["TransactionHandle"]!["TransactionSequence"] = reply["TransactionHandle"]!["TransactionSequence"]!.DeepClone();
            using var corrected = await post(request);
            Check(!JsonNode.Parse(await corrected.Content.ReadAsStringAsync())!["HasError"]!.GetValue<bool>() &&
                (await state())["captureCount"]!.GetValue<int>() == before + 1,"Corrected request failed or captured twice.");
        });
        await test("timeout trace never invents JSON or capture", async () =>
        {
            await configure("Timeout","ReplaySavedOutcome",150);
            int before = (await state())["captureCount"]!.GetValue<int>();
            using var cancellation = new CancellationTokenSource(100);
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect=false,UseProxy=false });
            try { using var response = await client.PostAsJsonAsync("http://127.0.0.1:18080/CompleteSale",Unique(),cancellation.Token); }
            catch (OperationCanceledException) { }
            await Task.Delay(100);
            var snapshot = await state();
            Check(snapshot["lastHttp"]!["statusCode"] is null && snapshot["lastHttp"]!["body"] is null &&
                snapshot["captureCount"]!.GetValue<int>() == before,"Timeout trace invented a response or capture.");
        });
        await test("UI assets separate selected/applied/used and never implicitly apply demo selection", async () =>
        {
            using var client = new HttpClient(new SocketsHttpHandler {AllowAutoRedirect=false,UseProxy=false});
            string html = await client.GetStringAsync("http://127.0.0.1:18080/");
            string js = await client.GetStringAsync("http://127.0.0.1:18080/app.js");
            Check(!html.Contains("id=\"tickets\"") && !js.Contains("state.tickets") && (await state())["tickets"] is null,
                "Removed service UI/state remains exposed.");
            Check(html.Contains("id=\"response-note\""),"UI state missing.");
            foreach (string id in new[] {"selected-scenario","applied-scenario","used-scenario","request-payment-type"}) Check(!html.Contains("id=\""+id+"\""),"Removed scenario-state panel still present.");
            foreach (string choice in new[] {"Success","Busy20","Error138","Error119","Error199","Error73","Error9001","Error72","Timeout","Correction72","Correction73"})
                Check(html.Contains("value=\""+choice+"\""),"Scenario choice missing.");
            Check(html.Split("value=\"Success\"").Length - 1 == 1 && html.Contains("Başarılı Ödeme") &&
                !html.Contains("SuccessCash") && !html.Contains("SuccessCard") && !html.Contains("payment-kind") &&
                !js.Contains("payment-kind"),"UI still selects payment type.");
            Check(!html.Contains("id=\"demo\"") && !js.Contains("function demo") && js.Contains("last.body"),"Removed demo request UI remains or response is fabricated.");
        });
    }
}