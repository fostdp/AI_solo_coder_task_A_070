using Microsoft.EntityFrameworkCore;
using AluminaDetection.Api.Models;

namespace AluminaDetection.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<PotInfo> PotInfos => Set<PotInfo>();
    public DbSet<PotRealtimeData> PotRealtimeData => Set<PotRealtimeData>();
    public DbSet<FeedingRecord> FeedingRecords => Set<FeedingRecord>();
    public DbSet<AlarmRecord> AlarmRecords => Set<AlarmRecord>();
    public DbSet<ConcentrationHistory> ConcentrationHistories => Set<ConcentrationHistory>();
    public DbSet<VoltageFeature> VoltageFeatures => Set<VoltageFeature>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PotInfo>(entity =>
        {
            entity.HasKey(e => e.PotId);
            entity.HasIndex(e => e.PotCode).IsUnique();
            entity.HasIndex(e => new { e.RowIndex, e.ColIndex });
            entity.Property(e => e.PotCode).HasMaxLength(50).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        });

        modelBuilder.Entity<PotRealtimeData>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PotId);
            entity.HasIndex(e => e.RecordedAt);
            entity.HasIndex(e => new { e.PotId, e.RecordedAt });

            entity.HasOne(e => e.Pot)
                .WithMany()
                .HasForeignKey(e => e.PotId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.AnodeCurrentDistribution).HasMaxLength(500);
            entity.Property(e => e.RecordedAt).HasDefaultValueSql("GETUTCDATE()");
        });

        modelBuilder.Entity<FeedingRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PotId);
            entity.HasIndex(e => e.FeedTime);

            entity.HasOne(e => e.Pot)
                .WithMany()
                .HasForeignKey(e => e.PotId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.FeedType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Operator).HasMaxLength(50);
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("Pending");
        });

        modelBuilder.Entity<AlarmRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PotId);
            entity.HasIndex(e => new { e.IsHandled, e.CreatedAt });
            entity.HasIndex(e => e.AlarmLevel);

            entity.HasOne(e => e.Pot)
                .WithMany()
                .HasForeignKey(e => e.PotId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.AlarmType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Message).HasMaxLength(500).IsRequired();
            entity.Property(e => e.HandledBy).HasMaxLength(50);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        });

        modelBuilder.Entity<ConcentrationHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PotId);
            entity.HasIndex(e => new { e.PotId, e.RecordedAt });

            entity.HasOne(e => e.Pot)
                .WithMany()
                .HasForeignKey(e => e.PotId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.Source).HasMaxLength(50).IsRequired();
            entity.Property(e => e.RecordedAt).HasDefaultValueSql("GETUTCDATE()");
        });

        modelBuilder.Entity<VoltageFeature>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PotId);
            entity.HasIndex(e => new { e.PotId, e.ExtractedAt });

            entity.HasOne(e => e.Pot)
                .WithMany()
                .HasForeignKey(e => e.PotId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
