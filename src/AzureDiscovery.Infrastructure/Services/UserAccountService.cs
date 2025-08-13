using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Model;
using AzureDiscovery.Infrastructure.Data;
using AzureDiscovery.Infrastructure.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;


namespace AzureDiscovery.Infrastructure.Services
{
    public class UserAccountService : IUserAccountService
    {
        private readonly DiscoveryDbContext _context;
        private readonly EmailSender _emailSender;
        private readonly IConfiguration _config;

        public UserAccountService(DiscoveryDbContext context, EmailSender emailSender, IConfiguration config)
        {
            _context = context;
            _emailSender = emailSender;
            _config = config;
        }

        public async Task RegisterAsync(UserRequest signupRequest)
        {
            if (_context.Users.Any(u => u.Email == signupRequest.Email))
                throw new Exception("User already exists");

            var encryptedPassword = BCrypt.Net.BCrypt.HashPassword(signupRequest.Password);

            var signUp = new User
            {
                FirstName = signupRequest.FirstName,
                LastName = signupRequest.LastName,
                Email = signupRequest.Email,
                PhoneNumber = signupRequest.Phone,
                Role = signupRequest.Role,
                NotificationsEnabled = signupRequest.Notifications,
                EncryptedPassword = encryptedPassword,
                IsActive = false
            };

            _context.Users.Add(signUp);
            await _context.SaveChangesAsync();

            // Build encrypted token
            var token = AesEncryptionHelper.Encrypt($"{signUp.Email}", _config["EncryptionKey"]);

            EmailTemplate template = await _context.EmailTemplates.FirstOrDefaultAsync(t => t.TemplateKey == "ACCOUNT_ACTIVATION") ??
                throw new Exception("template should not be empty");

            var activationLink = $"{_config["EmailUrl"]}/api/Auth/activate?token={Uri.EscapeDataString(token)}";
            var body = template.Body.Replace("{{activation_link}}", activationLink);

            var response = await _emailSender.SendEmailAsync(signUp.Email, template.Subject, body);
            var responseBody = await response.Body.ReadAsStringAsync();
        }

        public async Task ActivateUserAsync(string encryptedToken)
        {
            var decryptedEmail = AesEncryptionHelper.Decrypt(encryptedToken, _config["EncryptionKey"]);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == decryptedEmail);
            if (user == null)
                throw new Exception("User not found");

            user.IsActive = true;
            await _context.SaveChangesAsync();
        }

        public async Task<string> LoginAsync(LoginRequest loginRequest)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == loginRequest.Email);

            if (user == null)
                throw new Exception("User not found.");
            if (!user.IsActive)
                throw new Exception("Account is not activated. Please check your email.");
            if (!BCrypt.Net.BCrypt.Verify(loginRequest.Password, user.EncryptedPassword))
                throw new Exception("Invalid password.");

            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_config["JwtSettings:SecretKey"]);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
         new Claim(ClaimTypes.Email, user.Email),
         new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
     }),
                Expires = DateTime.UtcNow.AddHours(int.Parse(_config["JwtSettings:ExpiresInMinutes"])),
                Issuer = _config["JwtSettings:Issuer"],
                Audience = _config["JwtSettings:Audience"],
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

        public async Task SendPasswordResetEmailAsync(string email)
        {
            var token = AesEncryptionHelper.Encrypt($"{email}", _config["EncryptionKey"]);

            EmailTemplate template = await _context.EmailTemplates.FirstOrDefaultAsync(t => t.TemplateKey == "RESET_PASSWORD") ??
                throw new Exception("Email template for RESET_PASSWORD is missing.");

            var resetLink = $"{_config["FrontedUrl"]}/resetPassword?token={Uri.EscapeDataString(token)}";
            var body = template.Body.Replace("{{resetLink}}", resetLink);

            var response = await _emailSender.SendEmailAsync(email, template.Subject, body);
            var responseBody = await response.Body.ReadAsStringAsync();
        }

        public async Task ResetPasswordAsync(string token, string newPassword)
        {
            var email = AesEncryptionHelper.Decrypt(token, _config["EncryptionKey"]);
            if (email == null)
                throw new Exception("email should not be empty");

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user == null)
                throw new Exception("User not found");

            var encryptedPassword = BCrypt.Net.BCrypt.HashPassword(newPassword);
            user.EncryptedPassword = encryptedPassword;
            await _context.SaveChangesAsync();
        }
    }
}
