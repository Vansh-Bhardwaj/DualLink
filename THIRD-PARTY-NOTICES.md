# Third-party notices

DualLink installs the following unmodified, separate third-party components as prerequisites.

## ProxiFyre 2.5.0

- Copyright: WireSock contributors
- License: GNU Affero General Public License, version 3
- Source code for the exact bundled version: https://github.com/wiresock/proxifyre/tree/v2.5.0
- Upstream project: https://github.com/wiresock/proxifyre

The complete license is installed as `licenses/ProxiFyre-AGPL-3.0.txt`. DualLink does not modify or link against ProxiFyre; it configures the separately installed Windows service through its documented configuration file and Service Control Manager interface.

## Windows Packet Filter driver 3.6.2.1

- Publisher: NT Kernel Resources / WireSock
- Binary license: free for personal, educational, and nonprofit use
- Commercial use: requires an appropriate Windows Packet Filter license
- License information: https://www.ntkernel.com/windows-packet-filter/licensing/
- Official release: https://github.com/wiresock/ndisapi/releases/tag/v3.6.2

The offline installer contains the unmodified x64 driver MSI. The MIT license of the `ndisapi` source repository does not replace the separate terms applied to the precompiled Windows Packet Filter driver. Do not redistribute or use the bundled installer commercially without obtaining the required license.

## ndisapi user-mode source

- Copyright (c) 2018 Vadim Smirnov
- License: MIT
- Source code: https://github.com/wiresock/ndisapi

The complete MIT license is installed as `licenses/Windows-Packet-Filter-MIT.txt`.

## Microsoft .NET Runtime 10.0.11

DualLink's self-contained executable includes the Microsoft .NET Runtime. The runtime is MIT-licensed and contains third-party components under their respective terms.

- Source code: https://github.com/dotnet/runtime/tree/v10.0.11
- License: `licenses/dotnet-runtime-MIT.txt`
- Complete notices: `licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt`

## Microsoft Visual C++ Redistributable

The installer includes Microsoft Visual C++ Redistributable x64 version 14.44.35211.0, unmodified. Its use and redistribution are governed by the Microsoft Software License Terms displayed by the package and documented at https://visualstudio.microsoft.com/license-terms/.

## Inno Setup

Inno Setup is used to build the installer and is not installed with DualLink. The published installer was produced for a free, noncommercial release. Commercial users must comply with Inno Setup's current commercial licensing request: https://jrsoftware.org/ishelp/topic_purchase.htm.
