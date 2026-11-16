using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TpPavoSimulator;

public static class CorrectionWire
{
    public static bool IsCorrection(Scenario scenario) => scenario is Scenario.Correction72 or Scenario.Correction73;
    public static string DeviceHash(JsonElement root)
    {
        var handle = Wire.Field(root, "TransactionHandle");
        return Wire.Hash(JsonSerializer.Serialize(new[] { Wire.Text(handle, "SerialNumber"), Wire.Text(handle, "Fingerprint") }));
    }
    public static string Date(JsonElement root)
    {
        string value = Wire.Text(Wire.Field(root, "TransactionHandle"), "TransactionDate");
        return Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})?$",
            RegexOptions.CultureInvariant) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
                ? value : "[INVALID_DATE]";
    }
    public static int Sequence(JsonElement root) => Wire.Field(Wire.Field(root, "TransactionHandle"), "TransactionSequence").GetInt32();
    public static object Handle(JsonElement root, string date, int sequence)
    {
        var handle = Wire.Field(root, "TransactionHandle");
        return new
        {
            SerialNumber = Wire.Text(handle, "SerialNumber"), Fingerprint = Wire.Text(handle, "Fingerprint"),
            TransactionDate = date, TransactionSequence = sequence
        };
    }
    public static string CorrectedKey(JsonElement root, string date, int sequence)
    {
        var handle = Wire.Field(root, "TransactionHandle");
        return Wire.Hash(JsonSerializer.Serialize(new[] { Wire.Text(handle, "SerialNumber"), Wire.Text(handle, "Fingerprint"),
            date, sequence.ToString(CultureInfo.InvariantCulture) }));
    }
    public static string Hash(JsonElement value)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory)) { Write(writer, value); }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(memory.ToArray()));
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.TryGetDecimal(out decimal number)
                    ? number.ToString("G29", CultureInfo.InvariantCulture) : value.GetRawText()); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            default: writer.WriteNullValue(); break;
        }
    }
}

public sealed partial class SimulatorState
{
    private List<CorrectionRecord> corrections = [];

    // The two allowed handle aliases resolve to one persistent logical payment.
    // While a correction is pending, an unknown handle from the SAME hashed
    // device is treated as a failed retry, never as a new successful payment.
    public PaymentAttempt Begin(JsonElement root, decimal amount)
    {
        lock (sync)
        {
            string key = Wire.TransactionId(root);
            string device = CorrectionWire.DeviceHash(root);
            var challenge = corrections.FirstOrDefault(c => c.OriginalHandleHash == key || c.CorrectedHandleHash == key);
            if (challenge == null)
            {
                var pending = corrections.Where(c => c.DeviceHash == device && !c.Completed).ToArray();
                if (pending.Length > 1) throw new InvalidOperationException("Ambiguous pending correction; refusing capture.");
                challenge = pending.SingleOrDefault();
            }
            if (challenge != null) return CheckCorrection(root, amount, challenge);
            // Preserve existing correction/amount-conflict decisions before validation.
            // Never persist a new virtual capture if a complete template cannot be built.
            if (!CorrectionWire.IsCorrection(options.Scenario) && payments.Any(p => p.TransactionId == key && p.Amount != amount))
                return Begin(key, amount);
            try { SuccessResponse.Validate(root, amount); }
            catch (ArgumentException) { InvalidSale(); throw; }
            if (!CorrectionWire.IsCorrection(options.Scenario)) return Begin(key, amount);

            // Never reuse an ordinary payment as a new correction challenge.
            var oldPayment = payments.FirstOrDefault(p => p.TransactionId == key);
            if (oldPayment != null)
            {
                oldPayment.RequestCount++;
                oldPayment.Result = "CORRECTION_REQUIRES_NEW_TEST_TRANSACTION";
                Save(new SafeEvent(DateTimeOffset.UtcNow, "CORRECTION_VALIDATION_FAILED", key,
                    oldPayment.SaleNumber, amount, options.Scenario, oldPayment.RequestCount, oldPayment.Result));
                return new PaymentAttempt(oldPayment with { }, options.Scenario, true, true, options.DelayMilliseconds,
                    new CorrectionDecision(9104, oldPayment.Result, CorrectionWire.Date(root),
                        CorrectionWire.Sequence(root), false, 409));
            }
            string firstDate = CorrectionWire.Date(root);
            if (firstDate == "[INVALID_DATE]")
            {
                InvalidSale();
                throw new ArgumentException("Correction scenario requires an ISO date.");
            }
            int firstSequence = CorrectionWire.Sequence(root);
            int number = payments.Count + 1;
            string expectedDate = options.Scenario == Scenario.Correction72
                ? new DateTime(2035, 1, 1, 0, 0, 0).AddSeconds(number).ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture)
                : firstDate;
            if (options.Scenario == Scenario.Correction72 && expectedDate == firstDate)
                expectedDate = new DateTime(2036, 1, 1).ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            int expectedSequence = options.Scenario == Scenario.Correction73
                ? firstSequence == int.MaxValue ? 1 : firstSequence + 1 : firstSequence;
            string expectedKey = CorrectionWire.CorrectedKey(root, expectedDate, expectedSequence);
            if (payments.Any(p => p.TransactionId == expectedKey) ||
                corrections.Any(c => c.OriginalHandleHash == expectedKey || c.CorrectedHandleHash == expectedKey))
                throw new InvalidOperationException("Correction alias collision; refusing capture.");

            var sale = Wire.Field(root, "Sale");
            var items = Wire.Field(sale, "AddedSaleItems");
            challenge = new CorrectionRecord
            {
                LogicalTransactionId = key, DeviceHash = device, OriginalHandleHash = key, CorrectedHandleHash = expectedKey,
                Scenario = options.Scenario, ErrorCode = options.Scenario == Scenario.Correction72 ? 72 : 73,
                OriginalDate = firstDate, OriginalSequence = firstSequence, ExpectedDate = expectedDate, ExpectedSequence = expectedSequence,
                SaleHash = CorrectionWire.Hash(sale), ItemsHash = CorrectionWire.Hash(items),
                ItemCount = items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0
            };
            var payment = new PaymentRecord
            {
                Time = DateTimeOffset.UtcNow, TransactionId = key, Amount = amount, SaleNumber = $"SIM-SALE-{number:D8}",
                Number = number, Scenario = options.Scenario, RequestCount = 1, Result = "CORRECTION_REQUIRED"
            };
            challenge.Steps.Add(new CorrectionStep(1, "FIRST_REQUEST_ERROR_RESPONSE", firstDate, firstSequence,
                expectedDate, expectedSequence, false, true, "CORRECTION_REQUIRED"));
            corrections.Add(challenge);
            payments.Add(payment);
            Save(new SafeEvent(DateTimeOffset.UtcNow, "CORRECTION_REQUIRED", key, payment.SaleNumber,
                amount, challenge.Scenario, 1, payment.Result));
            return new PaymentAttempt(payment with { }, challenge.Scenario, false, false, options.DelayMilliseconds,
                new CorrectionDecision(challenge.ErrorCode, payment.Result, expectedDate, expectedSequence, false));
        }
    }

    private PaymentAttempt CheckCorrection(JsonElement root, decimal amount, CorrectionRecord challenge)
    {
        var payment = payments.Single(p => p.TransactionId == challenge.LogicalTransactionId);
        var sale = Wire.Field(root, "Sale");
        var items = Wire.Field(sale, "AddedSaleItems");
        bool payloadMatches = amount == payment.Amount && CorrectionWire.Hash(sale) == challenge.SaleHash &&
            CorrectionWire.Hash(items) == challenge.ItemsHash;
        string requestDate = CorrectionWire.Date(root);
        int requestSequence = CorrectionWire.Sequence(root);
        bool handleMatches = Wire.TransactionId(root) == challenge.CorrectedHandleHash &&
            requestDate == challenge.ExpectedDate && requestSequence == challenge.ExpectedSequence;
        payment.RequestCount++;
        bool accepted = payloadMatches && handleMatches;
        string result = !payloadMatches ? "CORRECTION_PAYLOAD_CHANGED" :
            !handleMatches ? "CORRECTION_HANDLE_NOT_FIXED" :
            payment.Captured ? "CORRECTION_VERIFIED_REPLAY" : "CORRECTION_VERIFIED_CAPTURED";
        if (accepted)
        {
            payment.Captured = true;
            challenge.Completed = true;
        }
        payment.Result = result;
        challenge.Steps.Add(new CorrectionStep(payment.RequestCount, "RETRY_VALIDATION", requestDate, requestSequence,
            challenge.ExpectedDate, challenge.ExpectedSequence, handleMatches, payloadMatches, result));
        Save(new SafeEvent(DateTimeOffset.UtcNow, accepted ? "CORRECTION_VERIFIED" : "CORRECTION_VALIDATION_FAILED",
            payment.TransactionId, payment.SaleNumber, amount, challenge.Scenario, payment.RequestCount, result));
        return new PaymentAttempt(payment with { }, accepted ? Scenario.Success : challenge.Scenario,
            true, !payloadMatches, options.DelayMilliseconds,
            new CorrectionDecision(accepted ? 0 : payloadMatches ? challenge.ErrorCode : 9104, result,
                challenge.ExpectedDate, challenge.ExpectedSequence, accepted, !payloadMatches ? 409 : 200));
    }
}
