LauncherGo Command Bridge

Copyright (C) 2026 Vintage Story CN Studio (VSCN)
License: GNU General Public License version 3.0 only. See LICENSE.txt when this
mod is distributed as a standalone package, or the repository LICENSE file when
distributed with LauncherGo.

This server-side mod is deployed by LauncherGo. It listens only on 127.0.0.1 and requires the per-profile random access token stored in ModConfig/launchergocommandbridge.json.

Commands are accepted on the Vintage Story server main thread through ICoreServerAPI.InjectConsole, so they do not depend on the ServerHost process stdin pipe.

LauncherGo Command Bridge 1.0.1 supports live access-token rotation. Deploying a new bridge build still takes effect on the next server start; after it is loaded, rotating only the token does not require restarting the server.
