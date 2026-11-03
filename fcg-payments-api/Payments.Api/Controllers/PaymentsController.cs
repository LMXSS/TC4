using FCG.Messaging.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Api.Infrastructure;

namespace Payments.Api.Controllers;

[ApiController]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    private readonly PaymentsDbContext _db;
    private readonly PaymentSimulationOptions _options;

    public PaymentsController(PaymentsDbContext db, IOptions<PaymentSimulationOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    [HttpGet("simulation")]
    public ActionResult<object> GetSimulationConfig() =>
        Ok(new
        {
            mode = _options.SimulationMode,
            approvalRate = _options.ApprovalRate,
            rejectPrices = _options.RejectPrices,
            hint = "Crie um jogo com preco 49.90 para aprovar (modo Random) ou 99.99 para reprovar sempre."
        });

    [HttpGet("orders/{orderId:guid}")]
    public async Task<ActionResult<object>> GetOrderStatus(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _db.ProcessedOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderId == orderId, cancellationToken);

        if (order is null)
            return NotFound(new { message = "Pedido ainda nao processado ou OrderId invalido." });

        return Ok(new
        {
            order.OrderId,
            order.UserId,
            order.GameId,
            order.Price,
            status = order.Status.ToString(),
            order.ProcessedAtUtc
        });
    }
}
