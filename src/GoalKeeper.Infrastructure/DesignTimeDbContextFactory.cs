using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GoalKeeper.Infrastructure;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GoalKeeperDbContext>
{
    public GoalKeeperDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GoalKeeperDbContext>()
            .UseSqlite("Data Source=goalkeeper-design.db")
            .Options;
        return new(options);
    }
}
