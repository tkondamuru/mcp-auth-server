using System;
using Microsoft.EntityFrameworkCore;

namespace McpServer.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Register the OpenIddict schemas.
            builder.UseOpenIddict();
        }
    }
}
