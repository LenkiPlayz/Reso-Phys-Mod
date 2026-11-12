"""Makes a friend pack: exports\\SoftbodyFriendPack-<version>.zip, for friends to install Softbody with the manager, and
(1.1.0) the files of a GitHub release, from which friends' managers update themselves.

  python make_friend_pack.py [--notes "what is new"] [--tag v0.25.0] [--out DIR] [--force]

It holds the self-contained Softbody Manager (no .NET needed on their PC), the Softbody build, the patched
Renderite.Unity.dll from RendererSoftbody (only that file: the friend's manager makes the fork from their own renderer),
Renderite's MIT licence, a manifest (versions, checksums, the compatibility hash, the renderer it fits) and instructions.

Checks first: the fork matches the installed Resonite renderer and carries the Softbody patch (the pack only fits friends
on that same Resonite version), and warns when the build is not the version installed here (friends would not match you).

Updates (1.1.0): with update-repo.txt (the GitHub repository, owner/name) and update-key.pub.txt (made once by
"dotnet run --project publisher -- keygen") next to this script, the manager is built with both, so it checks that
repository's latest release when it opens. The manifest then also says which release this is (its tag, v<version> unless
--tag), where updates come from, the manager's version and checksum, and the notes; and exports\\release-<tag>\\ gets what
publish_release.py uploads: update.json (the manifest, signed with the key on this PC), Softbody.dll, Renderite.Unity.dll,
SoftbodyManager.exe and the pack's zip. A tag is published once: a new pack of the same version (a rebuilt fork, say) needs
a new tag, e.g. --tag v0.25.0-r2. Without the two files the pack is made as before, with no updates. 1.2.0: publish_release.py
puts a release out as a stable build (--stable: tested) or an unstable one (--unstable: only friends who press Try unstable).
"""
import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))           # Testresolink\SoftbodyManager
BASE = os.path.dirname(HERE)                                # Testresolink
GAME = r"F:\SteamLibrary\steamapps\common\Resonite"
BUILD = os.path.join(BASE, "Softbody", "bin", "Release", "net10.0", "Softbody.dll")
MANAGED = os.path.join("Renderite.Renderer_Data", "Managed", "Renderite.Unity.dll")
FORK_DLL = os.path.join(BASE, "RendererSoftbody", MANAGED)
OFFICIAL_DLL = os.path.join(GAME, "Renderer", MANAGED)
INSTALLED = os.path.join(GAME, "Libraries", "Softbody.dll")
LICENSE = os.path.join(BASE, "RenderiteSrc", "LICENSE.txt")
LOG = os.path.join(BASE, "parked_addons", "README.txt")
OUT = os.path.join(BASE, "exports")
# 1.1.0: updates (SOFTBODY_UPDATE_TEST_DIR: a folder with test copies of the two files and the private key, for trying the
# release flow without the real key)
TEST_DIR = os.environ.get("SOFTBODY_UPDATE_TEST_DIR")
REPO_FILE = os.path.join(TEST_DIR or HERE, "update-repo.txt")
PUBLIC_KEY = os.path.join(TEST_DIR or HERE, "update-key.pub.txt")
TEST_KEY = ["--key", os.path.join(TEST_DIR, "update-signing-key.pem"), "--public", PUBLIC_KEY] if TEST_DIR else []
PUBLISHER = os.path.join(HERE, "publisher", "Publisher.csproj")
CSPROJ = os.path.join(HERE, "SoftbodyManager.csproj")


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def commit_of(dll):
    m = re.search(rb"1\.0\.0\+([0-9a-f]{40})", open(dll, "rb").read())
    return m.group(1).decode() if m else None


def file_version(dll):
    """The file version (Resonite's weaver overwrites the assembly version of the installed copy, not this)."""
    if not os.path.exists(dll):
        return None
    ps = f"[Diagnostics.FileVersionInfo]::GetVersionInfo('{dll}').ProductVersion"
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True)
    v = r.stdout.strip().split("+")[0]
    return v or None


def compatibility_hash(version):
    """From the install log: the hash recorded when this version was installed here."""
    if not os.path.exists(LOG):
        return None
    found = None
    for line in open(LOG, encoding="utf-8", errors="replace"):
        if f" {version} installed" in line:
            m = re.search(r"compatibility hash ([A-Za-z0-9+/]{22}==)", line)
            if m:
                found = m.group(1)
            elif "same compatibility hash as" in line or "same hash" in line:
                m = re.search(r"([A-Za-z0-9+/]{22}==)", line)
                if m:
                    found = m.group(1)
    return found


def installed_sha(version):
    """The sha256 prefix logged when this version was installed here (the copy in Libraries is rewritten by the engine)."""
    if not os.path.exists(LOG):
        return None
    found = None
    for line in open(LOG, encoding="utf-8", errors="replace"):
        if f" {version} installed" in line:
            m = re.search(r"sha256 ([0-9a-f]{8})", line)
            if m:
                found = m.group(1)
    return found


def pick_dll(version, force):
    """The exact Softbody.dll that was installed as this version: the untouched copy the manager keeps at install time, or
    the build when it is still that same file. Exits when neither is (friends would get code you do not run)."""
    want = installed_sha(version)
    pristine = os.path.join(BASE, "parked_addons", "Libraries", f"Softbody.dll.{version}.installed")
    if want is None:
        print(f"warning: no install of {version} in the log: packing the build as it is")
        return BUILD
    if os.path.exists(pristine) and sha(pristine).startswith(want):
        return pristine
    if sha(BUILD).startswith(want):
        return BUILD
    if force:
        print(f"warning (--force): the build ({sha(BUILD)[:8]}) is not the {version} you installed ({want}): packing it anyway")
        return BUILD
    sys.exit(f"the Softbody build ({sha(BUILD)[:8]}) is not the {version} you installed ({want}): it was rebuilt since, with other changes. "
             f"Install the current build first (then pack), or keep the installed one as parked_addons\\Libraries\\Softbody.dll.{version}.installed. "
             "(--force packs the build anyway: friends would run different code than you.)")


def read_line(path):
    """The first line of a small text file, or None."""
    if not os.path.exists(path):
        return None
    text = open(path, encoding="utf-8-sig").read().strip()
    return text.splitlines()[0].strip() if text else None


def update_source():
    """1.1.0: (repository, public key) when updates are set up, None when not (the pack is made as before); exits when only
    half of it is there."""
    repo, key = read_line(REPO_FILE), read_line(PUBLIC_KEY)
    if repo is None and key is None:
        print("note: no update-repo.txt and update-key.pub.txt: this pack has no updates (friends get new versions as packs)")
        return None
    if repo is None:
        sys.exit(f"update-key.pub.txt is there but update-repo.txt is not: write the GitHub repository (owner/name) into {REPO_FILE}")
    if key is None:
        sys.exit("update-repo.txt is there but update-key.pub.txt is not: make the signing key once with: dotnet run --project publisher -- keygen")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}", repo):
        sys.exit(f"update-repo.txt should hold owner/name of a GitHub repository, not {repo!r}")
    return repo, key


def manager_version():
    m = re.search(r"<Version>([^<]+)</Version>", open(CSPROJ, encoding="utf-8").read())
    return m.group(1).strip() if m else None


def main():
    ap = argparse.ArgumentParser(description="Makes a friend pack (and, with updates set up, a GitHub release's files).")
    ap.add_argument("--notes", default="", help="what is new, a line or two (shown in friends' managers)")
    ap.add_argument("--tag", help="the release's tag (default: v<version>); a tag is published once")
    ap.add_argument("--out", default=OUT, help="where the pack and the release go (default: exports)")
    ap.add_argument("--force", action="store_true", help="pack the build even when it is not the installed version")
    args = ap.parse_args()
    out = os.path.abspath(args.out)

    for path, what in [(BUILD, "the Softbody build"), (FORK_DLL, "the renderer fork"), (OFFICIAL_DLL, "Resonite's renderer"), (LICENSE, "Renderite's licence")]:
        if not os.path.exists(path):
            sys.exit(f"missing {what}: {path}")
    official, fork = commit_of(OFFICIAL_DLL), commit_of(FORK_DLL)
    if official is None or fork != official:
        sys.exit(f"the fork ({(fork or '?')[:10]}) does not match Resonite's renderer ({(official or '?')[:10]}): rebuild it first")
    if b"Softbody" not in open(FORK_DLL, "rb").read():
        sys.exit("the fork's Renderite.Unity.dll does not carry the Softbody patch")
    version = file_version(BUILD)
    if not version:
        sys.exit("could not read the build's version")
    installed = file_version(INSTALLED) or file_version(INSTALLED + ".off")
    if installed != version:
        print(f"warning: the pack has Softbody {version} but {installed or 'nothing'} is installed here: friends will not match you until you install {version} too")
    dll = pick_dll(version, args.force)
    if dll != BUILD:
        print(f"packing the untouched copy of the installed {version}: {dll}")
    chash = compatibility_hash(version)
    if not chash:
        print(f"warning: no compatibility hash for {version} in the install log; the manifest leaves it out")

    updates = update_source()
    tag = args.tag or f"v{version}"
    release = None
    if updates:
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}", tag):
            sys.exit(f"not a usable tag: {tag!r}")
        release = os.path.join(out, f"release-{tag}")
        if os.path.exists(release):
            sys.exit(f"{release} exists: a tag is published once. For a new pack of the same version give a new tag (--tag {tag}-r2); "
                     "if that one was never published, you can delete its folder and make it again.")

    with tempfile.TemporaryDirectory() as tmp:
        publish = os.path.join(tmp, "publish")
        print("publishing the self-contained manager" + (f" (updates from github.com/{updates[0]})" if updates else "") + " ...")
        r = subprocess.run(["dotnet", "publish", CSPROJ, "-c", "Release", "-r", "win-x64",
                            "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                            "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none", "-o", publish, "-nologo", "-v", "q",
                            # 1.1.0: the update source built in (none when the files are not there)
                            f"-p:UpdateRepoFile={REPO_FILE if updates else 'none'}", f"-p:UpdateKeyFile={PUBLIC_KEY if updates else 'none'}"],
                           capture_output=True, text=True, stdin=subprocess.DEVNULL)
        exe = os.path.join(publish, "SoftbodyManager.exe")
        if r.returncode != 0 or not os.path.exists(exe):
            print(r.stdout[-3000:], r.stderr[-2000:])
            sys.exit("publishing the manager failed")

        name = f"SoftbodyFriendPack-{version}"
        pack = os.path.join(out, name)
        if os.path.exists(pack):
            shutil.rmtree(pack)   # an older copy of this same pack, made by this script
        payload = os.path.join(pack, "payload")
        os.makedirs(payload)
        shutil.copy2(exe, os.path.join(pack, "SoftbodyManager.exe"))
        shutil.copy2(dll, os.path.join(payload, "Softbody.dll"))
        shutil.copy2(FORK_DLL, os.path.join(payload, "Renderite.Unity.dll"))
        shutil.copy2(LICENSE, os.path.join(pack, "LICENSE-Renderite.txt"))
        shutil.copy2(os.path.join(HERE, "FRIEND-README.txt"), os.path.join(pack, "README.txt"))
        manifest = {
            "SoftbodyVersion": version,
            "SoftbodySha256": sha(dll),
            "CompatibilityHash": chash,
            "RenderiteCommit": official,
            "RendererDllSha256": sha(FORK_DLL),
            "Made": datetime.datetime.now().strftime("%Y-%m-%d %H:%M"),
        }
        if updates:
            manifest.update({
                "Tag": tag,
                "UpdateRepo": updates[0],
                "ManagerVersion": manager_version(),
                "ManagerSha256": sha(exe),
                "Notes": args.notes.strip(),
            })
        manifest_file = os.path.join(payload, "manifest.json")
        with open(manifest_file, "w", encoding="utf-8") as f:
            json.dump(manifest, f, indent=2)
        with open(os.path.join(pack, "LICENSE-Renderite.txt"), "a", encoding="utf-8") as f:
            f.write(f"\n\npayload\\Renderite.Unity.dll is built from Renderite (github.com/Yellow-Dog-Man/Renderite) at commit {official},\n"
                    "with the Softbody patch (vertex-only updates of skinned meshes applied in place, and the GPU fluid).\n")

        if updates:
            # signed with the key on this PC; the manager just built carries its public half
            os.makedirs(release)
            r = subprocess.run(["dotnet", "run", "--project", PUBLISHER, "-c", "Release", "--", "sign", manifest_file, os.path.join(release, "update.json"), *TEST_KEY],
                               capture_output=True, text=True, stdin=subprocess.DEVNULL)
            if r.returncode != 0:
                shutil.rmtree(release)
                print(r.stdout[-3000:], r.stderr[-2000:])
                sys.exit("signing the manifest failed")
            for src, dst in [(os.path.join(payload, "Softbody.dll"), "Softbody.dll"), (os.path.join(payload, "Renderite.Unity.dll"), "Renderite.Unity.dll"),
                             (exe, "SoftbodyManager.exe")]:
                shutil.copy2(src, os.path.join(release, dst))

    zpath = os.path.join(out, name + ".zip")
    if os.path.exists(zpath):
        os.remove(zpath)
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(pack):
            for fn in files:
                full = os.path.join(root, fn)
                z.write(full, os.path.join(name, os.path.relpath(full, pack)))
    mb = os.path.getsize(zpath) / 1048576
    print(f"made {zpath} ({mb:.0f} MB): Softbody {version}, hash {chash or '?'}, for Resonite renderer {official[:10]}")
    if release:
        shutil.copy2(zpath, os.path.join(release, name + ".zip"))
        print(f"made {release}: the release {tag} for github.com/{updates[0]} (publish it with: python publish_release.py {tag} "
              "--unstable when it is not tested yet, or --stable)")


if __name__ == "__main__":
    main()
