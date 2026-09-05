LauncherGo Server Bridge

This server-side mod is deployed by LauncherGo and listens only on 127.0.0.1. It uses the per-profile token in ModConfig/launchergoserverbridge.json.

Protocol version 2 supports authenticated queries, commands, and long-lived event subscriptions over NDJSON. OSQ HTTP clients are not compatible.
