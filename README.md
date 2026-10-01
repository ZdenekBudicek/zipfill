# zipfill

A small C# command-line tool that finishes unpacking a zip that Windows Explorer only partly extracted.

## The problem

A colleague sends a project zipped on **macOS**. Windows Explorer stops with *"The destination path is too long"* or silently leaves files out, because:

- some entries are longer than Explorer's 259-character path limit (a Unity project's build folders easily reach 380+ characters, so no shorter target folder helps);
- names with diacritics are stored decomposed (Unicode NFD) in the zip, while Windows and git use the composed form (NFC).

zipfill reads the zip, compares it with what is on disk and **adds only what is missing**. It never overwrites or deletes anything.

## Usage

```
zipfill scan    <archive.zip> <target-folder> <reports-folder>
zipfill extract <archive.zip> <target-folder> <reports-folder>
zipfill verify  <archive.zip> <target-folder> <reports-folder>
```

- **scan** — report only: files on disk, what is missing (grouped by folder), size mismatches, longest path.
- **extract** — writes each missing file to a temporary file in the same folder, checks its **CRC32 and size**, then renames it into place without overwriting. On failure it removes only its own temporary file.
- **verify** — like scan, plus a CRC32 check of every existing file with the expected size.

`<target-folder>` is the folder the zip was extracted **into**. If nothing from the zip is found there, `extract` refuses to run (exit code 3) — usually a sign of a wrong target folder; add `--prazdny-cil` to extract into an empty folder on purpose. The reports folder must be outside the target; every run creates its own sub-folder there and never overwrites earlier reports. Console messages and code comments are in Czech.

### Skipped on purpose

| Entries | Why |
|---|---|
| `__MACOSX/`, `._*`, `.DS_Store` | macOS metadata; `.DS_Store` inside `.git/refs` even breaks `git fsck` |
| `.git/index.lock` | a stale lock from the Mac would block commits |
| `.git/lfs/tmp/` | git-lfs clears it on start |
| `Temp/`, `Logs/` of a Unity project | Unity deletes or rewrites them when the project opens |
| Unix symlinks | can't be created normally on Windows |

### Exit codes

`0` no differences · `1` something is missing or differs, or an extraction failed · `2` bad arguments · `3` extract refused (nothing from the zip on disk) · `4` fatal error (details on stderr).

## Build and test

Requires the .NET 9 SDK (dependency: `System.IO.Hashing` from NuGet).

```
cd src
dotnet publish -c Release -r win-x64 --self-contained false -o ..\bin
cd ..
powershell -ExecutionPolicy Bypass -File tests\selftest.ps1
```

The regression script builds test zips in `%TEMP%` and checks NFD/NFC names, skipped entries, long paths (including a 240-character file name), no-overwrite behaviour, CRC checks, a wrong target folder, reports inside the target, repeated runs and all exit codes. It prints `VSECHNY KONTROLY PROSLY` ("all checks passed") and exits with 0.

— [Zdeněk Budíček](https://zdenekbudicek.github.io)
