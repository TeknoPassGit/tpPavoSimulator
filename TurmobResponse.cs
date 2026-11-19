using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TpPavoSimulator;

/// <summary>
/// POST /GetTurmobInfo. Yanıt şekli PAVO dokümanındaki örnekten (References/TURMOB_dokuman_ornegi.txt) birebir alınır:
/// ErrorCode ve Message null, TurmobInfo'nun 17 alanı string. Dokümanda adres alanı yoktur, eklenmez.
/// Değerler sentetiktir: gerçek TÜRMOB sorgusu yapılmaz, gelen numara dışında kişi/şirket verisi üretilmez ve
/// gelen VKN/TCKN hiçbir yere kaydedilmez. Sıra numarası istekten aynen döner (doküman örneği: 3 -> 3),
/// tarih simülatör saatidir, Fingerprint cihazın kendi (sentetik) fingerprint'idir.
/// </summary>
public static class TurmobResponse
{
    public const int InvalidInputCode = 9104;

    private static readonly JsonObject Template = Load("turmob-success.json");
    private static readonly string DeviceFingerprint =
        Load("pairing-success.json")["TransactionHandle"]!["Fingerprint"]!.GetValue<string>();

    private static JsonObject Load(string name)
    {
        using var stream = typeof(TurmobResponse).Assembly.GetManifestResourceStream("TpPavoSimulator.Templates." + name)
            ?? throw new InvalidOperationException("Missing Turmob template.");
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static bool Digits(string value, int length) => value.Length == length && value.All(char.IsAsciiDigit);

    /// <summary>TurmobInput doğrulaması: Tckn (11 hane) veya Vkn (10 hane), yalnızca biri.</summary>
    public static bool TryReadInput(JsonElement root, out string kind, out string number)
    {
        kind = ""; number = "";
        var input = Wire.Field(root, "TurmobInput");
        if (input.ValueKind != JsonValueKind.Object) return false;
        string tckn = Wire.Text(input, "Tckn").Trim(), vkn = Wire.Text(input, "Vkn").Trim();
        if ((tckn.Length > 0) == (vkn.Length > 0)) return false;
        if (tckn.Length > 0 && Digits(tckn, 11)) { kind = "Tckn"; number = tckn; return true; }
        if (vkn.Length > 0 && Digits(vkn, 10)) { kind = "Vkn"; number = vkn; return true; }
        return false;
    }

    public static IResult Create(JsonElement root, string kind, string number, DateTimeOffset now)
    {
        var reply = Template.DeepClone().AsObject();

        var handle = reply["TransactionHandle"]!.AsObject();
        var sent = Wire.Field(root, "TransactionHandle");
        handle["SerialNumber"] = Wire.Text(sent, "SerialNumber");
        handle["Fingerprint"] = DeviceFingerprint;
        handle["TransactionSequence"] = Wire.Field(sent, "TransactionSequence").GetInt32();
        handle["TransactionDate"] = now.LocalDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

        var info = reply["Data"]!["TurmobInfo"]!.AsObject();
        string son4 = number[^4..];
        info["vergiDairesiAdi"] = "SENTETIK VERGI DAIRESI";
        info["vergiDairesiKodu"] = "999" + number[^3..];
        info["faalTerkDurumu"] = "SENTETIK";

        if (kind == "Vkn")
        {
            info["vkn"] = number;
            info["unvan"] = "SENTETIK TICARET " + son4 + " A.S.";
            info["kimlikUnvani"] = "SENTETIK TICARET " + son4 + " A.S.";
            info["sirketinTuru"] = "SENTETIK";
        }
        else
        {
            info["tckn"] = number;
            info["ad"] = "SENTETIK";
            info["soyad"] = "MUKELLEF" + son4;
            info["kimlikUnvani"] = "SENTETIK MUKELLEF" + son4;
        }

        return Results.Json(reply, new JsonSerializerOptions { PropertyNamingPolicy = null });
    }

    /// <summary>Doküman hata örneği vermez; geçersiz girişte simülatörün diğer test kodları gibi (9103) SENTETIK 9104.</summary>
    public static IResult InvalidInput(JsonElement root)
        => Wire.Envelope(root, null, InvalidInputCode,
            "SENTETIK: TurmobInput gecersiz; yalnizca biri: Tckn 11 hane veya Vkn 10 hane. 9104 test kodu.");
}
