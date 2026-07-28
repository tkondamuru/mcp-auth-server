using System;
using Microsoft.EntityFrameworkCore;

namespace McpServer.Data
{
    public class DeveloperKey
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Key { get; set; } = "";
        public string Username { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
    }

    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<DeveloperKey> DeveloperKeys => Set<DeveloperKey>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Register the OpenIddict schemas.
            builder.UseOpenIddict();
        }
    }
}
