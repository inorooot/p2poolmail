# p2poolmail

Email alerts and status reports for [P2Pool](https://github.com/SChernykh/p2pool) Monero mining.
Get mining alerts and events automatically, or send an email anytime, anywhere to request your latest mining status.

## Supported Notifications

| Event/Alert | Notification (example values) | Trigger condition |
| ----------- | ------------------------------ | ----------------- |
| SHARE FOUND | SHARE FOUND: mainchain height 3732699, sidechain height 14884402, diff 3475318407, client 192.168.0.111:36772, user x, effort 67.239%. | Log line contains `SHARE FOUND`; instant delivery. |
| Payout      | Your wallet  got a payout of 0.001873661100 XMR in block 3732713. | Log line contains `got a payout`; instant delivery. |
| Daemon      | Monerod is not synchronized. | Keyword hits ≥ 3 in 30 s (or 3 slow bursts); one alert, recovery email after 30 s silence. |
| Daemon      | Monerod is busy syncing. | Same as above. |
| Daemon      | JSONRPCRequest uv_poll_start returned error EBADF. | Same as above. |
| Daemon      | P2PServer ZMQ is not running. | Same as above. |
| Worker      | Previous:5 current:2 trend:Down | Smoothed worker count change confirmed for 5 s. |
| Daily stats | Hello workers, Here's what happened in the last 24 hours:<br>Received    : 0.013581564721 XMR (6 payment(s))<br>Share found : 5<br>Current:<br>Total worker: 2<br>Hashrate_15m: 27.109 KH/s<br>Hashrate_1h: 26.939 KH/s<br>Hashrate_24h: 26.042 KH/s<br>Average effort: 37.197%<br>Current effort: 58.440% | Scheduled at `[daily_stats].time_of_day`, every 24 hours. |

### On-demand request

Besides automatic notifications, you can proactively request your latest mining status at any time:
just send an email to the mailbox watched by `[imap_server]`, and p2poolmail will reply with a full statistics report.
and when `[imap_server].reply_allowlist` is non-empty, only listed senders get a reply.

## How it works

 It watches `p2pool.log` and the local mining statistics, and turns what it sees into plain emails. No database, no web UI — just set it up and check your inbox.

## Requirements

p2poolmail requires [P2Pool](https://github.com/SChernykh/p2pool/blob/master/docs/COMMAND_LINE.MD) to be started with **both** of these options:

```sh
./p2pool ... --stratum-api --data-api <data_api_dir>
```

- `--stratum-api` — enables recording of hashrate, effort, and worker details.
- `--data-api <data_api_dir>` — tells P2Pool where to write its mining data files. Use the same value as `data_api_dir` in `Config.toml`.

## Configuration

The application loads `Config.toml` from its current working directory at startup.  
Update the paths and mail settings before starting the service:

```toml
[p2pool_log]
file_path = "/path/to/p2pool/p2pool.log" # Path to the P2Pool log file
data_api_dir = "/path/to/data_api_dir/"  # Point to the P2Pool data API directory (--data-api <data_api_dir>)

[smtp]
host = "smtp.example.com"                # SMTP server hostname
port = 465
useSsl = true                            # Enable SSL for the SMTP connection
username = "sender@example.com"          # SMTP login username (also used as the From address)
fromName = "p2poolmail"                  # Display name shown to recipients
password = "YOUR_SMTP_CREDENTIAL"        # Use the credential required by your provider (OAuth2 unsupported)

[receiver]
toAddress = "alerts@example.com"         # Address that receives notifications

[notify_event]
share_found = true                        # Notify when a share is found
got_payout = true                         # Notify when a payout is received
worker_down_up = true                     # Notify when a worker goes up or down

[imap_server]
enable = false                            # Enable email-triggered statistics reports
host = "imap.example.com"                 # IMAP server hostname
port = 993                                # IMAP port
useSsl = true                             # Enable SSL for the IMAP connection
username = "receiver@example.com"         # IMAP login username
password = "YOUR_IMAP_CREDENTIAL"        # Use the credential required by your provider (OAuth2 unsupported)

[keepalive]
enable_remote_ping = false                # Enable periodic requests to a remote URL
interval_minutes = 10                     # Interval between keepalive pings
ping_url = ""                             # URL used when remote ping is enabled

[daily_stats]
enable = true                             # Enable scheduled daily statistics
time_of_day = "18:00"                     # Local time for the report (HH:mm)
frequency_hours = 24                      # No changes required
```

### Security notes

- **Use a dedicated email account for p2poolmail.** It's best to create a brand-new mailbox just for the SMTP and IMAP services in this app, instead of reusing your personal email. This keeps your main inbox and credentials isolated: if anything leaks or gets misconfigured, only this throwaway account is affected.
 
## Build

p2poolmail is a .NET 10 console application and is published as a self-contained native binary (Native AOT).

**Prerequisites:**

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Native AOT toolchain:
  - **Linux (Debian/Ubuntu):** `sudo apt install clang zlib1g-dev`
  - **Windows:** Visual Studio 2022 with the "Desktop development with C++" workload
  - **macOS:** Xcode command line tools (`xcode-select --install`)

**Build (debug):**

```sh
cd p2poolmail
dotnet build
```

**Publish a release binary:**

```sh
cd p2poolmail
dotnet publish -c Release
```

The output binary and a default `Config.toml` are written to `bin/Release/net10.0/<rid>/publish/` (e.g. `bin/Release/net10.0/linux-x64/publish/`). Copy that folder anywhere, edit `Config.toml`, then run the binary from that directory.

To cross-compile for another platform, add the runtime identifier:

```sh
dotnet publish -c Release -r linux-x64   # or win-x64, osx-arm64, ...
```
