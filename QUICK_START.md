# Quick Start Guide

## 1. Fix Compilation Issues

If you're getting compilation errors, run:

\`\`\`bash
# Restore packages
dotnet restore

# Clean and rebuild
dotnet clean
dotnet build
\`\`\`

## 2. Configure Authentication

1. **Get your Tenant ID:**
\`\`\`bash
az account show --query tenantId -o tsv
\`\`\`

2. **Update appsettings.json:**
\`\`\`json
{
  "AzureAuthentication": {
    "AuthenticationMethod": "DeviceCode",
    "TenantId": "YOUR_TENANT_ID_HERE",
    "ClientId": "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
  }
}
\`\`\`

## 3. Test Authentication

1. **Start the API:**
\`\`\`bash
dotnet run --project src/AzureDiscovery.Api
\`\`\`

2. **Check authentication status:**
\`\`\`bash
curl https://localhost:7000/api/authtest/status
\`\`\`

3. **Get help:**
\`\`\`bash
curl https://localhost:7000/api/authtest/help
\`\`\`

4. **Test token acquisition (this will trigger device code flow):**
\`\`\`bash
curl -X POST https://localhost:7000/api/authtest/test-token
\`\`\`

5. **Follow the device code instructions in the console/logs**

## 4. Test Resource Discovery

Once authentication is working:

\`\`\`bash
# Test resource discovery
curl https://localhost:7000/api/test/resources/YOUR_SUBSCRIPTION_ID

# Start full discovery
curl -X POST https://localhost:7000/api/discovery/start \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Test Discovery",
    "sourceSubscriptionId": "YOUR_SUBSCRIPTION_ID",
    "targetSubscriptionId": "YOUR_SUBSCRIPTION_ID"
  }'
\`\`\`

## Troubleshooting

### Package Issues:
\`\`\`bash
# Update packages
dotnet add package Azure.Identity --version 1.10.4
dotnet add package Azure.Core --version 1.36.0
\`\`\`

### Authentication Issues:
- Use DeviceCode for MFA accounts
- Check tenant ID is correct
- Ensure you have Reader permissions on subscription
\`\`\`
