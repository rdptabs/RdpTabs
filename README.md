# RdpTabs — a Remote Desktop client with Chrome-style tabs

Windows' own mstsc works well; the one thing it lacks is **tabs**. Connect to three machines and you get three
windows to hunt for on the taskbar.

RdpTabs keeps the Remote Desktop engine that ships with Windows and replaces only the shell: a Chrome-style
tab strip on top, `+` for a new connection, one window for every session.

**[rdptabs.github.io](https://rdptabs.github.io/)** — click through the tabs before you download.

![Two sessions in tabs, the first one active](docs/tabs.jpg)

![The new connection page](docs/new-tab-page-dark.png)

## Download

Grab `RdpTabs.exe` from the [latest release](https://github.com/rdptabs/RdpTabs/releases/latest) and run it.
One 457 KB file — no installer, no runtime to download, nothing left behind but a settings file.

It is not code-signed, so the first run shows "Windows protected your PC" → *More info* → *Run anyway*.

## Using it

| Action | Result |
|---|---|
| Click `+` | New tab with the "New connection" page |
| Quick-connect box | `10.0.0.5`, `10.0.0.5:3390`, `user@10.0.0.5`, `CORP\user@host:3390` |
| Click a tab / middle-click / drag | Switch / close / reorder |
| Right-click a tab | Disconnect, reconnect, edit, save as connection, close others |
| Drag the empty strip area | Move the window — snapping and double-click-to-maximize work as usual |
| Saved connection card | Click to connect, right-click to edit or delete |

Resize or maximize the window and the remote desktop is told its new size, so the picture stays sharp instead
of being stretched. "Advanced..." opens the full editor: view mode and scale, colour depth, redirection,
experience tier, Windows keys, server authentication and RD Gateway.

Start a session straight from a shortcut:

```bash
RdpTabs.exe 10.0.0.5 10.0.0.6 db@10.0.0.7
```

An address that matches a saved connection reuses its settings and stored password.

`Ctrl+T` / `Ctrl+W` / `Ctrl+Tab` / `Ctrl+1`…`Ctrl+9` work while focus is **outside** a remote session. Once you
click into the remote picture the keyboard belongs to that session — switch tabs with the mouse.

## Passwords

Settings live in `%APPDATA%\RdpTabs\profiles.json`. Tick "Save password" and it is encrypted with Windows
DPAPI under your account, so the file holds ciphertext and no other user or machine can read it. Leave the box
unticked and Windows will prompt you instead.

⚠️ If you cancel that Windows password prompt, the Remote Desktop control wedges and never reports an error.
RdpTabs times out after 30 seconds and tells you, rather than spinning forever.

## Not in this version

No full screen, no Ctrl+Alt+Del, no tearing a tab out into its own window, no .rdp import.

## Build it yourself

```bash
build.cmd
```

No .NET SDK needed — it uses the Roslyn compiler that ships with Visual Studio and writes `bin\RdpTabs.exe`.
`build.cmd test` runs 22 self checks that need no RDP server. `dotnet build -c Release` also works if you have
the SDK.

If you are going to touch the code, read [docs/notes.md](docs/notes.md) first: it records the parts of the
Remote Desktop ActiveX control that behave counter-intuitively and cost real debugging time.

## License

[Apache 2.0](LICENSE). One carve-out: `RdpTabs.ico` is a copy of Microsoft's Remote Desktop icon and stays
theirs — see [NOTICE](NOTICE).
