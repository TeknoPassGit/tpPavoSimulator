using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class RealTemplateTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static decimal Decimal(JsonNode? value) => JsonSerializer.Deserialize<decimal>(value!.ToJsonString());
    private static bool Dynamic(string path) =>
        Regex.IsMatch(path, @"^\$\.TransactionHandle\.(SerialNumber|FingerPrint|TransactionSequence|TransactionDate)$") ||
        path is "$.Data.Id" or "$.Data.SaleNumber" or "$.Data.SaleInquieryLink" ||
        Regex.IsMatch(path, @"^\$\.Data\.AddedPayments\[0\]\.(Id|SaleId|MerchantId)$") ||
        Regex.IsMatch(path, @"^\$\.Data\.AddedPayments\[0\]\.(CashPayment|OnlinePayment)\.(Id|PaymentId|SaleId|GivenAmount|AcquirerName|CardNo)$") ||
        Regex.IsMatch(path, @"^\$\.Data\.AddedSaleItems\[\d+\]\.(Id|Name|VATAmount|VATRate|ReservedText|ItemQuantity|UnitPriceAmount|TotalPriceAmount)$") ||
        Regex.IsMatch(path, @"^\$\.Data\.FinancialDocuments\[0\]\.(Id|InvoiceNo|DocumentNo)$");

    // Compare exact property casing, all nulls and JSON types against the actual
    // reference, never a reconstructed DTO. Only sale-item array length may vary.
    private static void Compare(JsonNode? reference, JsonNode? result, string path = "$")
    {
        if (reference is null) { Check(result is null, "Null mismatch at " + path); return; }
        Check(result is not null && reference.GetValueKind() == result.GetValueKind(), "Type mismatch at " + path);
        if (reference is JsonObject obj)
        {
            var actual = result!.AsObject();
            Check(obj.Select(p => p.Key).Order().SequenceEqual(actual.Select(p => p.Key).Order()), "Property mismatch at " + path);
            foreach (var entry in obj) Compare(entry.Value, actual[entry.Key], path + "." + entry.Key);
        }
        else if (reference is JsonArray array)
        {
            var actual = result!.AsArray();
            if (path == "$.Data.AddedSaleItems")
            {
                Check(array.Count == 1 && actual.Count > 0, "Empty sale items.");
                for (int i = 0; i < actual.Count; i++) Compare(array[0], actual[i], path + "[" + i + "]");
            }
            else
            {
                Check(array.Count == actual.Count, "Array count mismatch at " + path);
                for (int i = 0; i < array.Count; i++) Compare(array[i], actual[i], path + "[" + i + "]");
            }
        }
        else if (!Dynamic(path)) Check(JsonNode.DeepEquals(reference, result), "Fixed reference value mismatch at " + path);
    }

    private static JsonObject Item(string name, decimal quantity, decimal unit, decimal rate, bool taxGroup = false)
    {
        var result = new JsonObject
        {
            ["Name"] = name, ["ReservedText"] = "BARCODE-" + name,
            ["ItemQuantity"] = quantity, ["UnitPriceAmount"] = unit,
            ["TotalPriceAmount"] = decimal.Round(quantity * unit, 2, MidpointRounding.AwayFromZero)
        };
        if (taxGroup) result["TaxGroupCode"] = "KDV" + rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else result["VATRate"] = rate;
        return result;
    }
    private static JsonObject Request(Func<JsonObject> sale, int kind, params JsonObject[] items)
    {
        var request = sale();
        // Isolate these test operations from earlier deliberately pending challenges.
        request["TransactionHandle"]!["Fingerprint"] = "SENTETIK-TEMPLATES-" + Guid.NewGuid().ToString("N");
        var body = request["Sale"]!.AsObject();
        decimal total = items.Sum(i => Decimal(i["TotalPriceAmount"]));
        body["TotalPrice"] = total;
        body["GrossPrice"] = total;
        body["PaymentInformations"] = new JsonArray(new JsonObject { ["Mediator"] = kind, ["Amount"] = total });
        body["AddedSaleItems"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray());
        return request;
    }

    public static async Task Run(string project, Func<JsonObject> sale, Func<string, Task> configure,
        Func<JsonObject, Task<HttpResponseMessage>> post, Func<Task<JsonNode>> state,
        Func<string, Func<Task>, Task> test)
    {
        var cash = JsonNode.Parse(File.ReadAllText(Path.Combine(project, "References", "nakit_00SYN002_ODEME_gelen_154509.txt")))!;
        var card = JsonNode.Parse(File.ReadAllText(Path.Combine(project, "References", "kk_00SYN01_ODEME_gelen_130518.txt")))!;
        // No raw reference values are put in test artifacts, logs or exception text.
        async Task Verify(JsonObject request, int kind, bool repeat = false)
        {
            await configure("Success");
            int before = (await state())["captureCount"]!.GetValue<int>();
            using var response = await post(request);
            Check(response.IsSuccessStatusCode, "Success request rejected.");
            Check(response.Headers.GetValues("X-Simulator").Single() == "SENTETIK", "Synthetic header missing.");
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Compare(kind == 1 ? cash : card, reply);
            var sent = request["TransactionHandle"]!;
            var handle = reply["TransactionHandle"]!;
            foreach (string key in new[] { "SerialNumber", "TransactionDate", "TransactionSequence" })
                Check(JsonNode.DeepEquals(sent[key], handle[key]), "Handle value mismatch.");
            Check(JsonNode.DeepEquals(sent["Fingerprint"], handle["FingerPrint"]) && handle["Fingerprint"] is null, "FingerPrint casing mismatch.");
            var data = reply["Data"]!;
            var payment = data["AddedPayments"]![0]!;
            var detail = payment[kind == 1 ? "CashPayment" : "OnlinePayment"]!;
            Check(payment["PaymentTypeId"]!.GetValue<int>() == kind && payment[kind == 1 ? "OnlinePayment" : "CashPayment"] is null, "Payment template not separated.");
            Check(Decimal(detail["GivenAmount"]) == (kind == 1 ? Decimal(request["Sale"]!["TotalPrice"]) : 0m), "GivenAmount mismatch.");
            if (kind == 2)
            {
                Check(detail["ResponseCode"]!.GetValue<string>() == "00" && detail["CardNo"]!.GetValue<string>() == "000000******0000" &&
                    detail["AcquirerName"]!.GetValue<string>() == "SENTETIK_BANKA", "Card fields must be synthetic.");
            }
            Check(JsonNode.DeepEquals(data["Id"], payment["SaleId"]) && JsonNode.DeepEquals(data["Id"], detail["SaleId"]) &&
                JsonNode.DeepEquals(payment["Id"], detail["PaymentId"]), "Payment IDs inconsistent.");
            var inputs = request["Sale"]!["AddedSaleItems"]!.AsArray();
            var outputs = data["AddedSaleItems"]!.AsArray();
            Check(inputs.Count == outputs.Count, "Sale item lost.");
            for (int i = 0; i < inputs.Count; i++)
            {
                foreach (string key in new[] { "Name", "ItemQuantity", "UnitPriceAmount", "TotalPriceAmount", "ReservedText" })
                    Check(JsonNode.DeepEquals(inputs[i]![key], outputs[i]![key]), "Item field mismatch: " + key);
                decimal rate = inputs[i]!["VATRate"] is { } r ? Decimal(r) :
                    decimal.Parse(inputs[i]!["TaxGroupCode"]!.GetValue<string>()[3..], System.Globalization.CultureInfo.InvariantCulture);
                decimal expectedVat = decimal.Round(Decimal(inputs[i]!["TotalPriceAmount"]) * rate / (100m + rate), 2, MidpointRounding.AwayFromZero);
                Check(Decimal(outputs[i]!["VATRate"]) == rate && Decimal(outputs[i]!["VATAmount"]) == expectedVat, "VAT calculation mismatch.");
            }
            Check(outputs.Sum(i => Decimal(i!["TotalPriceAmount"])) == Decimal(request["Sale"]!["TotalPrice"]), "Sale total mismatch.");
            Check((await state())["captureCount"]!.GetValue<int>() == before + 1, "Capture count mismatch.");
            if (repeat)
            {
                using var replay = await post(request);
                var second = JsonNode.Parse(await replay.Content.ReadAsStringAsync())!;
                Check(JsonNode.DeepEquals(reply, second) && (await state())["captureCount"]!.GetValue<int>() == before + 1, "Replay changed IDs or captured twice.");
            }
        }
        foreach (int method in new[] { 1, 2 })
        {
            int kind = method;
            await test("real template: single item " + (kind == 1 ? "cash" : "card") + " exact schema/nulls/replay", () =>
                Verify(Request(sale, kind, Item("ONE", 1m, 115m, 15m)), kind, true));
            await test("real template: multi-item " + (kind == 1 ? "cash" : "card") + " mixed rates and quantities", () =>
                Verify(Request(sale, kind, Item("TWO", 2m, 2.75m, 10m), Item("FRACTION", 1.5m, 12.99m, 7.5m),
                    Item("ZERO_VAT", 3m, 0.50m, 0m), Item("TAX_GROUP", 1m, 115m, 15m, true)), kind));
            foreach (decimal amount in new[] { 0.50m, 2.75m, 12.99m, 115.00m, 1234.56m })
            {
                decimal value = amount;
                await test("real template: method " + kind + " amount " + value.ToString(System.Globalization.CultureInfo.InvariantCulture), () =>
                    Verify(Request(sale, kind, Item("AMOUNT", 1m, value, 20m)), kind));
            }
        }
        foreach (string defect in new[] { "MISSING_VAT", "CONFLICTING_VAT", "EMPTY_ITEMS", "TOTAL", "QUANTITY", "MEDIATOR" })
        {
            string current = defect;
            await test("real template: invalid " + current + " never captured", async () =>
            {
                await configure("Success");
                var request = Request(sale, 2, Item("INVALID", 1m, 12.99m, 8m));
                var item = request["Sale"]!["AddedSaleItems"]![0]!.AsObject();
                switch (current)
                {
                    case "MISSING_VAT": item.Remove("VATRate"); break;
                    case "CONFLICTING_VAT": item["TaxGroupCode"] = "KDV20"; break;
                    case "EMPTY_ITEMS": request["Sale"]!["AddedSaleItems"] = new JsonArray(); break;
                    case "TOTAL": request["Sale"]!["TotalPrice"] = 99m; request["Sale"]!["PaymentInformations"]![0]!["Amount"] = 99m; break;
                    case "QUANTITY": item["ItemQuantity"] = 2m; break;
                    case "MEDIATOR": request["Sale"]!["PaymentInformations"]![0]!["Mediator"] = 99; break;
                }
                int before = (await state())["captureCount"]!.GetValue<int>();
                using var response = await post(request);
                Check(response.StatusCode == HttpStatusCode.BadRequest && (await state())["captureCount"]!.GetValue<int>() == before,
                    "Invalid request accepted or captured.");
            });
        }
    }
}