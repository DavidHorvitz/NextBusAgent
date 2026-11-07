# Infisical Secrets Setup

## Infisical Project Details

**Project Name:** Bus Pulse
**Project ID:** f55b30c8-9013-4f4d-985c-08733c438eed

## Secrets Stored in Infisical

The following secrets are stored securely in Infisical and accessed at runtime:

- `GOOGLE_CLIENT_ID` — Google OAuth 2.0 Client ID for calendar integration
- `GOOGLE_CLIENT_SECRET` — Google OAuth 2.0 Client Secret for calendar integration

## Environment Variables Required for Local Development

To run BusPulse locally, you need to set these three environment variables. These allow the app to connect to Infisical and fetch the Google secrets:

```
BUSPULSE_INFISICAL_CLIENT_ID=<your_infisical_machine_identity_client_id>
BUSPULSE_INFISICAL_CLIENT_SECRET=<your_infisical_machine_identity_client_secret>
BUSPULSE_INFISICAL_PROJECT_ID=f55b30c8-9013-4f4d-985c-08733c438eed
```

Optional: `BUSPULSE_INFISICAL_ENV` (default `dev`), `BUSPULSE_INFISICAL_PATH` (default `/`), and `BUSPULSE_INFISICAL_URL` (default `https://app.infisical.com`; HTTPS only).

## How to Get Infisical Credentials

The app signs in to Infisical with a **Machine Identity** (Universal Auth), not a personal API key.

1. Log in to Infisical (https://infisical.com)
2. Create a Machine Identity (Organization Settings → Access Control → Machine Identities) and give it the Universal Auth method
3. Add the identity to the "Bus Pulse" project with read access to the environment you use
4. Create a client secret for the identity and copy the Client ID and Client Secret
5. Set them as Windows environment variables (never in a file that is committed)

## How to Set Up Google OAuth

1. Go to Google Cloud Console (https://console.cloud.google.com)
2. Search for "Google Calendar API" and enable it
3. Go to Credentials in the left sidebar
4. Click "+ Create Credentials" → "OAuth Client ID"
5. Choose "Desktop application"
6. Copy the Client ID and Client Secret
7. In Infisical, create two secrets:
   - Name: GOOGLE_CLIENT_ID → Value: <your_client_id>
   - Name: GOOGLE_CLIENT_SECRET → Value: <your_client_secret>

## Running the App

Once you have set the environment variables, run:

```bash
dotnet run --project .\BusPulse\BusPulse.csproj
```

The app will automatically fetch the Google credentials from Infisical using the provided Machine Identity credentials. If Infisical is not configured or cannot be reached, it falls back to the `BUSPULSE_GOOGLE_CLIENT_ID` and `BUSPULSE_GOOGLE_CLIENT_SECRET` environment variables.

## Security Notes

- Never commit .env or appsettings.Local.json to Git
- Infisical credentials should only be set on your local machine or in CI/CD secrets
- Google credentials are stored only in Infisical, not in the Git repository
