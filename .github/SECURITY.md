# Security Policy

These plugins run **inside the Final Fantasy XIV client process**, loaded by
Dalamud. That is a different risk profile from an ordinary desktop app: a bug
here can crash the game client, and anything the plugin parses is parsed in that
process. The attack surface is small - the plugin reads game memory and Excel
sheets, writes JSON to its own config directory, and publishes a read-only IPC
gate that other plugins can call - but reports touching any of it are taken
seriously.

## Supported versions

The latest release on the `main` branch is supported. Fixes land there first and
ship in the next tagged release.

| Version | Supported |
| ------- | --------- |
| 0.x     | Yes (pre-release; no compatibility promises) |

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report privately via either:

- GitHub's [private vulnerability reporting][advisories] - the **"Report a
  vulnerability"** button under the repository's *Security* tab (preferred), or
- email to **braininblack@gmail.com** with `[FFXIVPlugins security]` in the
  subject.

Please include:

- the plugin version and the Dalamud API level you are on,
- whether the Umbra widget is installed,
- a description of the issue and its impact, and
- a reproduction if you have one - for parsing bugs, the smallest file or IPC
  payload that triggers it.

You can expect an acknowledgement within **5 business days**. Once confirmed,
we will agree on a disclosure timeline with you, prepare a fix, and credit you in
the release notes unless you prefer to stay anonymous.

## Scope

In scope - vulnerabilities **in this code**, for example:

- a crafted or corrupt history file in the plugin's config directory that leads
  to anything worse than the file being rejected (it is read at login, and a
  broken one is expected to be set aside, not to take the client down),
- a payload passed *into* the plugin over its IPC gates that causes a crash or
  unintended write,
- the IPC gates exposing more than the progress snapshot they document, or a
  path where they can be made to write rather than read,
- unsafe pointer use in `Msq/QuestState.cs` that can fault the client on
  ordinary game state (a null `QuestManager` during a zone change, for example),
- the plugin writing outside its own `ConfigDirectory`.

Out of scope:

- **Dalamud plugins are not endorsed by Square Enix, and using them is against
  the game's Terms of Service.** That is a known, accepted property of the entire
  ecosystem, not a vulnerability in this repo.
- Crashes caused by a Dalamud or game version this release does not target -
  those are compatibility bugs; open a normal issue.
- Vulnerabilities in Dalamud, Umbra, FFXIVClientStructs, or the game client
  itself. Report those to their own projects.
- The measured history being stored unencrypted in the plugin's config directory.
  It is quest names and durations on your own machine; treating it as a secret is
  not a goal.
- Anything requiring an attacker who already has write access to your Dalamud
  config directory or can run code as your user - at that point the plugin is not
  the weakest link.

[advisories]: https://github.com/BrainInBlack/FFXIVPlugins/security/advisories/new
