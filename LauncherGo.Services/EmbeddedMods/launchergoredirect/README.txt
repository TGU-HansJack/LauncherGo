LauncherGo Gateway Redirect

Author: VSCN-Studio
Copyright (C) 2026 Vintage Story CN Studio (VSCN)
License: GNU General Public License version 3.0 only. See LICENSE.txt when this
mod is distributed as a standalone package, or the repository LICENSE file when
distributed with LauncherGo.

This mod is deployed by LauncherGo to gateway-associated server instances. It accepts:
  /launchergateway redirect <player-name-or-uid> <server-id>
  /launchergateway evacuate <server-id>

The client reconnects through its original Gateway address using a one-time transfer credential.
Backend host and port are never sent to players.
