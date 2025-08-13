# Deployment API Guide

## Overview

The deployment phase provides APIs for validating and deploying ARM templates generated from discovered Azure resources.

## API Endpoints

### 1. Create Deployment Session

Create a new deployment session from a completed discovery session.

\`\`\`bash
POST /api/deployment/sessions
Content-Type: application/json

{
  "name": "Production Environment Deployment",
  "discoverySessionId": "discovery-session-guid",
  "targetSubscriptionId": "target-subscription-id",
  "targetResourceGroup": "target-rg-name",
  "mode": "Incremental",
  "validateOnly": false,
  "parameters": {
    "location": "East US 2",
    "environment": "prod"
  }
}
\`\`\`

### 2. Get Deployment Status

\`\`\`bash
GET /api/deployment/sessions/{sessionId}
\`\`\`

### 3. Validate Templates

Validate all templates in a deployment session:
\`\`\`bash
POST /api/deployment/sessions/{sessionId}/validate
\`\`\`

Validate a specific template:
\`\`\`bash
POST /api/deployment/templates/{templateId}/validate
\`\`\`

### 4. Deploy Templates

Deploy all templates in a session:
\`\`\`bash
POST /api/deployment/sessions/{sessionId}/deploy
\`\`\`

Deploy a specific template:
\`\`\`bash
POST /api/deployment/templates/{templateId}/deploy
\`\`\`

### 5. Monitor Progress

Get deployment summary:
\`\`\`bash
GET /api/deployment/sessions/{sessionId}/summary
\`\`\`

Get template deployments:
\`\`\`bash
GET /api/deployment/sessions/{sessionId}/templates
\`\`\`

### 6. Cancel Deployment

\`\`\`bash
POST /api/deployment/sessions/{sessionId}/cancel
\`\`\`

## Complete Workflow Example

### Step 1: Complete Discovery
\`\`\`bash
# Start discovery
curl -X POST https://localhost:7000/api/discovery/start \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Source Environment Discovery",
    "sourceSubscriptionId": "source-sub-id",
    "targetSubscriptionId": "target-sub-id"
  }'

# Wait for completion and get session ID
DISCOVERY_SESSION_ID="discovery-session-guid"
\`\`\`

### Step 2: Create Deployment Session
\`\`\`bash
curl -X POST https://localhost:7000/api/deployment/sessions \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Target Environment Deployment",
    "discoverySessionId": "'$DISCOVERY_SESSION_ID'",
    "targetSubscriptionId": "target-subscription-id",
    "targetResourceGroup": "cloned-environment-rg",
    "mode": "Incremental",
    "parameters": {
      "location": "West US 2",
      "environment": "staging"
    }
  }'

# Get deployment session ID from response
DEPLOYMENT_SESSION_ID="deployment-session-guid"
\`\`\`

### Step 3: Validate Templates
\`\`\`bash
# Validate all templates
curl -X POST https://localhost:7000/api/deployment/sessions/$DEPLOYMENT_SESSION_ID/validate

# Check validation results
curl https://localhost:7000/api/deployment/sessions/$DEPLOYMENT_SESSION_ID/summary
\`\`\`

### Step 4: Deploy Templates
\`\`\`bash
# Deploy all templates (if validation passed)
curl -X POST https://localhost:7000/api/deployment/sessions/$DEPLOYMENT_SESSION_ID/deploy

# Monitor progress
curl https://localhost:7000/api/deployment/sessions/$DEPLOYMENT_SESSION_ID/summary
\`\`\`

### Step 5: Monitor Deployment
\`\`\`bash
# Check overall status
curl https://localhost:7000/api/deployment/sessions/$DEPLOYMENT_SESSION_ID

# Get detailed template status
curl https://localhost:7000/api/deployment/sessions/$DEPLOYMENT_SESSION_ID/templates
\`\`\`

## Response Examples

### Deployment Session Response
\`\`\`json
{
  "id": "deployment-session-guid",
  "name": "Target Environment Deployment",
  "discoverySessionId": "discovery-session-guid",
  "targetSubscriptionId": "target-subscription-id",
  "targetResourceGroup": "cloned-environment-rg",
  "mode": "Incremental",
  "status": "Deploying",
  "startedAt": "2024-01-15T10:00:00Z",
  "totalTemplates": 5,
  "templatesDeployed": 2,
  "templatesFailed": 0
}
\`\`\`

### Validation Result Response
\`\`\`json
{
  "isValid": true,
  "errors": [],
  "warnings": [
    {
      "code": "TemplateWarning",
      "message": "Resource name may conflict with existing resources",
      "target": "virtualMachine1"
    }
  ],
  "validationDuration": "00:00:30",
  "validatedAt": "2024-01-15T10:05:00Z"
}
\`\`\`

### Deployment Result Response
\`\`\`json
{
  "isSuccessful": true,
  "deploymentId": "azure-deployment-id",
  "state": "Succeeded",
  "errors": [],
  "outputs": {
    "virtualMachineId": "/subscriptions/.../virtualMachines/vm1",
    "publicIpAddress": "20.1.2.3"
  },
  "deploymentDuration": "00:15:30",
  "deployedAt": "2024-01-15T10:20:00Z"
}
\`\`\`

## Error Handling

### Common Error Responses

**Discovery Session Not Found:**
\`\`\`json
{
  "error": "Discovery session not found"
}
\`\`\`

**Discovery Not Completed:**
\`\`\`json
{
  "error": "Discovery session must be completed before deployment. Current status: InProgress"
}
\`\`\`

**Validation Failed:**
\`\`\`json
{
  "isValid": false,
  "errors": [
    {
      "code": "InvalidTemplate",
      "message": "The template is not valid",
      "target": "Microsoft.Compute/virtualMachines",
      "details": "Property 'vmSize' is required"
    }
  ]
}
\`\`\`

**Deployment Failed:**
\`\`\`json
{
  "isSuccessful": false,
  "state": "Failed",
  "errors": [
    {
      "code": "ResourceNotFound",
      "message": "The specified subnet does not exist",
      "target": "networkInterface1"
    }
  ]
}
\`\`\`

## Best Practices

1. **Always validate before deploying**
2. **Monitor deployment progress regularly**
3. **Use incremental mode for safer deployments**
4. **Test in a development environment first**
5. **Review validation warnings carefully**
6. **Keep deployment sessions for audit purposes**

## Deployment Modes

- **Incremental**: Only deploys resources defined in the template
- **Complete**: Removes resources not defined in the template (use with caution)

## Template Dependencies

Templates are deployed in dependency order:
- Level 0: Independent resources (VNets, Storage Accounts)
- Level 1: Resources depending on Level 0 (Subnets, NICs)
- Level 2: Resources depending on Level 1 (VMs)
- And so on...

## Monitoring and Troubleshooting

Use the summary endpoint to monitor progress:
\`\`\`bash
curl https://localhost:7000/api/deployment/sessions/$DEPLOYMENT_SESSION_ID/summary
\`\`\`

This provides:
- Overall progress percentage
- Templates by status
- Templates by dependency level
- Recent activity log
- Error details
\`\`\`
</QuickEdit>

I've implemented a comprehensive deployment engine with the following key features:

## 🚀 **Processing Engine & Deployment Phase**

### **Core Components:**

1. **DeploymentSession & TemplateDeployment Models** - Track deployment progress and template status
2. **AzureDeploymentService** - Orchestrates the entire deployment process
3. **AzureResourceManagerService** - Handles ARM template validation and deployment
4. **DeploymentController** - Exposes REST APIs for deployment operations

### **Key Features:**

#### **Template Validation:**
- Individual template validation
- Bulk validation for all templates
- Detailed error reporting with Azure ARM validation
- Validation warnings and recommendations

#### **Smart Deployment:**
- **Dependency-aware deployment** - Templates deployed in correct order based on resource dependencies
- **Parallel processing** - Templates at same dependency level deploy simultaneously
- **Rate limiting** - Respects Azure ARM API limits
- **Progress monitoring** - Real-time deployment status tracking

#### **Deployment APIs:**

\`\`\`bash
# Create deployment session
POST /api/deployment/sessions

# Validate templates
POST /api/deployment/sessions/{id}/validate
POST /api/deployment/templates/{id}/validate

# Deploy templates  
POST /api/deployment/sessions/{id}/deploy
POST /api/deployment/templates/{id}/deploy

# Monitor progress
GET /api/deployment/sessions/{id}/summary
GET /api/deployment/sessions/{id}/templates

# Cancel deployment
POST /api/deployment/sessions/{id}/cancel
\`\`\`

#### **Advanced Features:**
- **Resource Group Management** - Automatically creates target resource groups if they don't exist
- **Template Storage** - ARM templates backed up to blob storage for audit and rollback
- **Error Recovery** - Detailed error reporting with retry capabilities
- **Incremental/Complete Modes** - Support for both deployment modes
- **Parameter Management** - Dynamic parameter generation with user overrides

#### **Deployment Flow:**

1. **Template Generation** - Convert discovered resources to ARM templates
2. **Dependency Analysis** - Calculate deployment order based on resource dependencies
3. **Validation Phase** - Validate all templates before deployment
4. **Staged Deployment** - Deploy templates level by level respecting dependencies
5. **Progress Monitoring** - Real-time status updates and error reporting

#### **Usage Example:**

\`\`\`bash
# Complete workflow
DISCOVERY_ID=$(curl -X POST .../api/discovery/start -d '{"name":"prod-discovery","sourceSubscriptionId":"..."}' | jq -r '.id')

# Wait for discovery completion
curl .../api/discovery/$DISCOVERY_ID/status

# Create deployment session
DEPLOY_ID=$(curl -X POST .../api/deployment/sessions -d '{
  "name":"prod-clone",
  "discoverySessionId":"'$DISCOVERY_ID'",
  "targetSubscriptionId":"target-sub-id",
  "targetResourceGroup":"cloned-prod-rg",
  "mode":"Incremental"
}' | jq -r '.id')

# Validate templates
curl -X POST .../api/deployment/sessions/$DEPLOY_ID/validate

# Deploy if validation passes
curl -X POST .../api/deployment/sessions/$DEPLOY_ID/deploy

# Monitor progress
curl .../api/deployment/sessions/$DEPLOY_ID/summary
\`\`\`

The deployment engine provides enterprise-grade template validation and deployment capabilities with comprehensive error handling, progress tracking, and dependency management - making it safe and reliable for production environment cloning.
