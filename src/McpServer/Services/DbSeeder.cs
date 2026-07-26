using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;
using McpServer.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace McpServer.Services
{
    public class DbSeeder : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;

        public DbSeeder(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();

            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.Database.EnsureCreatedAsync(cancellationToken);

            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

            // Seed Client application if it doesn't exist.
            if (await manager.FindByClientIdAsync("mcp-client", cancellationToken) == null)
            {
                await manager.CreateAsync(new OpenIddictApplicationDescriptor
                {
                    ClientId = "mcp-client",
                    DisplayName = "MCP Client Application",
                    ClientType = ClientTypes.Public,
                    RedirectUris =
                    {
                        new Uri("http://localhost:5000/callback"),
                        new Uri("http://localhost:5000/callback.html"),
                        new Uri("http://localhost:3000/callback"),
                        new Uri("http://localhost:3000/callback.html"),
                        new Uri("http://localhost:8000/callback"),
                        new Uri("http://localhost:8000/callback.html"),
                        new Uri("http://localhost:5080/callback"),
                        new Uri("http://localhost:5080/callback.html"),
                        new Uri("http://localhost:5156/callback"),
                        new Uri("http://localhost:5156/callback.html"),
                        new Uri("https://localhost:7048/callback")
                    },
                    Permissions =
                    {
                        Permissions.Endpoints.Authorization,
                        Permissions.Endpoints.Token,
                        Permissions.GrantTypes.AuthorizationCode,
                        Permissions.GrantTypes.Password,
                        Permissions.GrantTypes.RefreshToken,
                        Permissions.ResponseTypes.Code,
                        Permissions.Prefixes.Scope + Scopes.OpenId,
                        Permissions.Prefixes.Scope + Scopes.Profile,
                        Permissions.Prefixes.Scope + Scopes.Email,
                        Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                        Permissions.Prefixes.Scope + "mcp"
                    }
                }, cancellationToken);
            }

            var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
            
            if (await scopeManager.FindByNameAsync("mcp", cancellationToken) == null)
            {
                await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
                {
                    Name = "mcp",
                    DisplayName = "Model Context Protocol API Access",
                    Resources = { "mcp_resource" }
                }, cancellationToken);
            }

            if (await scopeManager.FindByNameAsync("email", cancellationToken) == null)
            {
                await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
                {
                    Name = "email",
                    DisplayName = "Email address access",
                    Description = "Access to the user's primary email address"
                }, cancellationToken);
            }

            if (await scopeManager.FindByNameAsync("profile", cancellationToken) == null)
            {
                await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
                {
                    Name = "profile",
                    DisplayName = "User profile access",
                    Description = "Access to the user's profile information"
                }, cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
