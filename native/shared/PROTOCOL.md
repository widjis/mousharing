# Mousharing native protocol v1

The native applications share this wire contract. The existing Python CLI protocol (version 3, TCP 47831) remains a separate implementation and is not wire-compatible. The Python files here are a test oracle, not an application runtime.

## Roles and ports

- **Master:** the device whose physical mouse/keyboard are shared. It listens on TCP **47832**.
- **Client:** the device receiving input. It connects to its configured master.
- **c2s:** client → master. **s2c:** master → client. These names never change with the operating system.
- Clipboard is bidirectional. Only one peer may control a paired session at a time.
- Discovery uses UDP **47833**. Discovery metadata is untrusted and does not establish identity.

A discovered master advertises only:

```json
{"service":"mousharing-native","v":1,"id":"lowercase-uuid","name":"Device name","port":47832}
```

Do not advertise pairing secrets, clipboard data, session keys, or input events over discovery. A client may fall back to an IPv4 address entered by the user.

## Invitation

The master generates 32 bytes using the operating system cryptographic random generator. The invitation is `MS1.` followed by unpadded base64url of the UTF-8 JSON object:

```json
{"v":1,"id":"master-lowercase-uuid","name":"Master name","port":47832,"key":"standard-base64-32-byte-secret","addresses":["192.0.2.10"]}
```

The secret grants access to the master and must be kept in Keychain on Mac and DPAPI-protected current-user storage on Windows. Copy/paste is the initial pairing UI; no manual certificate copying is required. Do not log, publish, or send the invitation to discovery. A six-digit PIN is not an equivalent substitute for this high-entropy secret.

The client pins the master ID from the invitation, and the master remembers the accepted client ID. A different client ID is rejected until the pairing is explicitly forgotten. Remembered IDs are binding metadata; authentication still requires the secret. Forgetting a master-side pairing rotates its secret and closes the live session. Role changes stop sharing and release all held input before changing the network role.

## Framing and encoding

Each record is one UTF-8 JSON object followed by LF. Maximum frame length is **3 MiB**, including LF. Reject incomplete, oversized, invalid UTF-8, invalid JSON, or wrong-type records. Standard base64 values use the `A–Z a–z 0–9 + /` alphabet and normal `=` padding. Invitation outer encoding alone uses unpadded base64url.

Authenticated envelopes are:

```json
{"seq":1,"nonce":"standard-base64","ciphertext":"standard-base64","tag":"standard-base64"}
```

AES-256-GCM uses a 32-byte key, fresh random **12-byte nonce**, **16-byte tag**, and the exact UTF-8 AAD defined below. `ciphertext` excludes the tag. JSON property ordering and insignificant whitespace are not canonicalized on the wire: encrypt the sender's actual UTF-8 bytes and parse the authenticated plaintext after decryption.

Authentication failure, invalid framing, invalid sequence, malformed payload, incompatible version, or identity mismatch closes the connection and restores local input. Error logs must not contain clipboard contents, invitation secrets, passwords, or plaintext input payloads.

## Authenticated handshake

1. Master sends an unencrypted challenge:

   ```json
   {"type":"challenge","v":1,"challenge":"base64-32-random-bytes","id":"master-lowercase-uuid","name":"Master name"}
   ```

2. Client checks version, challenge length, and master ID against the invitation. It generates its own random 32-byte `clientNonce`.
3. Client sends an envelope with `seq:0`, encrypted with the **invitation secret** and AAD exactly `mousharing-native-v1:hello`. Its plaintext is:

   ```json
   {"type":"hello","v":1,"challenge":"exact-challenge-base64","clientNonce":"base64-32-random-bytes","id":"client-lowercase-uuid","name":"Client name"}
   ```

4. Master authenticates the envelope, validates its challenge echo, client nonce, protocol version, and remembered client identity. It replies with `seq:0`, encrypted with the invitation secret and AAD exactly `mousharing-native-v1:welcome`:

   ```json
   {"type":"welcome","v":1,"challenge":"exact-challenge-base64","clientNonce":"exact-clientNonce-base64","id":"master-lowercase-uuid","name":"Master name"}
   ```

5. Client authenticates welcome and verifies both nonce echoes, version, and pinned master ID before enabling input or clipboard.

The unencrypted challenge is not proof of identity. A peer is authenticated only after the invitation-secret GCM check and the expected nonce/identity checks. Do not read or export the system clipboard before authentication succeeds.

Handshake timeout is **15 seconds**. The protocol must not accept input or session records in place of the handshake records.

### Optional capability negotiation

Hello may contain `"clipboard": true` when the client has enabled text sharing. Welcome returns `"clipboard": true` only when both the master setting and the hello flag are true. A missing flag is false. Do not read, emit, or apply clipboard data when this capability was not negotiated. A toggle during a session must reconnect or clearly take effect on the next session. The isolated crypto fixture records omit these optional flags so their exact bytes remain stable.

## Session derivation and sequences

Compute:

```text
salt = SHA256(rawChallenge32 || rawClientNonce32)
c2sKey = HKDF-SHA256(IKM=invitationSecret32, salt=salt,
                    info=UTF8("mousharing-native-v1:c2s"), length=32)
s2cKey = HKDF-SHA256(IKM=invitationSecret32, salt=salt,
                    info=UTF8("mousharing-native-v1:s2c"), length=32)
```

Each direction starts at **sequence 1**. For direction `c2s` and sequence 17, AAD is exactly `mousharing-native-v1:c2s:17`; `s2c` uses its own direction string and independent counter. Increment receive sequence only after a successful GCM authentication. Require the exact next positive integer sequence; reject duplicates, gaps, reflections, negative/fractional values, and replayed frames. Reset counters only for a fresh authenticated handshake with new challenge/nonces.

Generate a fresh random 12-byte nonce for every encrypted record. Serialize writes per connection so a clipboard update cannot interleave bytes with an input or heartbeat record. Do not share one session key between directions.

Heartbeat is every **1 second**, with a **6-second** session timeout. Disconnect must restore cursor visibility/association and release all synthetic keys/buttons. Switching master or disabling sharing must also stop capture before enabling another source.

## Payloads

All are encrypted session records. `side` is the client's position relative to its master. `ratio` is a finite number in `[0,1]`. `switchId` identifies one transition so stale `leave` replies cannot close a newer remote activation.

| Type | Fields | Direction |
|---|---|---|
| `enter` | `side`: `left/right/top/bottom`, `ratio`, `switchId`: integer | s2c |
| `move` | `dx`, `dy`: finite numbers; `edgeSwitch`: boolean | s2c |
| `button` | `name`: `left/right/middle`; `down`: boolean | s2c |
| `key` | `name`, `down`: boolean | s2c |
| `scroll` | `dx`, `dy`: finite numbers | s2c |
| `leave` | `ratio`, `switchId` | c2s |
| `reset` | none | s2c |
| `ping` | none | either |
| `pong` | none | either |
| `clipboard` | `text`, `clock`, `origin` | either |

Special key names are `ctrl`, `ctrl_r`, `shift`, `shift_r`, `alt`, `alt_r`, `cmd`, `cmd_r`, `enter`, `tab`, `space`, `backspace`, `esc`, `delete`, `home`, `end`, `page_up`, `page_down`, `left`, `right`, `up`, `down`, `caps_lock`, and `f1` through `f12`; printable keys use lowercase characters.

The client accepts mouse/keyboard input only during an active `enter` session. After `leave` or `reset`, queued input is ignored until the next valid `enter`. The master suppresses local input while forwarding. Synthetic input must not be re-captured and forwarded again. Safety escape/pause remains available locally.

### Clipboard

Only plain text is synchronized, with a maximum of **256 KiB encoded UTF-8**. Images, files, NUL-containing text, and larger payloads are not synchronized. Normalize CRLF to LF for comparison. Do not send clipboard contents that predate the authenticated session; establish a local baseline first.

Each clipboard message has a positive integer Lamport `clock` and a lowercase UUID `origin`. On a new local copy, increment the clock and send `(clock, origin, text)`. On reception, advance the local clock to at least the received clock and apply only revisions newer than the current `(clock, origin)` tuple, comparing the origin string lexicographically to break equal-clock ties. Received clipboard writes update the local baseline so they are not echoed. Retry a busy system clipboard without blocking input capture. Do not log or persist the clipboard text.

## Deterministic interoperability vectors

`fixtures.json` contains **public test secrets only**. Regenerate it with:

```sh
.venv/bin/python native/shared/generate_fixtures.py
```

The fixture invitation key is bytes `0..31`, challenge bytes `32..63`, and client nonce bytes `64..95`. It provides salt, both derived session keys, an invitation, and hello/welcome/c2s-clipboard/s2c-enter records. For exact encryption comparisons use `plaintextBase64`, not reserialized JSON, since serializers can choose different Unicode escaping and property ordering.

`protocol_reference.py` is the bounded framing/encryption/handshake test oracle. `test_protocol.py` verifies vectors, wrong-key/AAD/tag rejection, strict sequences, replay across sessions, handshake nonce/identity checks, and invite/frame bounds. Native implementations must consume the same vectors and pass their own cryptographic tests in CI; a Python-only pass is not proof that both native applications interoperate.

## Cross-runtime implementation checks

`native/shared/dotnet/Mousharing.ProtocolChecks.csproj` links the production Windows `SecureChannel.cs` and `Invitation.cs` source without WinUI dependencies. Build it with `dotnet build native/shared/dotnet -c Release`, then run `.venv/bin/python native/shared/cross_runtime.py`. It verifies the actual .NET HKDF/AES-GCM/framing code against the fixture and exchanges encrypted Unicode clipboard payloads over TCP in both C#-master/Python-client and Python-master/C#-client roles. This harness never reads or modifies the system clipboard or captures input.

`swift_loopback.py` starts the actual Swift app executable with its no-GUI/no-Keychain `--protocol-test-client` mode, authenticates it against a Python master, and exchanges encrypted Unicode payloads through `NativeSession`. Run `.venv/bin/python native/shared/swift_loopback.py` after a debug Swift build. The fixture invitation passed to the test process contains only the public deterministic test key.
