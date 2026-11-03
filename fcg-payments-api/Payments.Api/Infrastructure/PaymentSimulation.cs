using FCG.Messaging.Contracts;

namespace Payments.Api.Infrastructure;

public class PaymentSimulationOptions
{
    public const string SectionName = "Payments";

    /// <summary>Random (padrao), AlwaysApprove ou AlwaysReject.</summary>
    public string SimulationMode { get; set; } = "Random";

    public double ApprovalRate { get; set; } = 0.92;

    /// <summary>Precos que sempre reprovam (ex.: 99.99) — util para demo no Swagger.</summary>
    public decimal[] RejectPrices { get; set; } = [99.99m];
}

public static class PaymentSimulation
{
    public static PaymentStatus Resolve(PaymentSimulationOptions options, decimal price)
    {
        var mode = options.SimulationMode?.Trim() ?? "Random";

        if (mode.Equals("AlwaysApprove", StringComparison.OrdinalIgnoreCase))
            return PaymentStatus.Approved;

        if (mode.Equals("AlwaysReject", StringComparison.OrdinalIgnoreCase))
            return PaymentStatus.Rejected;

        foreach (var rejectPrice in options.RejectPrices)
        {
            if (price == rejectPrice)
                return PaymentStatus.Rejected;
        }

        var approved = Random.Shared.NextDouble() < options.ApprovalRate;
        return approved ? PaymentStatus.Approved : PaymentStatus.Rejected;
    }
}
