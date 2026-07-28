LauncherGo Restriction (embedded universal mod)

This directory is maintained by LauncherGo.

The server loads ModConfig/launchergorestriction.json and publishes the native
ModIdBlackList/ModIdWhiteList policy during the connection handshake. The client
then skips restricted mods before their code and assets are loaded. Players are
not kicked for a policy match.
