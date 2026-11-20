# Privacy

Open-LeanPrint collects nothing and sends nothing about you. There is no
telemetry, no analytics, no account, and no server belonging to this project.
Nothing you print, open or configure reaches its author or anybody else.

The one exception is the **update check** of this fork, described under
[The network](#the-network): it asks GitHub which version is the newest, and
you can switch it off.

That said, a tool that catches your print jobs necessarily handles your
documents. This is what it does with them, so you can decide for yourself
whether the arrangement suits you.

## Your documents

Printing into the Open-LeanPrint printer writes the job to disk as a PDF — it is
exactly the document you printed. The file is a hand-over to the app, not an
archive:

| | |
|---|---|
| Where it lands | `%ProgramData%\Open-LeanPrint\captured\<your SID>` when the Windows service catches it, `%LOCALAPPDATA%\Open-LeanPrint\captured` when the app or the console host does |
| How long it stays | until the app reads it into its job pool, which deletes it immediately |
| If no app is running | it waits, and the service removes it after 7 days or 500 MB, whichever comes first (the size limit spares jobs younger than a day) |
| When you uninstall | the folder is removed |

PDFs you open or drag in yourself are read where they are and never moved,
copied or deleted.

**On a machine with several users**, each user's jobs wait in a folder of their
own that only they (and administrators) can open; the others can neither read nor
remove them. A job that names no account of the machine is refused rather than
stored where anyone could see it. If the folder is inside a synced
folder such as OneDrive, the files will be uploaded by that service under its own
terms, not ours.

## What is written down

Two plain-text logs, both local, neither sent anywhere:

- `%ProgramData%\Open-LeanPrint\service.log` — when the service started, and one
  line per captured job: time, the account name the job came from, its size, the
  file it was written to, and what the job asked for (sides, colour).
- `%APPDATA%\Open-LeanPrint\app.log` — when the app started, under which account
  and session, and any crash. It exists because "is it even running, and as
  whom?" is otherwise unanswerable after the fact.

Your settings and saved layouts live in `%APPDATA%\Open-LeanPrint\settings.json`.

Delete any of these at any time; the program recreates what it needs.

## The network

The only listener is on loopback: `http://localhost:6310/leanprint`, which is
how Windows' own IPP class driver hands print jobs over. It is meant for the
local machine and should not be exposed to a network.

The only outgoing connection is the **update check**. Each time the app starts -
and when you choose *Check for updates…* in the tray menu - it sends one request
to GitHub's public API, `api.github.com/repos/MartinKammler/Open-LeanPrint/releases/latest`.
The request carries the app's name and version (`Open-LeanPrint/0.5.1`) in its
User-Agent, because GitHub refuses requests without one, and nothing else: no
identifier, nothing about the machine, nothing you printed. GitHub sees your IP
address, as any website you visit does; what it does with it is governed by
[GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

If a newer version exists, the installer is downloaded from that release only
after you agree, checked against the size and SHA-256 that GitHub publishes for
it, and run. *Check for updates at start* in the tray menu switches the
automatic check off; nothing then leaves the machine unless you ask.

## Questions

Open an issue at
[github.com/MartinKammler/Open-LeanPrint](https://github.com/MartinKammler/Open-LeanPrint/issues),
or for anything security-related see [SECURITY.md](SECURITY.md).
