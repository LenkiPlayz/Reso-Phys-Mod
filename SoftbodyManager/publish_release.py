"""Publishes a release made by make_friend_pack.py on GitHub as a stable or an unstable build, so friends' managers find it.

  python publish_release.py v0.27.0 --unstable   # not tested yet: only friends who chose unstable builds get it (Try unstable)
  python publish_release.py v0.26.6 --stable     # tested: every friend's manager offers it (Update)
  python publish_release.py v0.27.0 --stable     # an unstable release that works: now stable for everyone (nothing is uploaded again)
  python publish_release.py v0.27.0 --unstable   # a stable release that turned out broken: unstable again, and the stable
                                                 # release before it is the latest again (friends on it get Back to stable)

Stable is GitHub's latest release (managers 1.1.0 read only that one). Unstable is a pre-release, which is never the latest:
managers from 1.2.0 on find it in GitHub's list of releases and offer it to friends who press Try unstable. The same can be done
by hand on GitHub: edit the release, tick or untick "Set as a pre-release", and tick "Set as the latest release" on the stable one.

Needs the GitHub CLI (gh), signed in once ("gh auth login"), and the repository named in update-repo.txt (made on GitHub with a
README, so it has a first commit for the tag). Checks first that update.json is signed with this PC's key, as the managers will
check it, and that every file matches its checksum in the signed manifest; a tag is uploaded once. Then uploads update.json,
Softbody.dll, Renderite.Unity.dll, SoftbodyManager.exe and the pack's zip. --dry-run checks everything and shows what it would
change on GitHub, without changing anything.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))           # Testresolink\SoftbodyManager
OUT = os.path.join(os.path.dirname(HERE), "exports")
# SOFTBODY_UPDATE_TEST_DIR: test copies of update-repo.txt and update-key.pub.txt (as for make_friend_pack.py)
TEST_DIR = os.environ.get("SOFTBODY_UPDATE_TEST_DIR")
REPO_FILE = os.path.join(TEST_DIR or HERE, "update-repo.txt")
TEST_KEY = ["--public", os.path.join(TEST_DIR, "update-key.pub.txt")] if TEST_DIR else []
PUBLISHER = os.path.join(HERE, "publisher", "Publisher.csproj")
DRY = False


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def find_gh():
    gh = shutil.which("gh")
    if gh:
        return gh
    for p in [r"C:\Program Files\GitHub CLI\gh.exe", os.path.expandvars(r"%LOCALAPPDATA%\Programs\GitHub CLI\gh.exe")]:
        if os.path.exists(p):
            return p
    return None


def rank(tag):
    """A tag's version and repack number (v0.27.0-r2: ((0, 27, 0, 0), 2)), as the managers order releases; None without a version."""
    m = re.match(r"v?(\d{1,9}(?:\.\d{1,9}){1,3})(?:-r(\d{1,6}))?", tag or "")
    if not m:
        return None
    parts = [int(x) for x in m.group(1).split(".")]
    return tuple(parts + [0] * (4 - len(parts))), int(m.group(2) or 0)


def older(tag, than):
    a, b = rank(tag), rank(than)
    return a is not None and b is not None and a < b


def read(gh, *args):
    """A gh command that only reads; returns (ok, stdout, stderr)."""
    r = subprocess.run([gh, *args], capture_output=True, text=True, stdin=subprocess.DEVNULL)
    return r.returncode == 0, r.stdout.strip(), r.stderr.strip()


def change(cmd, what):
    """A gh command that changes something on GitHub (only shown with --dry-run)."""
    print(what)
    if DRY:
        shown = [("<notes>" if i > 0 and cmd[i - 1] in ("--notes", "-n") else c) for i, c in enumerate(cmd)]
        print("  (dry run) " + " ".join(f'"{c}"' if " " in c else c for c in shown[1:]))
        return
    r = subprocess.run(cmd, text=True, stdin=subprocess.DEVNULL)
    if r.returncode != 0:
        sys.exit("that failed (see above)")


def release_state(gh, repo, tag):
    """The release on GitHub: None when there is none, else {"isPrerelease": .., "isDraft": ..}."""
    ok, out, err = read(gh, "release", "view", tag, "--repo", repo, "--json", "isPrerelease,isDraft")
    if ok:
        return json.loads(out)
    if "not found" in err.lower():
        return None
    sys.exit(f"could not ask GitHub about {tag}: {err}")


def latest_tag(gh, repo):
    """GitHub's latest release (the stable one), or None when there is none."""
    ok, out, err = read(gh, "api", f"repos/{repo}/releases/latest", "--jq", ".tag_name")
    if ok:
        return out or None
    if "not found" in err.lower() or "404" in err:
        return None
    sys.exit(f"could not ask GitHub for its latest release: {err}")


def previous_stable(gh, repo, tag):
    """The newest stable (not pre-release, not draft) release other than tag, by version; None when there is none."""
    ok, out, err = read(gh, "release", "list", "--repo", repo, "--json", "tagName,isPrerelease,isDraft", "--limit", "100")
    if not ok:
        sys.exit(f"could not list the releases on GitHub: {err}")
    others = [r["tagName"] for r in json.loads(out) if not r["isPrerelease"] and not r["isDraft"] and r["tagName"] != tag and rank(r["tagName"])]
    return max(others, key=rank) if others else None


def title(version, unstable):
    return f"Softbody {version}" + (" (unstable)" if unstable else "")


def body(manifest, zip_name, unstable):
    notes = (manifest.get("Notes") or "").strip()
    version = manifest["SoftbodyVersion"]
    head = ("**Unstable build**: Lenki's newest, not tested yet, so it might break. Softbody Managers from 1.2.0 on offer it only to "
            "friends who press Try unstable (everyone else stays on the stable build), and Back to stable goes back.\n\n") if unstable else ""
    return head + (notes + "\n\n" if notes else "") + \
        f"Softbody {version} for Resonite (renderer {manifest['RenderiteCommit'][:10]}), compatibility hash {manifest.get('CompatibilityHash') or '?'}.\n\n" \
        f"Have the Softbody Manager already? It offers this update by itself ({'Try unstable' if unstable else 'Update'}, with Resonite closed).\n" \
        f"First time: download {zip_name}, unzip it somewhere you keep it, and follow README.txt inside."


def main():
    global DRY
    ap = argparse.ArgumentParser(description="Publishes a release made by make_friend_pack.py on GitHub, as a stable or an unstable build.")
    ap.add_argument("tag", help="the tag make_friend_pack.py printed, e.g. v0.27.0")
    which = ap.add_mutually_exclusive_group()
    which.add_argument("--stable", action="store_true", help="tested: every friend's manager offers it (an unstable release: made stable)")
    which.add_argument("--unstable", action="store_true", help="not tested yet: only friends who chose unstable builds get it (a stable release: taken back)")
    ap.add_argument("--out", default=OUT, help="where make_friend_pack.py put it (default: exports)")
    ap.add_argument("--dry-run", action="store_true", help="check everything and show what would change on GitHub, without changing it")
    args = ap.parse_args()
    DRY = args.dry_run
    if not (args.stable or args.unstable):
        sys.exit("say which: --stable (tested: every friend's manager offers it) or --unstable (not tested yet: only friends who "
                 "pressed Try unstable get it; make it stable later with --stable)")
    tag = args.tag
    release = os.path.join(os.path.abspath(args.out), f"release-{tag}")
    if not os.path.isdir(release):
        sys.exit(f"no release made for {tag}: {release} (make it with make_friend_pack.py)")
    if not os.path.exists(REPO_FILE):
        sys.exit(f"no {REPO_FILE}: updates are not set up")
    repo = open(REPO_FILE, encoding="utf-8-sig").read().strip()

    update = json.load(open(os.path.join(release, "update.json"), encoding="utf-8"))
    manifest = json.loads(update["Manifest"])
    if manifest.get("Tag") != tag:
        sys.exit(f"the release folder's manifest is for {manifest.get('Tag')}, not {tag}")
    if manifest.get("UpdateRepo") != repo:
        sys.exit(f"this release was made for {manifest.get('UpdateRepo')}, but update-repo.txt says {repo}: make it again")
    r = subprocess.run(["dotnet", "run", "--project", PUBLISHER, "-c", "Release", "--", "verify", os.path.join(release, "update.json"), *TEST_KEY],
                       capture_output=True, text=True, stdin=subprocess.DEVNULL)
    print("  " + (r.stdout.strip() or r.stderr.strip()))
    if r.returncode != 0:
        sys.exit("the signature does not check out: friends' managers would refuse this release")
    version = manifest["SoftbodyVersion"]
    zip_name = f"SoftbodyFriendPack-{version}.zip"
    files = []
    for name, key in [("Softbody.dll", "SoftbodySha256"), ("Renderite.Unity.dll", "RendererDllSha256"), ("SoftbodyManager.exe", "ManagerSha256")]:
        path = os.path.join(release, name)
        if not os.path.exists(path) or sha(path) != manifest.get(key):
            sys.exit(f"{name} is missing or does not match the signed manifest: make the release again")
        files.append(path)
    if not os.path.exists(os.path.join(release, zip_name)):
        sys.exit(f"{zip_name} is missing from {release}")
    files = [os.path.join(release, "update.json")] + files + [os.path.join(release, zip_name)]

    gh = find_gh()
    if not gh:
        sys.exit("the GitHub CLI is not installed. Install it with: winget install --id GitHub.cli   then sign in once with: gh auth login")
    if subprocess.run([gh, "auth", "status"], capture_output=True, text=True).returncode != 0:
        sys.exit("the GitHub CLI is not signed in: run gh auth login once")
    existing = release_state(gh, repo, tag)
    latest = latest_tag(gh, repo)
    going_back = f"it is older than the stable release {latest}: as the latest release it would send every friend back to it"

    if existing is None:
        if args.stable and older(tag, latest):
            sys.exit(f"not publishing {tag} as stable: {going_back}. Publish it with --unstable, or give it a newer version.")
        cmd = [gh, "release", "create", tag, *files, "--repo", repo, "--title", title(version, args.unstable), "--notes", body(manifest, zip_name, args.unstable)]
        cmd += ["--prerelease", "--latest=false"] if args.unstable else ["--latest"]
        change(cmd, f"publishing {tag} to github.com/{repo} as {'an unstable' if args.unstable else 'the stable'} build ...")
    elif existing["isDraft"]:
        sys.exit(f"{tag} is a draft on GitHub: publish or delete it there first")
    elif args.stable and existing["isPrerelease"]:
        if older(tag, latest):
            sys.exit(f"not making {tag} stable: {going_back}")
        change([gh, "release", "edit", tag, "--repo", repo, "--prerelease=false", "--latest", "--title", title(version, False), "--notes", body(manifest, zip_name, False)],
               f"making {tag} stable: every friend's manager offers it now (nothing is uploaded again) ...")
    elif args.unstable and not existing["isPrerelease"]:
        prev = previous_stable(gh, repo, tag)
        if prev is None:
            sys.exit(f"{tag} is the only stable release on GitHub: there is no stable release to fall back to")
        if latest == tag:
            change([gh, "release", "edit", prev, "--repo", repo, "--latest"], f"making {prev} the latest (stable) release again ...")
        change([gh, "release", "edit", tag, "--repo", repo, "--prerelease", "--latest=false", "--title", title(version, True), "--notes", body(manifest, zip_name, True)],
               f"taking {tag} back out of stable: it is an unstable build again (friends on it are offered Back to stable) ...")
    else:
        print(f"{tag} is {'an unstable' if existing['isPrerelease'] else 'a stable'} build on GitHub already: nothing to do. The latest (stable) release is {latest}.")
        return

    if DRY:
        print("dry run: nothing was changed on GitHub")
        return
    now = latest_tag(gh, repo)
    print(f"done: https://github.com/{repo}/releases/tag/{tag}")
    print(f"GitHub's latest (stable) release is now {now}")
    if args.stable and now != tag:
        print(f"WARNING: {tag} should be the latest release now, but GitHub says {now}: mark it as the latest on GitHub by hand")
    if args.unstable and now == tag:
        print(f"WARNING: {tag} is still the latest release: mark another stable release as the latest on GitHub by hand")


if __name__ == "__main__":
    main()
