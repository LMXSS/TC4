using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;

namespace Payments.Api.Infrastructure;

public class PaymentsDbContext : DbContext
{
    public PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : base(options) { }

    public DbSet<ProcessedOrder> ProcessedOrders => Set<ProcessedOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedOrder>(e =>
        {
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProcessedAtUtc).IsRequired();
        });
    }
}
