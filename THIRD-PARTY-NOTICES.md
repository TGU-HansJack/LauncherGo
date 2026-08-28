# Third-Party Notices

This file is an audit snapshot for LauncherGo release packages. It is not a
replacement for the license text shipped by each upstream package. When making
a release, keep this file, `NOTICE`, and `LICENSE` in the distributed package.

Audit date: 2026-08-28

## LauncherGo License

LauncherGo source and first-party embedded mods are licensed under GPL-3.0-only.
See `LICENSE`.

## Runtime NuGet Dependencies

The following runtime package inventory was checked from `dotnet list
LauncherGo.slnx package --include-transitive` and local `.nuspec` metadata.

| Component | Version | License metadata found | Source / license |
| --- | --- | --- | --- |
| Avalonia | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Desktop | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.FreeDesktop | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.FreeDesktop.AtSpi | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.HarfBuzz | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Native | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Remote.Protocol | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Skia | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Win32 | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.X11 | 12.0.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | https://github.com/AvaloniaUI/AvaloniaEdit |
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | LICENSE file in NuGet package | https://github.com/AvaloniaUI/angle |
| HarfBuzzSharp and native asset packages | 8.3.1.3 | MIT | https://github.com/mono/SkiaSharp |
| Irihi.Avalonia.Shared | 0.4.0 | MIT | https://github.com/irihitech |
| MicroCom.Runtime | 0.11.4 | MIT | https://github.com/AvaloniaUI/MicroCom |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core | 10.0.7 | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.* packages | 10.0.0-10.0.1 | MIT | https://github.com/dotnet/runtime |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT | https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream |
| Microsoft.VisualStudio.Validation | 17.13.22 | MIT | https://github.com/microsoft/vs-validation |
| System.CodeDom | 10.0.8 | MIT | https://github.com/dotnet/runtime |
| System.Diagnostics.EventLog | 10.0.1 | MIT | https://github.com/dotnet/runtime |
| System.Management | 10.0.8 | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.Pkcs | 10.0.6 | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.Xml | 10.0.6 | MIT | https://github.com/dotnet/runtime |
| System.ServiceModel.Primitives | 10.0.652802 | MIT | https://github.com/dotnet/wcf |
| Tmds.DBus.Protocol | 0.92.0 | MIT | https://github.com/tmds/Tmds.DBus |
| Semi.Avalonia | 12.0.1 | MIT | https://github.com/irihitech/Semi.Avalonia |
| Serilog, Serilog.Enrichers.Thread, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.0.0-10.0.0 | Apache-2.0 | https://github.com/serilog |
| protobuf-net and protobuf-net.Core | 3.2.56 | Apache-2.0 | https://github.com/protobuf-net/protobuf-net |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.core, SQLitePCLRaw.lib.e_sqlite3, SQLitePCLRaw.provider.e_sqlite3 | 2.1.11 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| Nerdbank.MessagePack | 1.3.29-beta | MIT | https://github.com/AArnott/Nerdbank.MessagePack |
| PolyType | 1.4.1 | MIT | https://github.com/eiriktsarpalis/PolyType |
| SkiaSharp and native asset packages | 3.119.3-preview.1.1 | MIT | https://github.com/mono/SkiaSharp |
| ZstdSharp.Port | 0.8.6 | MIT | https://github.com/oleg-st/ZstdSharp |
| NaCl.Net | 0.1.13 | MPL-2.0 | https://github.com/somdoron/NaCl.net |
| AsyncIO | 0.1.69 | Upstream LICENSE.md is MPL-2.0; NuGet metadata does not declare a license field | https://github.com/somdoron/AsyncIO |
| NetMQ | 4.0.4.2 | NuGet license URL points to COPYING.LESSER | https://github.com/zeromq/netmq/blob/master/COPYING.LESSER |
| PDFsharp and PDFsharp-MigraDoc | 6.2.4 | MIT | https://github.com/empira/PDFsharp |

## Development-Only Dependencies

`AvaloniaUI.DiagnosticsSupport` 2.2.1 is referenced with release assets
disabled. Do not publish Debug builds externally without separately auditing
this package, because its NuGet metadata did not expose a license expression or
license file in the local package cache.

Test packages such as `Microsoft.NET.Test.Sdk`, `xunit`,
`xunit.runner.visualstudio`, and their transitive dependencies are not part of
normal runtime release packages.

## Embedded Mods

The embedded mod packages built from this repository are first-party
LauncherGo components:

| Mod id | Package file | License |
| --- | --- | --- |
| launchergoauth | serverauth.dll | GPL-3.0-only |
| launchergoredirect | launchergoredirect.dll | GPL-3.0-only |
| launchergocommandbridge | commandbridge.dll | GPL-3.0-only |

Each standalone embedded mod ZIP should include `LICENSE.txt` copied from the
repository `LICENSE` file.

## Release Audit Notes

PDF export reads DengXian or SimHei from the Windows fonts directory at runtime;
these font files are not copied into LauncherGo release packages. Their OpenType
`fsType` value was checked as `0x0008`, which permits editable document
embedding. Generated PDF documents may contain embedded font data, subject to
the font license supplied with the user's Windows installation.

`NetMQ` should be treated as an LGPL dependency based on its package license
URL. For single-file releases, verify that the packaging model does not remove
rights normally expected for LGPL-covered libraries, such as notice retention
and a practical way to relink or replace the library where required.

Self-contained .NET releases include Microsoft .NET runtime components in
addition to NuGet assemblies. Keep any license or third-party notice files
emitted by `dotnet publish`, and do not overwrite them with project notices.

The repository contains `guidance_interface.gif` without a nearby provenance
note. Confirm that this media is first-party content or add attribution and
license permission before distributing it outside the repository.
