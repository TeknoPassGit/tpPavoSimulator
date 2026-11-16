using System.Text;
using System.Text.Json;

namespace TpPavoSimulator;

public sealed record HttpObservation(long RequestId, DateTimeOffset Time, string SelectedScenario,
    string UsedScenario, string RepeatPolicy, bool Repeated, int? StatusCode, string? Body, string Note, int? PaymentTypeId = null);


public sealed partial class SimulatorState
{
    // Actual response text is volatile and bounded to the last CompleteSale only.
    // It is never added to SavedState, logs or the payment ledger.
    private HttpObservation? lastHttp;
    private long httpSequence;
    public PaymentAttempt Dispatch(JsonElement root, decimal amount)
    {
        lock (sync)
        {
            return Begin(root, amount) with { SelectedScenario = options.Scenario, RepeatPolicy = options.RepeatPolicy };
        }
    }
    public void ClearLastHttp()
    {
        lock (sync) lastHttp = null;
    }
    public async Task TraceHttp(HttpContext context, Func<Task> next)
    {
        if (context.Request.Path != "/CompleteSale") { await next(); return; }
        long id;
        SimulatorOptions selected;
        lock (sync)
        {
            id = ++httpSequence;
            selected = options with { };
            lastHttp = new(id, DateTimeOffset.UtcNow, selected.Scenario.ToString(), "PENDING",
                selected.RepeatPolicy.ToString(), false, null, null, "İstek sürüyor; henüz yanıt gönderilmedi.");
        }
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        bool delivered = false;
        try
        {
            await next();
            if (!context.RequestAborted.IsCancellationRequested)
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(original, context.RequestAborted);
                delivered = true;
            }
        }
        finally
        {
            context.Response.Body = original;
            string chosen = context.Response.Headers["X-Simulator-Selected-Scenario"].ToString();
            string used = context.Response.Headers["X-Simulator-Used-Scenario"].ToString();
            string policy = context.Response.Headers["X-Simulator-Repeat-Policy"].ToString();
            string note = context.Items["SimulatorNote"] as string ?? "";
            lock (sync)
            {
                if (lastHttp?.RequestId == id)
                    lastHttp = new(id, DateTimeOffset.UtcNow, chosen.Length == 0 ? selected.Scenario.ToString() : chosen,
                        used.Length == 0 ? "NOT_DISPATCHED" : used, policy.Length == 0 ? selected.RepeatPolicy.ToString() : policy,
                        context.Response.Headers["X-Simulator-Repeated"] == "true", delivered ? context.Response.StatusCode : null,
                        delivered ? Encoding.UTF8.GetString(buffer.ToArray()) : null,
                        delivered ? note : "İstemciye tam HTTP yanıtı ulaştırılamadı; yeniden tahsilat varsaymayın.", context.Items["SimulatorPaymentType"] as int?);
            }
        }
    }
}