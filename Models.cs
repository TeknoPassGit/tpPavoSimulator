using System.Text.Json;
using System.Text.Json.Serialization;

namespace TpPavoSimulator;

public enum Scenario { Success, Busy20, Rejected, Error72, Error73, Delayed, Timeout, Malformed, Duplicate, LostResponse, Correction72, Correction73, Error119, Error138, Error199, Error9001, SuccessCash, SuccessCard }
public enum RepeatPolicy { ReplaySavedOutcome, ApplySelectedScenario }

public sealed record SimulatorOptions
{
    public Scenario Scenario { get; init; } = Scenario.Success;
    /// <summary>Pairing dışındaki tüm PAVO isteklerine uygulanan yanıt gecikmesi.</summary>
    public int DelayMilliseconds { get; init; } = 0;
    /// <summary>Beklenen cihaz seri numarası. Boşsa kontrol yapılmaz; null gelirse mevcut değer korunur.</summary>
    public string? SerialNumber { get; init; }
    public RepeatPolicy RepeatPolicy { get; init; } = RepeatPolicy.ReplaySavedOutcome;
}

public sealed record PaymentRecord
{
    public DateTimeOffset Time { get; init; }
    public required string TransactionId { get; init; }
    public required string SaleNumber { get; init; }
    public decimal Amount { get; init; }
    public Scenario Scenario { get; set; }
    public int RequestCount { get; set; }
    public bool Captured { get; set; }
    public bool Finalized { get; set; }
    public required string Result { get; set; }
    public int Number { get; init; }
}
public sealed record SafeEvent(DateTimeOffset Time, string Event, string TransactionId,
    string SaleNumber, decimal? Amount, Scenario Scenario, int RequestCount, string Result);

public sealed record SavedState(List<PaymentRecord> Payments, List<CorrectionRecord>? Corrections = null);
public sealed record PaymentAttempt(PaymentRecord Payment, Scenario Scenario, bool Duplicate, bool AmountConflict, int DelayMilliseconds, CorrectionDecision? Correction = null)
{
    public Scenario SelectedScenario { get; init; }
    public RepeatPolicy RepeatPolicy { get; init; }
}

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static JsonElement Field(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) return default;
        foreach (var item in value.EnumerateObject())
            if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return item.Value;
        return default;
    }
    public static string Text(JsonElement value, string name)
    {
        var field = Field(value, name);
        return field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    }
    public static bool Number(JsonElement value, string name, out decimal amount)
    {
        var field = Field(value, name);
        amount = 0;
        return field.ValueKind == JsonValueKind.Number && field.TryGetDecimal(out amount);
    }
    public static bool ValidHandle(JsonElement root)
    {
        var handle = Field(root, "TransactionHandle");
        return Text(handle, "SerialNumber").Length is > 0 and <= 256 &&
            Text(handle, "Fingerprint").Length is > 0 and <= 256 &&
            Text(handle, "TransactionDate").Length is > 0 and <= 128 &&
            Field(handle, "TransactionSequence").TryGetInt32Safe(out _);
    }
    public static bool TryGetInt32Safe(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
    public static object Handle(JsonElement root)
    {
        var h = Field(root, "TransactionHandle");
        return new
        {
            SerialNumber = Text(h, "SerialNumber"), Fingerprint = Text(h, "Fingerprint"),
            TransactionDate = Text(h, "TransactionDate"),
            TransactionSequence = Field(h, "TransactionSequence").GetInt32()
        };
    }
    public static string TransactionId(JsonElement root)
    {
        var h = Field(root, "TransactionHandle");
        // No raw device identity, fingerprint, customer details or body is persisted.
        return Hash(JsonSerializer.Serialize(new[] { Text(h, "SerialNumber"), Text(h, "Fingerprint"),
            Text(h, "TransactionDate"), Field(h, "TransactionSequence").ToString() }));
    }
    public static string Hash(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public static IResult Envelope(JsonElement root, object? data, int error = 0, string message = "SENTETIK: yerel test yaniti", int status = 200, object? handleOverride = null)
        => Results.Json(new { HasError = error != 0, ErrorCode = error, Message = message,
            TransactionHandle = handleOverride ?? Handle(root), Data = data }, new JsonSerializerOptions { PropertyNamingPolicy = null }, statusCode: status);
    public static IResult Unsupported() => Results.Json(new
    {
        simulator = true, synthetic = true, error = "UNSUPPORTED",
        message = "SENTETIK: bu komutun dogrulanmis sozlesmesi uygulanmadi; yonlendirme yok."
    }, statusCode: 501);
}


// Simulator-only evidence, never additional official PAVO JSON fields.
public sealed record CorrectionStep(int Attempt, string Phase, string RequestDate, int RequestSequence,
    string ResponseDate, int ResponseSequence, bool HandleMatches, bool PayloadMatches, string Result);
public sealed record CorrectionDecision(int ErrorCode, string Outcome, string ResponseDate,
    int ResponseSequence, bool Accepted, int HttpStatus = 200);
public sealed record CorrectionRecord
{
    public required string LogicalTransactionId { get; init; }
    public required string DeviceHash { get; init; }
    public required string OriginalHandleHash { get; init; }
    public required string CorrectedHandleHash { get; init; }
    public Scenario Scenario { get; init; }
    public int ErrorCode { get; init; }
    public required string OriginalDate { get; init; }
    public int OriginalSequence { get; init; }
    public required string ExpectedDate { get; init; }
    public int ExpectedSequence { get; init; }
    public required string SaleHash { get; init; }
    public required string ItemsHash { get; init; }
    public int ItemCount { get; init; }
    public bool Completed { get; set; }
    public List<CorrectionStep> Steps { get; init; } = [];
}
