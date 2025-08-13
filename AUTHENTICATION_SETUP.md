# Azure Authentication Setup Guide

## For MFA-Enabled Accounts (Recommended)

Since your account has MFA enabled, you have several authentication options:

### Option 1: Device Code Flow (Recommended)
This is the best option for MFA-enabled accounts.

1. **Update appsettings.json:**
\`\`\`json
{
  "AzureAuthentication": {
    "AuthenticationMethod": "DeviceCode",
    "TenantId": "your-tenant-id",
    "ClientId": "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
  }
}
\`\`\`

2. **Start the application and authenticate:**
\`\`\`bash
# Start the API
dotnet run --project src/AzureDiscovery.Api

# Trigger authentication
curl -X POST https://localhost:7000/api/auth/device-code
\`\`\`

3. **Follow the device code instructions:**
   - The API will display a URL and code
   - Visit the URL in your browser
   - Enter the code
   - Complete MFA authentication

### Option 2: Interactive Browser Flow
Opens a browser window for authentication.

1. **Update appsettings.json:**
\`\`\`json
{
  "AzureAuthentication": {
    "AuthenticationMethod": "InteractiveBrowser",
    "TenantId": "your-tenant-id",
    "ClientId": "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
  }
}
\`\`\`

### Option 3: Username/Password (Limited - No MFA Support)
⚠️ **Warning:** This method does NOT work with MFA-enabled accounts.

\`\`\`json
{
  "AzureAuthentication": {
    "AuthenticationMethod": "UsernamePassword",
    "TenantId": "your-tenant-id",
    "ClientId": "04b07795-8ddb-461a-bbee-02f9e1bf7b46",
    "Username": "your-username@domain.com",
    "Password": "your-password"
  }
}
\`\`\`

## Required Azure Permissions

Your user account needs the following permissions:

### Minimum Required Permissions:
- **Reader** role on the subscription(s) you want to discover
- **Resource Graph Reader** (if available in your tenant)

### Recommended Permissions:
- **Reader** role on subscription
- **Storage Blob Data Reader** (if using blob storage)
- **Service Bus Data Receiver** (if using service bus)

## Setup Steps

1. **Get your Tenant ID:**
\`\`\`bash
# Using Azure CLI
az account show --query tenantId -o tsv

# Or visit Azure Portal > Azure Active Directory > Properties
\`\`\`

2. **Test authentication:**
\`\`\`bash
# Validate credentials
curl -X POST https://localhost:7000/api/auth/validate

# Check available methods
curl https://localhost:7000/api/auth/methods
\`\`\`

3. **Start discovery:**
\`\`\`bash
curl -X POST https://localhost:7000/api/discovery/start \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Test Discovery",
    "sourceSubscriptionId": "your-subscription-id",
    "targetSubscriptionId": "target-subscription-id",
    "resourceGroupFilters": [],
    "resourceTypeFilters": []
  }'
\`\`\`

## Troubleshooting

### Common Issues:

1. **MFA Required Error:**
   - Switch to DeviceCode or InteractiveBrowser authentication
   - Username/Password doesn't support MFA

2. **Insufficient Permissions:**
   - Ensure you have Reader access to the subscription
   - Contact your Azure administrator for permissions

3. **Tenant ID Issues:**
   - Verify your tenant ID is correct
   - Use `az account show` to get the correct tenant

4. **Client ID Issues:**
   - The default Client ID (04b07795-8ddb-461a-bbee-02f9e1bf7b46) is Azure CLI's public client
   - You can create your own app registration if needed

### Testing Authentication:
\`\`\`bash
# Test different authentication methods
curl https://localhost:7000/api/auth/methods

# Validate current credentials
curl -X POST https://localhost:7000/api/auth/validate

# For device code flow
curl -X POST https://localhost:7000/api/auth/device-code
\`\`\`

## Security Best Practices

1. **Never store passwords in plain text**
2. **Use Azure Key Vault for production**
3. **Consider using Managed Identity in Azure**
4. **Rotate credentials regularly**
5. **Use least privilege access**

## Production Deployment

For production, consider:
- **Managed Identity** (when running in Azure)
- **Service Principal** with certificate authentication
- **Azure Key Vault** for credential storage
