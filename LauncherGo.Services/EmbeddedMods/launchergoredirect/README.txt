LauncherGo Gateway Redirect

Author: VSCN-Studio

This mod is deployed by LauncherGo to gateway-associated server instances. It accepts:
  /launchergateway redirect <player-name-or-uid> <server-id>
  /launchergateway evacuate <server-id>

The client reconnects through its original Gateway address using a one-time transfer credential.
Backend host and port are never sent to players.
