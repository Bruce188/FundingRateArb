#!/usr/bin/env bash
set -euo pipefail

# ============================================================================
# Azure Infrastructure Setup for FundingRateArb
# Run once to create all Azure resources + GitHub OIDC federation.
#
# Prerequisites:
#   - az cli logged in (az login)
#   - gh cli logged in (gh auth login)
#   - jq installed
#
# Usage:
#   chmod +x infra/setup-azure.sh
#   ./infra/setup-azure.sh
# ============================================================================

# --- Configuration (edit these) ---
RESOURCE_GROUP="rg-fundingratearb1"
LOCATION="germanywestcentral"
APP_NAME="fundingratearb"
SQL_SERVER_NAME="sql-fundingratearb"
SQL_DB_NAME="FundingRateArbDb"
SQL_ADMIN_USER="sqladmin"
APP_SERVICE_PLAN="plan-fundingratearb"
KEY_VAULT_NAME="kv-fundingratearb"
GITHUB_REPO="Bruce188/FundingRateArb"        # owner/repo

# --- Prompt for secrets ---
read -rsp "SQL admin password (min 12 chars, mixed case + digits + symbols): " SQL_ADMIN_PASSWORD
echo
read -rsp "App seed admin password: " SEED_ADMIN_PASSWORD
echo

echo "==> Creating resource group..."
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" -o none

# --- Azure SQL ---
echo "==> Creating SQL Server..."
az sql server create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$SQL_SERVER_NAME" \
  --location "$LOCATION" \
  --admin-user "$SQL_ADMIN_USER" \
  --admin-password "$SQL_ADMIN_PASSWORD" \
  -o none

echo "==> Creating SQL Database (Basic tier, 5 DTU)..."
az sql db create \
  --resource-group "$RESOURCE_GROUP" \
  --server "$SQL_SERVER_NAME" \
  --name "$SQL_DB_NAME" \
  --edition Basic \
  --capacity 5 \
  --max-size 2GB \
  -o none

echo "==> Allowing Azure services through SQL firewall..."
az sql server firewall-rule create \
  --resource-group "$RESOURCE_GROUP" \
  --server "$SQL_SERVER_NAME" \
  --name AllowAzureServices \
  --start-ip-address 0.0.0.0 \
  --end-ip-address 0.0.0.0 \
  -o none

SQL_CONN="Server=tcp:${SQL_SERVER_NAME}.database.windows.net,1433;Database=${SQL_DB_NAME};User ID=${SQL_ADMIN_USER};Password=${SQL_ADMIN_PASSWORD};Encrypt=True;TrustServerCertificate=False;"

# --- App Service ---
echo "==> Creating App Service Plan (Linux, B1)..."
az appservice plan create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_SERVICE_PLAN" \
  --location "$LOCATION" \
  --sku B1 \
  --is-linux \
  -o none

echo "==> Creating Web App (.NET 8)..."
az webapp create \
  --resource-group "$RESOURCE_GROUP" \
  --plan "$APP_SERVICE_PLAN" \
  --name "$APP_NAME" \
  --runtime "DOTNETCORE:8.0" \
  -o none

echo "==> Enabling WebSockets..."
az webapp config set \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --web-sockets-enabled true \
  -o none

echo "==> Setting Always On..."
az webapp config set \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --always-on true \
  -o none

# --- Azure Key Vault ---
echo "==> Creating Key Vault..."
az keyvault create \
  --name "$KEY_VAULT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --enable-rbac-authorization true \
  -o none

echo "==> Enabling managed identity on App Service..."
IDENTITY_PRINCIPAL_ID=$(az webapp identity assign \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --query principalId -o tsv)

echo "==> Granting Key Vault Secrets User role to App Service..."
KV_RESOURCE_ID=$(az keyvault show \
  --name "$KEY_VAULT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query id -o tsv)
az role assignment create \
  --assignee "$IDENTITY_PRINCIPAL_ID" \
  --role "Key Vault Secrets User" \
  --scope "$KV_RESOURCE_ID" \
  -o none

echo "==> Granting Key Vault Secrets Officer role to current user..."
CURRENT_USER_ID=$(az ad signed-in-user show --query id -o tsv)
az role assignment create \
  --assignee "$CURRENT_USER_ID" \
  --role "Key Vault Secrets Officer" \
  --scope "$KV_RESOURCE_ID" \
  -o none

echo "==> Waiting for RBAC propagation..."
for i in {1..12}; do
  if az keyvault secret list --vault-name "$KEY_VAULT_NAME" --maxresults 1 -o none 2>/dev/null; then
    echo "  RBAC propagated."
    break
  fi
  if [ "$i" -eq 12 ]; then
    echo "ERROR: RBAC propagation timed out after 3 minutes. Re-run the script or wait and retry manually."
    exit 1
  fi
  echo "  Attempt $i/12 — waiting 15s..."
  sleep 15
done

echo "==> Populating Key Vault secrets..."
az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "ConnectionStrings--DefaultConnection" --value "$SQL_CONN" -o none
az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "Seed--AdminPassword" --value "$SEED_ADMIN_PASSWORD" -o none

# Exchange API keys — add these manually after running the script:
#   az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "Exchanges--Hyperliquid--WalletAddress" --value "0x..."
#   az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "Exchanges--Hyperliquid--PrivateKey" --value "..."
#   az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "Exchanges--Aster--ApiKey" --value "..."
#   az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "Exchanges--Aster--ApiSecret" --value "..."
#   az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "Exchanges--Lighter--ApiKey" --value "..."
#   az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "Exchanges--Lighter--SignerPrivateKey" --value "..."

# --- Application Insights ---
echo "==> Creating Application Insights resource..."
az monitor app-insights component create \
  --app "${APP_NAME}-insights" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --kind web \
  --application-type web \
  -o none

AI_CONN_STRING=$(az monitor app-insights component show \
  --app "${APP_NAME}-insights" \
  --resource-group "$RESOURCE_GROUP" \
  --query connectionString -o tsv)

echo "==> Storing Application Insights connection string in Key Vault..."
az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "ApplicationInsights--ConnectionString" --value "$AI_CONN_STRING" -o none

# --- Data Protection Key Storage ---
DP_STORAGE_NAME="${APP_NAME}dpkeys"
# Storage account names must be 3-24 chars, lowercase, alphanumeric
DP_STORAGE_NAME=$(echo "$DP_STORAGE_NAME" | tr -cd '[:alnum:]' | cut -c1-24 | tr '[:upper:]' '[:lower:]')
echo "==> Creating storage account for Data Protection keys..."
az storage account create \
  --name "$DP_STORAGE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --sku Standard_LRS \
  --kind StorageV2 \
  -o none

echo "==> Creating 'dataprotection' container..."
DP_STORAGE_CONN=$(az storage account show-connection-string \
  --name "$DP_STORAGE_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query connectionString -o tsv)
az storage container create \
  --name dataprotection \
  --connection-string "$DP_STORAGE_CONN" \
  -o none

echo "==> Storing Data Protection connection string in Key Vault..."
az keyvault secret set --vault-name "$KEY_VAULT_NAME" --name "DataProtection--BlobStorageConnection" --value "$DP_STORAGE_CONN" -o none

echo "==> Configuring app settings..."
az webapp config appsettings set \
  --resource-group "$RESOURCE_GROUP" \
  --name "$APP_NAME" \
  --settings \
    "ASPNETCORE_ENVIRONMENT=Production" \
    "KeyVaultName=${KEY_VAULT_NAME}" \
  -o none

# --- OIDC Federation for GitHub Actions ---
echo "==> Creating Entra ID app registration..."
APP_REG_ID=$(az ad app create --display-name "${APP_NAME}-github-deploy" --query appId -o tsv)
az ad sp create --id "$APP_REG_ID" -o none

OBJECT_ID=$(az ad app show --id "$APP_REG_ID" --query id -o tsv)
SUB_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)

echo "==> Adding federated credential for main branch..."
az ad app federated-credential create --id "$OBJECT_ID" --parameters "{
  \"name\": \"github-main\",
  \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"repo:${GITHUB_REPO}:ref:refs/heads/main\",
  \"audiences\": [\"api://AzureADTokenExchange\"]
}" -o none

echo "==> Adding federated credential for production environment..."
az ad app federated-credential create --id "$OBJECT_ID" --parameters "{
  \"name\": \"github-production-env\",
  \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"repo:${GITHUB_REPO}:environment:production\",
  \"audiences\": [\"api://AzureADTokenExchange\"]
}" -o none

echo "==> Assigning Contributor role..."
az role assignment create \
  --assignee "$APP_REG_ID" \
  --role "Contributor" \
  --scope "/subscriptions/${SUB_ID}/resourceGroups/${RESOURCE_GROUP}" \
  -o none

# --- Set GitHub secrets ---
echo "==> Setting GitHub repository secrets..."
gh secret set AZURE_CLIENT_ID      --repo "$GITHUB_REPO" --body "$APP_REG_ID"
gh secret set AZURE_TENANT_ID      --repo "$GITHUB_REPO" --body "$TENANT_ID"
gh secret set AZURE_SUBSCRIPTION_ID --repo "$GITHUB_REPO" --body "$SUB_ID"
gh secret set SQL_CONNECTION_STRING --repo "$GITHUB_REPO" --body "$SQL_CONN"

# --- Summary ---
echo ""
echo "============================================"
echo "  Azure infrastructure created!"
echo "============================================"
echo "  Resource Group:  $RESOURCE_GROUP"
echo "  App Service:     https://${APP_NAME}.azurewebsites.net"
echo "  SQL Server:      ${SQL_SERVER_NAME}.database.windows.net"
echo "  Database:        $SQL_DB_NAME"
echo "  Key Vault:       $KEY_VAULT_NAME"
echo "  GitHub OIDC:     configured for ${GITHUB_REPO}:main"
echo ""
echo "  Next steps:"
echo "    1. Add exchange API keys via Key Vault:"
echo "       az keyvault secret set --vault-name $KEY_VAULT_NAME \\"
echo "         --name \"Exchanges--Hyperliquid--WalletAddress\" --value \"0x...\""
echo "    2. Push to main — GitHub Actions will build, test, and deploy."
echo "    3. (Optional) Create a 'production' environment in GitHub"
echo "       with required reviewers for deploy approval gate."
echo "============================================"
