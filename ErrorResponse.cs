using System.Text.Json;
using System.Text.Json.Nodes;

namespace TpPavoSimulator;

public static class ErrorResponse
{
    private static readonly Dictionary<int, JsonObject> Templates = new[] { 20, 73, 119, 138, 199, 9001 }
        .ToDictionary(code => code, code => Load(code));
    private static JsonObject Load(int code)
    {
        using var stream = typeof(ErrorResponse).Assembly.GetManifestResourceStream($"TpPavoSimulator.Templates.error-{code}.json")
            ?? throw new InvalidOperationException("Missing sanitized error template.");
        return JsonNode.Parse(stream)!.AsObject();
    }
    public static IResult Create(JsonElement root, int code, string? date = null, int? sequence = null, int status = 200)
    {
        var reply = Templates[code].DeepClone().AsObject();
        var handle = reply["TransactionHandle"]!.AsObject();
        var sent = Wire.Field(root, "TransactionHandle");
        handle["SerialNumber"] = Wire.Text(sent, "SerialNumber");
        // Both spellings occur in the real samples. Keep this template's spelling.
        string fingerprint = handle.ContainsKey("FingerPrint") ? "FingerPrint" : "Fingerprint";
        handle[fingerprint] = Wire.Text(sent, "Fingerprint");
        handle["TransactionDate"] = date ?? Wire.Text(sent, "TransactionDate");
        handle["TransactionSequence"] = sequence ?? Wire.Field(sent, "TransactionSequence").GetInt32();
        return Results.Json(reply, new JsonSerializerOptions { PropertyNamingPolicy = null }, statusCode: status);
    }
}

public static class ScenarioRules
{
    public static bool Captures(Scenario scenario) => scenario is Scenario.Success or Scenario.SuccessCash or
        Scenario.SuccessCard or Scenario.Duplicate or Scenario.Delayed or Scenario.LostResponse;
}