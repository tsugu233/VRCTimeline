using Microsoft.EntityFrameworkCore;
using VRCTimeline.Models;

namespace VRCTimeline.Data;

/// <summary>
/// SQLite データベースコンテキスト。
/// DB ファイルは %APPDATA%/VRCTimeline/vrchat_activity.db に配置される。
/// </summary>
public class AppDbContext : DbContext
{
    private static readonly string DbDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCTimeline");

    public static string DbPath => Path.Combine(DbDirectory, "vrchat_activity.db");

    /// <summary>ワールド訪問履歴</summary>
    public DbSet<WorldVisit> WorldVisits => Set<WorldVisit>();

    /// <summary>同室プレイヤーのセッション記録</summary>
    public DbSet<PlayerSession> PlayerSessions => Set<PlayerSession>();

    /// <summary>VRChat スクリーンショットのメタデータ</summary>
    public DbSet<PhotoRecord> PhotoRecords => Set<PhotoRecord>();

    /// <summary>
    /// ログファイルの処理済み位置。
    /// 現在は未使用（既存 DB とのスキーマ互換のため残置）。
    /// </summary>
    public DbSet<ProcessedLogFile> ProcessedLogFiles => Set<ProcessedLogFile>();

    /// <summary>Invite / Boop 等の通知履歴</summary>
    public DbSet<NotificationRecord> NotificationRecords => Set<NotificationRecord>();

    /// <summary>ワールド内で検出された動画再生の記録</summary>
    public DbSet<VideoRecord> VideoRecords => Set<VideoRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        Directory.CreateDirectory(DbDirectory);
        options.UseSqlite($"Data Source={DbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── WorldVisit ──
        modelBuilder.Entity<WorldVisit>(entity =>
        {
            entity.HasIndex(e => e.JoinedAt);
            entity.HasIndex(e => e.WorldId);
            entity.HasMany(e => e.PlayerSessions)
                .WithOne(e => e.WorldVisit)
                .HasForeignKey(e => e.WorldVisitId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Photos)
                .WithOne(e => e.WorldVisit)
                .HasForeignKey(e => e.WorldVisitId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ── PlayerSession ──
        modelBuilder.Entity<PlayerSession>(entity =>
        {
            entity.HasIndex(e => e.DisplayName);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.JoinedAt);
        });

        // ── PhotoRecord ──
        modelBuilder.Entity<PhotoRecord>(entity =>
        {
            entity.HasIndex(e => e.TakenAt);
            entity.HasIndex(e => e.FilePath).IsUnique();
        });

        // ── ProcessedLogFile ──
        modelBuilder.Entity<ProcessedLogFile>(entity =>
        {
            entity.HasIndex(e => e.FileName).IsUnique();
        });

        // ── NotificationRecord ──
        modelBuilder.Entity<NotificationRecord>(entity =>
        {
            entity.HasIndex(e => e.ReceivedAt);
            entity.HasIndex(e => e.SenderName);
            entity.HasIndex(e => e.NotificationType);
        });

        // ── VideoRecord ──
        modelBuilder.Entity<VideoRecord>(entity =>
        {
            entity.HasIndex(e => e.DetectedAt);
            entity.HasIndex(e => e.Url);
        });
    }
}
