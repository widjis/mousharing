# Mousharing native preview

Two platform-native applications share one authenticated protocol:

- **Mac:** SwiftUI/AppKit, CoreGraphics input, NSPasteboard, Keychain, menu bar.
- **Windows:** C#/WinUI 3, Win32 input/raw mouse, native clipboard, user-scoped DPAPI, system tray.

The applications run without Python. The existing Python CLI is retained as a separate, working prototype and test oracle. Native apps connect to each other, not to the Python CLI receiver.

## Daily setup

1. Open Mousharing on both laptops and put them on the same private LAN.
2. On the laptop with the primary mouse/keyboard, choose **Bagikan mouse & keyboard saya** (master).
3. On the other laptop, choose **Gunakan perangkat lain** (client).
4. Start the master, copy its invitation, and paste it into the client. Discovery helps select the matching master address; a manual address field is available when the LAN blocks discovery.
5. Choose the client's physical position relative to the master: left, right, above, or below.
6. Connect. The pointer crosses the shared edge and keyboard input follows. Escape returns to the master; F8 is the manual switch.

The invitation is an access credential. Copy/paste it only to the intended paired laptop. No certificate files or pairing password need to be entered at every connection. Secrets are stored by each OS, separately from normal settings. Forgetting/renewing the invitation stops the previous trust relationship and requires pairing again.

The master can be either Mac or Windows. To change the physical source, pause both apps, select the new roles, and pair to the new master's invitation. There is one active master and one client; the apps do not elect or take over master automatically.

## Pause and recovery

**Jeda sharing** stops the session, input forwarding, clipboard, and reconnect attempts. Starting again uses saved configuration. Client reconnection uses bounded retry delays; all retries authenticate the expected peer. Disconnect, quit, role changes, and capture failure restore local cursor/input and release injected held keys/buttons. The master returns to waiting for its paired client.

Mac requires Accessibility to inject input and additionally Input Monitoring when it supplies input. The app includes permission guidance; macOS grants must be completed by the user. Closing the settings window leaves the menu bar/system tray available for reopening or stopping sharing.

## Clipboard

New plain text copied after the authenticated session begins can synchronize in both directions, up to 256 KiB UTF-8. It is enabled only when both endpoints permit it. Images, files, and rich formatting are not transferred. Contents are not written to application logs or stored as clipboard history. Mac uses Command+C/V; Windows uses Control+C/V, including when its input comes from a Mac keyboard.

## Build

No SDK is installed automatically by these instructions.

### macOS

Requires a Swift toolchain and macOS SDK supporting macOS 14+:

```sh
bash native/macos/build-app.sh
open native/macos/dist/Mousharing.app
```

The development `.app` is ad-hoc signed. Signing/notarization for public distribution is a separate release step. The build script keeps its output under the project and does not install into Applications.

### Windows

Requires the .NET 10 SDK, Windows SDK, and compatible WinUI build tools:

```powershell
dotnet publish native/windows/Mousharing.Windows/Mousharing.Windows.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o artifacts/windows-x64
```

Launch `Mousharing.Windows.exe` from the complete publish folder; its DLLs and runtime files must remain alongside it. CI produces a development artifact with this folder. Building the app is not a claim that an installer or public signing has been completed.

.NET 10 is the chosen LTS target; see [Microsoft's support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Verification

See [shared/PROTOCOL.md](shared/PROTOCOL.md), [shared/fixtures.json](shared/fixtures.json), and the repository's **Native apps** CI workflow. Tests cover deterministic AES-GCM/HKDF vectors, framing, identity/nonce binding, replay rejection, and cross-runtime socket exchanges. They must exercise the actual native cryptographic implementations, not just the Python reference.

Before accepting a release, test actual input on both laptops in both master directions, clipboard, mouse drag, hotkeys, sleep/wake, pause/resume, pairing renewal, network loss, role changes, cursor visibility, and release of held keys. UI/build/crypto tests do not prove those physical behaviors.

## Current scope

Development preview for one primary display on each laptop and one active paired client. Native packaging, discovery, pairing, and configurable roles are separate from the Python CLI's protocol. Multi-monitor geometry, transferring files/images, UAC/secure-desktop input, and public distribution signing are outside this initial preview.

For a blocked LAN, the native protocol uses TCP 47832 and discovery uses UDP 47833. The Python CLI uses TCP 47831. Restrict network access to the private network; no internet-facing listener setup is needed.
