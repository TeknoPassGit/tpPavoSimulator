using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TpPavoSimulator;

public sealed record CapturedPayment(DateTimeOffset Time, int Number, decimal Amount);

// Sanitized clones of the supplied real PerformEOD, idle AbortCurrentPayment,
// UpdateTerminalData and RebootDevice replies. Raw references stay in References.
public static class DeviceCommandResponse
{
    private static readonly JsonObject Eod = Load("eod-success.json");
    private static readonly JsonObject AbortIdle = Load("abort-error-102.json");
    private static readonly JsonObject TerminalData = Load("update-terminal-data-success.json");
    private static readonly JsonObject Reboot = Load("reboot-device-success.json");
    private static readonly JsonSerializerOptions ExactCase = new() { PropertyNamingPolicy = null };
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static JsonObject Load(string name)
    {
        using var stream = typeof(DeviceCommandResponse).Assembly.GetManifestResourceStream("TpPavoSimulator.Templates." + name)
            ?? throw new InvalidOperationException("Missing sanitized device command template.");
        return JsonNode.Parse(stream)!.AsObject();
    }

    // Real replies keep the request sequence for these commands; the handle is echoed.
    private static JsonObject Prepare(JsonObject template, JsonElement root)
    {
        var reply = template.DeepClone().AsObject();
        var handle = reply["TransactionHandle"]!.AsObject();
        var sent = Wire.Field(root, "TransactionHandle");
        handle["SerialNumber"] = Wire.Text(sent, "SerialNumber");
        handle["Fingerprint"] = Wire.Text(sent, "Fingerprint");
        handle["TransactionSequence"] = Wire.Field(sent, "TransactionSequence").GetInt32();
        handle["TransactionDate"] = Wire.Text(sent, "TransactionDate");
        return reply;
    }

    public static IResult UpdateTerminalData(JsonElement root) => Results.Json(Prepare(TerminalData, root), ExactCase);
    public static IResult RebootDevice(JsonElement root) => Results.Json(Prepare(Reboot, root), ExactCase);
    public static IResult AbortWithoutActivePayment(JsonElement root) => Results.Json(Prepare(AbortIdle, root), ExactCase);

    private static string Money(decimal value) => value.ToString("N2", Tr);

    public static IResult PerformEod(JsonElement root, IReadOnlyList<CapturedPayment> captured, DateTimeOffset now)
    {
        var reply = Prepare(Eod, root);
        var eod = reply["Data"]!["eodData"]!.AsObject();
        eod["serialNo"] = Wire.Text(Wire.Field(root, "TransactionHandle"), "SerialNumber");
        eod["dateTime"] = now.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Inv);

        var acquirer = eod["acquirers"]![0]!.AsObject();
        var group = acquirer["cardTransactions"]![0]!.AsObject();
        var prototype = group["transactions"]![0]!;
        var transactions = new JsonArray();
        foreach (var payment in captured)
        {
            var local = payment.Time.ToLocalTime();
            var item = prototype.DeepClone().AsObject();
            item["transactionNo"] = payment.Number.ToString("D6", Inv);
            item["stan"] = payment.Number.ToString("D6", Inv);
            item["date"] = local.ToString("dd.MM.yyyy", Inv);
            item["time"] = local.ToString("HH:mm", Inv);
            item["dateTime"] = local.ToString("dd.MM.yyyy HH:mm", Inv);
            item["amount"] = Money(payment.Amount) + " TL";
            transactions.Add(item);
        }
        group["transactions"] = transactions;

        string count = captured.Count.ToString(Inv);
        string total = Money(captured.Sum(p => p.Amount));
        group["summaries"]![0]!["transactionCount"] = count;
        group["summaries"]![0]!["totalAmount"] = total;
        acquirer["hasCard"] = captured.Count > 0 ? "true" : "false";
        acquirer["cardTotals"]![0]!["transactionCount"] = count;
        acquirer["cardTotals"]![0]!["totalAmount"] = total + " TL";
        acquirer["acquirerTotalCount"] = count;
        eod["grandTotals"]![0]!["transactionCount"] = count;
        eod["grandTotals"]![0]!["totalAmount"] = total + " TL";
        return Results.Json(reply, ExactCase);
    }

    // Only Busy20 and Timeout apply to these commands; every other scenario answers normally.
    public static async Task<IResult?> ApplyScenario(HttpContext context, JsonElement root, Scenario scenario)
    {
        context.Response.Headers["X-Simulator-Selected-Scenario"] = scenario.ToString();
        switch (scenario)
        {
            case Scenario.Busy20:
                context.Response.Headers["X-Simulator-Used-Scenario"] = scenario.ToString();
                context.Response.Headers["X-Contract-Source"] = "OBSERVED_ERROR_SHAPE_TEST_IDENTITIES";
                return ErrorResponse.Create(root, 20);
            case Scenario.Timeout:
                context.Response.Headers["X-Simulator-Used-Scenario"] = scenario.ToString();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                return Results.Empty;
            default:
                context.Response.Headers["X-Simulator-Used-Scenario"] = Scenario.Success.ToString();
                return null;
        }
    }
}

public sealed partial class SimulatorState
{
    public CapturedPayment[] CapturedPayments()
    {
        lock (sync) return payments.Where(p => p.Captured).OrderBy(p => p.Number)
            .Select(p => new CapturedPayment(p.Time, p.Number, p.Amount)).ToArray();
    }
}
