LauncherGo Command Bridge

This server-side mod is deployed by LauncherGo. It listens only on 127.0.0.1 and requires the per-profile random access token stored in ModConfig/launchergocommandbridge.json.

Commands are accepted on the Vintage Story server main thread through ICoreServerAPI.InjectConsole, so they do not depend on the ServerHost process stdin pipe.

LauncherGo Command Bridge 1.0.1 supports live access-token rotation. Deploying a new bridge build still takes effect on the next server start; after it is loaded, rotating only the token does not require restarting the server.
