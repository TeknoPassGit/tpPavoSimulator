using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TpPavoSimulator;

public sealed partial class SimulatorState
{
    private readonly object sync = new();
    private readonly string statePath;
    private readonly string logPath;
    private readonly string settingsPath;
    private List<PaymentRecord> payments = [];

    private readonly Queue<SafeEvent> recent = new();
    private bool writesBlocked;
    private SimulatorOptions options = new();

    public SimulatorState(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        statePath = Path.Combine(root, "data", "SYNTHETIC-state.json");
        logPath = Path.Combine(root, "logs", "SYNTHETIC-events.jsonl");
        settingsPath = Path.Combine(root, "data", "SYNTHETIC-settings.json");
        options = LoadSettings(options);
        if (File.Exists(statePath))
        {
            // Accept the removed legacy field without resetting PAVO ownership,
            // captures or pending correction aliases. It is never saved again.
            var document = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
            foreach (string name in document.Select(p => p.Key).Where(n => n.Equals("tickets", StringComparison.OrdinalIgnoreCase)).ToArray())
                document.Remove(name);
            var saved = document.Deserialize<SavedState>(Wire.Json)
                ?? throw new InvalidDataException("Synthetic state cannot be read; refusing to reset it.");
            payments = saved.Payments;

            corrections = saved.Corrections ?? [];
        }
    }
    public SimulatorOptions Options { get { lock (sync) return options with { }; } }
    public void Configure(SimulatorOptions value)
    {
        if (!Enum.IsDefined(value.Scenario) || !Enum.IsDefined(value.RepeatPolicy) || value.DelayMilliseconds is < 0 or > 180000 ||
            value.SerialNumber is { Length: > 256 })
            throw new ArgumentException("Invalid simulator options.");
        string? serial = value.SerialNumber?.Trim();
        // Keep legacy names readable for existing clients/records, but they no
        // longer select or constrain a payment method.
        lock (sync)
        {
            serial ??= options.SerialNumber ?? "";
            if (serial != (options.SerialNumber ?? "") || value.DelayMilliseconds != options.DelayMilliseconds)
                SaveSettings(serial, value.DelayMilliseconds);
            options = value with
            {
                Scenario = value.Scenario is Scenario.SuccessCash or Scenario.SuccessCard ? Scenario.Success : value.Scenario,
                SerialNumber = serial
            };
        }
    }
    /// <summary>Gelen seri numarası ayarlı değerden farklıysa true. Ayar boşsa kontrol yapılmaz.</summary>
    public bool SerialMismatch(string incoming)
    {
        string expected = Options.SerialNumber ?? "";
        return expected.Length > 0 && !string.Equals(incoming.Trim(), expected, StringComparison.Ordinal);
    }
    // Seri numarası ve yanıt gecikmesi yeniden başlatmada hatırlanır; senaryo ve tekrar politikası varsayılana döner.
    private SimulatorOptions LoadSettings(SimulatorOptions defaults)
    {
        string serial = "";
        int delay = defaults.DelayMilliseconds;
        try
        {
            if (!File.Exists(settingsPath)) return defaults with { SerialNumber = serial };
            var node = JsonNode.Parse(File.ReadAllText(settingsPath));
            string value = node?["serialNumber"]?.GetValue<string>()?.Trim() ?? "";
            if (value.Length <= 256) serial = value;
            if (node?["delayMilliseconds"] is JsonValue d && d.TryGetValue(out int saved) && saved is >= 0 and <= 180000)
                delay = saved;
        }
        catch { }
        return defaults with { SerialNumber = serial, DelayMilliseconds = delay };
    }
    private void SaveSettings(string serial, int delayMilliseconds)
    {
        string temporary = settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { serialNumber = serial, delayMilliseconds }, Wire.Json), Encoding.UTF8);
        File.Move(temporary, settingsPath, true);
    }
    public object Snapshot()
    {
        lock (sync) return new
        {
            synthetic = true, options, writesBlocked, lastHttp,
            payments = payments.Select(p => p with { }).ToArray(),
            events = recent.ToArray(),
            corrections = corrections.Select(c => c with { Steps = c.Steps.ToList() }).ToArray(),
            captureCount = payments.Count(p => p.Captured), totalAmount = payments.Where(p => p.Captured).Sum(p => p.Amount)
        };
    }
    private void Save(SafeEvent item)
    {
        if (writesBlocked) throw new IOException("Synthetic storage blocked.");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedState(payments, corrections), Wire.Json);
            string temporary = statePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, statePath, true);
            File.AppendAllText(logPath, JsonSerializer.Serialize(item, Wire.Json) + Environment.NewLine, Encoding.UTF8);
            recent.Enqueue(item);
            while (recent.Count > 100) recent.Dequeue();
        }
        catch { writesBlocked = true; throw new IOException("Synthetic storage blocked."); }
    }
    public PaymentAttempt Begin(string transactionId, decimal amount)
    {
        lock (sync)
        {
            var entry = payments.FirstOrDefault(p => p.TransactionId == transactionId);
            bool duplicate = entry != null;
            bool conflict = duplicate && entry!.Amount != amount;
            var scenario = options.Scenario;
            if (entry == null)
            {
                int number = payments.Count + 1;
                entry = new PaymentRecord
                {
                    Time = DateTimeOffset.UtcNow, TransactionId = transactionId, Amount = amount,
                    SaleNumber = $"SIM-SALE-{number:D8}", Number = number, Scenario = scenario, Result = "RECEIVED"
                };
                payments.Add(entry);
            }
            else if (options.RepeatPolicy == RepeatPolicy.ReplaySavedOutcome)
                scenario = entry.Captured ? Scenario.Success : entry.Scenario;
            entry.RequestCount++;
            entry.Scenario = scenario;
            if (conflict) entry.Result = "AMOUNT_CONFLICT";
            else if (ScenarioRules.Captures(scenario))
            {
                entry.Captured = true; // a single boolean capture per unique transaction, never a second charge
                entry.Result = scenario == Scenario.LostResponse ? "CAPTURED_RESPONSE_LOST" :
                    scenario == Scenario.Delayed ? "CAPTURED_RESPONSE_DELAYED" : "CAPTURED";
            }
            else entry.Result = scenario switch
            {
                Scenario.Timeout => entry.Captured ? "NO_RESPONSE_ALREADY_CAPTURED" : "NO_RESPONSE_NOT_CAPTURED",
                Scenario.Malformed => entry.Captured ? "MALFORMED_RESPONSE_ALREADY_CAPTURED" : "MALFORMED_RESPONSE_NOT_CAPTURED",
                _ => entry.Captured ? "ERROR_RESPONSE_ALREADY_CAPTURED" : "ERROR_RESPONSE"
            };
            Save(new SafeEvent(DateTimeOffset.UtcNow, duplicate ? "REPEAT_ATTEMPT" : "COMPLETE_SALE",
                transactionId, entry.SaleNumber, amount, scenario, entry.RequestCount, entry.Result));
            return new PaymentAttempt(entry with { }, scenario, duplicate, conflict, options.DelayMilliseconds);
        }
    }
    public void Pairing(System.Text.Json.JsonElement request) { lock(sync) Save(new SafeEvent(DateTimeOffset.UtcNow, "PAIRING", Wire.TransactionId(request), "", null, options.Scenario, 1, "PAIRED")); }
    public void InvalidSale()
    {
        lock (sync) Save(new SafeEvent(DateTimeOffset.UtcNow, "COMPLETE_SALE_INVALID",
            Guid.NewGuid().ToString("N"), "", null, options.Scenario, 1, "INVALID_REQUEST"));
    }
    public object[] Pending()
    {
        lock (sync) return payments.Where(p => p.Captured && !p.Finalized).Select(p => (object)new
            { Id = p.Number, p.SaleNumber, TotalPrice = p.Amount, StatusId = 4 }).ToArray();
    }
    public bool FinalizeSale(string number)
    {
        lock (sync)
        {
            var entry = payments.FirstOrDefault(p => p.SaleNumber == number && p.Captured);
            if (entry == null) return false;
            entry.Finalized = true;
            Save(new SafeEvent(DateTimeOffset.UtcNow, "FINALIZE_SALE", entry.TransactionId,
                entry.SaleNumber, entry.Amount, entry.Scenario, entry.RequestCount, "FINALIZED"));
            return true;
        }
    }
    public int CapturedCount { get { lock (sync) return payments.Count(p => p.Captured); } }
    public decimal CapturedTotal { get { lock (sync) return payments.Where(p => p.Captured).Sum(p => p.Amount); } }
}
