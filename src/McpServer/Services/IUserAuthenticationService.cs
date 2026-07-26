using System.Threading.Tasks;

namespace McpServer.Services
{
    public interface IUserAuthenticationService
    {
        Task<bool> ValidateCredentialsAsync(string username, string password);
    }
}
