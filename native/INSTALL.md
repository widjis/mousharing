# Install Mousharing 0.1.0

This is the first native preview release. macOS ARM64 (Apple Silicon, macOS 14+) and Windows x64 (Windows 10 build 17763+, Windows 11 recommended) are packaged. The installers are not publisher-signed; macOS is not notarized. Do not treat this preview as a signed public distribution.

## Mac

Quit Mousharing before installing. Open `Mousharing-0.1.0-macOS-arm64.pkg` and follow Installer. It installs `/Applications/Mousharing.app` and does not start sharing, alter pairing, or grant permissions. Launch that copy from Applications. Grant Accessibility and Input Monitoring to that installed copy when macOS asks. The old source-folder copy should stay closed to avoid duplicate instances and confusing permission entries. Installation may require an administrator account. If macOS blocks the unsigned installer, do not disable Gatekeeper; this preview needs an appropriate local approval or a future signed release.

Remove the app through Finder when it is no longer running. Pairing, Keychain entries, and settings are retained; use the app's explicit pairing reset if you want to revoke a peer.

## Windows

Quit Mousharing from its system tray menu before upgrading. Run `Mousharing-0.1.0-Windows-x64-Setup.exe`. The per-user installer needs no administrator privileges, includes the .NET/WinUI runtime, and creates a Start menu shortcut. A desktop shortcut is optional. It does not change firewall rules, enable startup, pair devices, or start sharing. The optional launch checkbox is off by default. Windows can warn that the publisher is unverified; publisher signing is not included in this preview.

Default location: `%LOCALAPPDATA%\Programs\Mousharing`. Uninstall from Windows Settings → Apps. Configuration and DPAPI-protected pairing in `%LOCALAPPDATA%\Mousharing` are retained, allowing reinstall without a new invitation under the same Windows account.

## First connection

Open Mousharing on both devices. Choose the device providing your mouse/keyboard as master; choose client on the other. Copy the invitation directly between your own device GUIs, not into issue reports. Start sharing on the master and connect the client. Escape returns control to the master; F8 switches manually. Pause or Quit stops sharing. Closing Windows to tray keeps sharing active.

## Build installers

- Mac: `bash native/macos/package-installer.sh` (does not install or replace the running source-folder app).
- Windows: publish with `native/windows/build.ps1`, then `native/windows/package-installer.ps1 -PublishDirectory native/windows/dist/win-x64`. Inno Setup 6 must be available on the packaging machine. CI uses the Windows runner's installed compiler.
- CI tests Windows silent install → installed WinUI startup with sharing disabled → uninstall. This is separate from physical input, network, permissions and clipboard testing.
