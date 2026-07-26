using System.Threading.Tasks;

namespace McpServer.Services
{
    public class UserAuthenticationService : IUserAuthenticationService
    {
        public Task<bool> ValidateCredentialsAsync(string username, string password)
        {
            // For now, credentials are hardcoded as requested.
            // In the future, this can be updated to make an HTTP request to the external Web API.
            var isValid = username == "CUS9999" && password == "test5PGW";
            return Task.FromResult(isValid);
        }
    }
}
