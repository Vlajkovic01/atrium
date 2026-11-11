using Microsoft.EntityFrameworkCore;

namespace Atrium.Data.Migrations;

public sealed class EfMigrationOperations(AtriumDbContext db) : IMigrationOperations
{
    public IReadOnlyList<string> Known { get; } = [.. db.Database.GetMigrations()];

    public async Task<IReadOnlyList<string>> GetAppliedAsync(CancellationToken ct) =>
        [.. await db.Database.GetAppliedMigrationsAsync(ct)];

    public Task ApplyPendingAsync(CancellationToken ct) => db.Database.MigrateAsync(ct);
}
