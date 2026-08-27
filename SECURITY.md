# Security notes for this repo

## Never commit a game save

FTK2 save state embeds the **Steam64 id of every player who was in the room**, along with their
character names. A save captured from a co-op session is therefore other people's personal data,
and they did not agree to have it published.

This is not a guess about the format. The vendor's own `NetworkDesyncReport\*_STATE_AFTER_DESYNC.json`
dumps are plaintext serializations of the same state, and each one from a co-op session contains
8-13 distinct Steam64 ids.

**Rules:**

- `*.ftk2` and `*.sav` are gitignored. Do not force-add them.
- Keep fixtures outside the working tree. The default location is
  `%USERPROFILE%\Backups\ftk2-fixtures`; override with the `FTK2_FIXTURE_DIR` environment variable.
- `tools\deploy.ps1` excludes `FTK2.Crucible\data\Fixtures` from the staged payload, and refuses
  to zip a payload containing any `*.ftk2` / `*.sav` at all. The exclusion is the fix; the refusal
  is the backstop, because the exclusion is one line in one of seven `Copy-Tree` calls and the
  next person to add an eighth will not remember this file.

## What happened once, so it does not happen twice

`FTK2.Crucible/data/Fixtures/overworld-four-classes/run.ftk2` -- a 5.4 MB live co-op save -- was
committed in `0eb4d67` and carried on `class-pack-hardening`. The staging block copied all of
`FTK2.Crucible\data` into the payload unconditionally, with no `-Mods` gate, and the packaging step
zipped the whole payload.

The zips actually handed to testers (`FahrulIrregulars-*.zip`) did **not** contain it, because
Crucible was stripped from those by hand for unrelated reasons. One raw package build
(`ftk2mods-20260826-194928-d9baab7.zip`) did contain it and was deleted locally.

Two independent failures had to line up: a real save living inside the repo, and a staging path
that shipped a whole directory rather than named files. The fixes above address both.

## Before sharing any build

The packaging tripwire covers saves. It does not cover everything, so when a build is going to
someone else, also confirm the payload has no `BepInEx\config\*.cfg` carrying a local absolute
path, and no log files.
