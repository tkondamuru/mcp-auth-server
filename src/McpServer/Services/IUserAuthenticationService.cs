using System;
using System.Threading.Tasks;

namespace McpServer.Services
{
    public class ExternalAuthResult
    {
        public bool Success { get; set; }
        public string? Token { get; set; }
        public string? SessionKey { get; set; }
        public string? CustomerId { get; set; }
        public DateTime? TokenExpiration { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public interface IUserAuthenticationService
    {
        Task<ExternalAuthResult> ValidateCredentialsAsync(string username, string password);
    }
}
