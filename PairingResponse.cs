using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TpPavoSimulator;

/// <summary>
/// Gerçek Pairing yanıtının şekli (References/PAIR_gelen_*.txt): ErrorCode ve Data alanı yoktur, Message boştur.
/// Cihaz istemcinin Fingerprint değerini değil kendi cihaz fingerprint'ini döner, sırayı bir artırır ve kendi saatini yazar.
/// Fingerprint gerçek biçimde ama sentetik bir değerdir; gerçek cihaz kimliği kullanılmaz.
/// </summary>
public static class PairingResponse
{
    private static readonly JsonObject Template = Load();
    private static JsonObject Load()
    {
        using var stream = typeof(PairingResponse).Assembly.GetManifestResourceStream("TpPavoSimulator.Templates.pairing-success.json")
            ?? throw new InvalidOperationException("Missing pairing template.");
        return JsonNode.Parse(stream)!.AsObject();
    }
    public static IResult Create(JsonElement root, DateTimeOffset now)
    {
        var reply = Template.DeepClone().AsObject();
        var handle = reply["TransactionHandle"]!.AsObject();
        var sent = Wire.Field(root, "TransactionHandle");
        handle["SerialNumber"] = Wire.Text(sent, "SerialNumber");
        int sequence = Wire.Field(sent, "TransactionSequence").GetInt32();
        handle["TransactionSequence"] = sequence == int.MaxValue ? sequence : sequence + 1;
        handle["TransactionDate"] = now.LocalDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return Results.Json(reply, new JsonSerializerOptions { PropertyNamingPolicy = null });
    }
}
