# Softbody Manager

A small window that turns Lenki's Resonite addons on and off, installs the latest Softbody build, and checks the renderer
fork. Double-click `Testresolink\SoftbodyManager.exe` (it needs the .NET 10 desktop runtime, which the SDK installs).

## What it does

- **On / off** (Softbody plugin, ResoniteModLoader): renames `Resonite\Libraries\X.dll` to `X.dll.off` and back. The Steam
  launch option `-LoadAssembly Libraries/X.dll` then finds nothing and Resonite skips it without a word, so the launch
  options never change. With Softbody off, your data model matches everyone's and you can join public sessions.
- **Install latest build** (Softbody): copies `Softbody\bin\Release\net10.0\Softbody.dll` into `Libraries` and turns it on.
  Whatever it replaces goes to `parked_addons\Libraries\Softbody.dll.<version>.bak`, and the install is logged in
  `parked_addons\README.txt`, the same way installs were done by hand.
- **Renderer fork**: says whether `RendererSoftbody` still matches Resonite's renderer (the Renderite commit in
  `Renderite.Unity.dll`). After a Resonite update it shows "out of date"; **Rebuild fork** runs `make_softbody_renderer.py`.
  The fork is never switched off: it can only be left out by editing the launch options, and it is harmless with the
  plugin off.
- **Launch options** are shown read only (from Steam's `localconfig.vdf`); a row notes when an addon is not in them.
- **Launch Resonite** asks Steam to start it (`steam://rungameid/2519830`).

Nothing can be switched while Resonite is running. Versions come from each DLL's file version: Resonite's weaver stamps
the weave time over the assembly version of `Libraries\Softbody.dll` on its first start.

## Friend packs

    python make_friend_pack.py      # -> ..\exports\SoftbodyFriendPack-<version>.zip

A self-contained manager (no .NET needed), the Softbody DLL that is installed here (exactly: checked against the sha256 in
`parked_addons\README.txt`, or the untouched `parked_addons\Libraries\Softbody.dll.<version>.installed` the manager keeps on
install), the patched `Renderite.Unity.dll` from `RendererSoftbody` (it must match the installed Resonite renderer), Renderite's
MIT licence, `payload\manifest.json` and `FRIEND-README.txt` as `README.txt`. Next to a pack, the manager runs in pack mode:
installs from it, keeps backups in `Resonite\SoftbodyBackups`, makes the fork at `Resonite\RendererSoftbody` from the friend's
own renderer (only if the Renderite commit matches; it never deletes a folder it did not make), and copies the launch options
the friend needs. A pack fits one Resonite version: after a Resonite update, a new pack.

## Updates from GitHub (1.1.0)

Friends' managers update themselves from the latest release of a public GitHub repository (owner/name in
`update-repo.txt`), so a new version no longer needs a new zip:

- **Each release** has `update.json` (the pack's manifest, signed), `Softbody.dll`, `Renderite.Unity.dll`,
  `SoftbodyManager.exe` and the whole pack's zip (for new friends). The manager reads
  `https://github.com/<repo>/releases/latest/download/update.json` when it opens; with a newer release for the friend's
  Resonite renderer, **Update** downloads the files into `payload.new`, checks each one's sha256 against the signed manifest,
  then swaps the payload (the old one stays as `payload.previous`), installs Softbody (on or off, as it was; backups and log
  as on every install), puts the new patch into the renderer fork (the old DLL goes to `SoftbodyBackups\Renderer`; a fork
  from before a Resonite update is made again) and, when the release has a newer manager, swaps the running exe (renamed to
  `.old`, deleted on the next start) and restarts.
- **Signing:** ECDSA P-256. `dotnet run --project publisher -- keygen` makes the key once: the private key in
  `%APPDATA%\SoftbodyPublisher\update-signing-key.pem` (it never leaves this PC: back it up; if it is lost, friends need one
  new zip), the public key in `update-key.pub.txt`, which is built into the manager. A release that is not signed with it
  is refused, so a stolen GitHub login cannot send code to friends.
- **Making a release:** `python make_friend_pack.py --notes "what is new"` builds the manager with the repository and the
  key, makes the pack as before and puts the release's files in `exports\release-<tag>` (tag `v<version>`; a tag is
  published once, so a new pack of the same version needs `--tag v0.25.0-r2`). Without `update-repo.txt` and
  `update-key.pub.txt`, the pack is made as before, with no updates.
- **Publishing:** `python publish_release.py <tag> --stable` or `--unstable` (1.2.0: one of the two is needed) checks the
  signature and every checksum, then uploads the files with the GitHub CLI (`winget install --id GitHub.cli`, then
  `gh auth login` once): as the new latest release, or as a pre-release. See below.
- `SOFTBODY_UPDATE_TEST_DIR=<folder>` (test copies of the two files and a private key) and `--out <folder>` try the whole
  flow without the real key.

## Stable and unstable builds (1.2.0)

Lenki's idea: friends get the builds Lenki tested, and only those who ask get the newest one before it is tested.

- **Stable** is GitHub's latest release (what `releases/latest` points at; managers 1.1.0 read only this one, so they never
  see an unstable build). **Unstable** is a pre-release, which GitHub never makes the latest. Nothing in the signed manifest
  says which a release is, so making one stable needs no new upload and no new signature.
- **Publishing:** `publish_release.py v0.27.0 --unstable` uploads it as a pre-release; `publish_release.py v0.27.0 --stable`
  then makes it stable (an edit on GitHub, nothing uploaded again). A new tested release goes straight out with `--stable`.
  A stable release that turns out broken: `--unstable` on it takes it back (the newest other stable release is made the
  latest first). It refuses to make a release stable that is older than the latest one (that would send everyone back), a
  draft, or taking back the only stable release; `--dry-run` shows what it would change. The same can be done by hand on
  GitHub: edit the release, tick or untick "Set as a pre-release" and "Set as the latest release".
- **The manager** (`Updater.CheckAllAsync`) reads the latest release as before and GitHub's list of releases
  (`api.github.com/repos/<repo>/releases`, 60 requests an hour per network without signing in; if it fails, the stable
  check still works). The newest release with an update.json (by the tag's version, then its repack number `-rN`), when it
  is newer than the stable one, is the unstable build; its signed manifest must carry its own tag. `Updater.Offer` decides
  what the window shows: on the stable channel, Update takes the stable release and a second card offers **Try unstable**;
  on the unstable channel (after Try unstable; kept in `update-channel.txt` next to the pack), Update takes the newest and
  the card says so, with **Back to stable**. On an unstable build it is hard to miss: an UNSTABLE badge next to the title,
  the card tinted with a warning sign, and "(unstable build)" in the window's title (and so in the taskbar). Back to stable installs the stable release when the pack is newer than it (an
  older Softbody goes in like a newer one, the newer one to the backups; the manager itself never goes back), then the pack
  takes stable builds again. A pack newer than the stable release on the stable channel (one Lenki took back, or an
  unstable zip) gets the Back to stable card too.
- Friends on different builds can't be in a session together when the compatibility hash differs; the Try unstable
  question says so.

## The look (1.1.0)

Dark by default (light with the sun/moon button at the top right; the choice is kept in
`%LOCALAPPDATA%\SoftbodyManager\theme.txt`). A friend pack shows the simple view (`MainForm.Friend.cs`): Resonite's state,
the update banner, the plugin's switch, the setup checklist (it folds away when all three steps are done), Launch Resonite,
and Details (paths, checksums, RML, the log). Lenki's view (`MainForm.Dev.cs`) has the same cards and colours. The window
fits its content until it is resized by hand. `SoftbodyManager.exe --snapshot <png>` draws it to a picture, with
`--theme dark|light`, `--details`, `--show-update`, `--up-to-date`, `--unstable-offer` or `--on-unstable` (made up; nothing
is fetched), and `--game <folder>` `--launch-options <text>` for states the real ones are not in.

## Build

    dotnet run -c Release --project tests      # offline checks on a fake game folder (and a fake GitHub on localhost)
    dotnet publish SoftbodyManager.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish

then copy `publish\SoftbodyManager.exe` to `Testresolink\`. `SoftbodyManager.exe --snapshot <png>` draws the window (with
the real, read-only status) to a picture and exits.
