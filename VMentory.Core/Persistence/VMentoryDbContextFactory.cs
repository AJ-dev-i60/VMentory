using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VMentory.Core.Persistence;

// Design-time factory so `dotnet ef migrations` can build the context without running the Web app
// (which finds a free port, checks for updates, etc.). The connection string here is a placeholder —
// migrations only need the provider (SQLite) to emit the right SQL; the real path comes from
// VMENTORY_DB at runtime.
public class VMentoryDbContextFactory : IDesignTimeDbContextFactory<VMentoryDbContext>
{
    public VMentoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VMentoryDbContext>()
            .UseSqlite("Data Source=vmentory-design.db")
            .Options;
        return new VMentoryDbContext(options);
    }
}
