using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TpPavoSimulator;

// These separate resources are sanitized clones of the two supplied real replies.
// Never copy request card/customer data or load the raw reference files at runtime.
public static class SuccessResponse
{
    private static readonly JsonObject Cash = Load("cash-success.json");
    private static readonly JsonObject Card = Load("card-success.json");
    private static readonly JsonSerializerOptions ExactCase = new() { PropertyNamingPolicy = null };

    private static JsonObject Load(string name)
    {
        using var stream = typeof(SuccessResponse).Assembly.GetManifestResourceStream("TpPavoSimulator.Templates." + name)
            ?? throw new InvalidOperationException("Missing sanitized success template.");
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static decimal Number(JsonElement value, string name)
    {
        if (!Wire.Number(value, name, out decimal number)) throw new ArgumentException("Missing numeric sale field.");
        return number;
    }
    private static string RequiredText(JsonElement value, string name)
    {
        var field = Wire.Field(value, name);
        if (field.ValueKind != JsonValueKind.String) throw new ArgumentException("Missing text sale field.");
        return field.GetString()!;
    }
    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal VatRate(JsonElement item)
    {
        var explicitRate = Wire.Field(item, "VATRate");
        string group = Wire.Text(item, "TaxGroupCode");
        decimal? groupRate = null;
        // TaxGroupCode is sent by the existing client. Only an explicit numeric
        // KDV suffix is interpreted; no default rate or opaque-code lookup exists.
        if (Regex.IsMatch(group, @"^KDV[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant) &&
            decimal.TryParse(group.AsSpan(3), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsed))
            groupRate = parsed;
        decimal rate;
        if (explicitRate.ValueKind != JsonValueKind.Undefined)
        {
            rate = Number(item, "VATRate");
            if (groupRate.HasValue && rate != groupRate.Value) throw new ArgumentException("Conflicting VAT rates.");
        }
        else rate = groupRate ?? throw new ArgumentException("VAT rate must be provided.");
        if (rate < 0 || rate > 100) throw new ArgumentException("Unsupported VAT rate.");
        return rate;
    }

    private static (int Kind, JsonArray Items) ReadSale(JsonElement root, decimal amount)
    {
        var sale = Wire.Field(root, "Sale");
        var payments = Wire.Field(sale, "PaymentInformations");
        if (payments.ValueKind != JsonValueKind.Array || payments.GetArrayLength() != 1 ||
            !Wire.Field(payments[0], "Mediator").TryGetInt32Safe(out int kind) || kind is not (1 or 2) ||
            Number(payments[0], "Amount") != amount || amount <= 0 || Money(amount) != amount)
            throw new ArgumentException("Unsupported payment information.");
        var input = Wire.Field(sale, "AddedSaleItems");
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0)
            throw new ArgumentException("At least one sale item is required.");
        var prototype = (kind == 1 ? Cash : Card)["Data"]!["AddedSaleItems"]![0]!;
        var result = new JsonArray();
        decimal sum = 0;
        foreach (var item in input.EnumerateArray())
        {
            decimal quantity = Number(item, "ItemQuantity"), unit = Number(item, "UnitPriceAmount"),
                total = Number(item, "TotalPriceAmount"), rate = VatRate(item);
            if (quantity <= 0 || unit < 0 || total < 0 || Money(total) != total || Money(quantity * unit) != total)
                throw new ArgumentException("Inconsistent sale item amounts.");
            var output = prototype.DeepClone().AsObject();
            output["Name"] = RequiredText(item, "Name");
            output["ReservedText"] = RequiredText(item, "ReservedText");
            output["ItemQuantity"] = quantity;
            output["UnitPriceAmount"] = unit;
            output["TotalPriceAmount"] = total;
            output["VATRate"] = rate;
            output["VATAmount"] = Money(total * rate / (100m + rate));
            result.Add(output);
            sum += total;
        }
        if (sum != amount) throw new ArgumentException("Sale total differs from item total.");
        return (kind, result);
    }

    public static void Validate(JsonElement root, decimal amount) => ReadSale(root, amount);

    public static IResult Create(JsonElement root, PaymentRecord payment)
    {
        var (kind, items) = ReadSale(root, payment.Amount);
        var reply = (kind == 1 ? Cash : Card).DeepClone().AsObject();
        var handle = reply["TransactionHandle"]!.AsObject();
        var requestHandle = Wire.Field(root, "TransactionHandle");
        handle["SerialNumber"] = Wire.Text(requestHandle, "SerialNumber");
        handle["FingerPrint"] = Wire.Text(requestHandle, "Fingerprint");
        handle["TransactionDate"] = Wire.Text(requestHandle, "TransactionDate");
        handle["TransactionSequence"] = Wire.Field(requestHandle, "TransactionSequence").GetInt32();
        var data = reply["Data"]!.AsObject();
        data["Id"] = payment.Number;
        data["SaleNumber"] = payment.SaleNumber;
        var added = data["AddedPayments"]![0]!.AsObject();
        added["Id"] = payment.Number;
        added["SaleId"] = payment.Number;
        var detail = added[kind == 1 ? "CashPayment" : "OnlinePayment"]!.AsObject();
        detail["Id"] = payment.Number;
        detail["PaymentId"] = payment.Number;
        detail["SaleId"] = payment.Number;
        // The real card sample reports zero here, independently of the sale total.
        detail["GivenAmount"] = kind == 1 ? payment.Amount : 0.0m;
        for (int i = 0; i < items.Count; i++) items[i]!["Id"] = (long)payment.Number * 1000 + i + 1;
        data["AddedSaleItems"] = items;
        var document = data["FinancialDocuments"]![0]!.AsObject();
        document["Id"] = payment.Number;
        document["InvoiceNo"] = $"SIM-INVOICE-{payment.Number:D8}";
        document["DocumentNo"] = $"SIM-DOC-{payment.Number:D8}";
        data["SaleInquieryLink"] = $"http://127.0.0.1:18080/sim/receipt/{payment.Number}";
        return Results.Json(reply, ExactCase);
    }
}