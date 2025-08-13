namespace AzureDiscovery.Core.Interfaces
{
    public interface IUserAccountService
    {
        Task RegisterAsync(UserRequest signupRequest);
        Task ActivateUserAsync(string encryptedToken);
        Task<string> LoginAsync(LoginRequest loginRequest);
        Task SendPasswordResetEmailAsync(string email);
        Task ResetPasswordAsync(string token, string newPassword);
    }
    public class UserRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public bool Notifications { get; set; }
    }
    public class LoginRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
    public class ResetPasswordRequest
    {
        public string Token { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }
}
