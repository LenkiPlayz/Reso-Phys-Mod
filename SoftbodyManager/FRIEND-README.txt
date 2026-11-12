Softbody for Resonite - friend pack
====================================

This pack adds Lenki's Softbody plugin to your Resonite: soft, jiggly avatar parts
(SkinnedSoftbody), soft objects (Softbody) and liquid (FluidEmitter).

What is in it
-------------
  SoftbodyManager.exe        the app that installs it and switches it on and off
  payload\Softbody.dll       the plugin
  payload\Renderite.Unity.dll  Resonite's renderer library with the Softbody patch
                             (Renderite is MIT licensed, see LICENSE-Renderite.txt)
  payload\manifest.json      versions and checksums

Setting it up (once)
--------------------
1. Unzip the pack somewhere you will keep it, and close Resonite.
2. Start SoftbodyManager.exe. Windows may say "Windows protected your PC" because
   the app is not signed: click "More info", then "Run anyway".
3. Press "Install Softbody".
4. Press "Set up renderer fork". It copies your own Resonite renderer (about 220 MB)
   to Resonite\RendererSoftbody and puts the patch in the copy. Your Resonite itself
   is not changed.
5. Press "Copy launch options". In Steam: right-click Resonite, Properties, General,
   Launch Options, paste, and close the window.
6. Start Resonite.

Updates
-------
The manager looks for a new version from Lenki each time it opens (it asks GitHub;
nothing about you is sent). When there is one, it shows "Update to Softbody x.y.z":
close Resonite and press it. It downloads the new files (about 1 MB, or about 50 MB
when the manager itself is new too), checks that Lenki signed them, installs them
(the old ones are kept in Resonite\SoftbodyBackups), puts the new patch into your
renderer fork, and restarts itself when it was updated too. Softbody stays on or off,
as it was. No new zip needed.

Stable and unstable builds
--------------------------
Update gives you stable builds: the ones Lenki tested. Lenki's newest build, before
it is tested, is an unstable build. When there is one, the manager shows it with a
"Try unstable" button. It might break things. After you try it, the manager shows an
UNSTABLE badge, Update gives you the newest builds, and "Back to stable" puts the
stable build back (the unstable one is kept in Resonite\SoftbodyBackups). If
something breaks on an unstable build, tell Lenki. To be in a session together,
everyone needs the same build, so friends on the stable build can't join you while
you're on an unstable one.

Every day
---------
- Turn Softbody OFF in the manager to join public sessions (with it on, you can only
  join sessions where everyone has the same Softbody version). Turn it ON again for
  softbodies. Resonite must be closed to switch.
- To be in a session together with Lenki, you both need the same Softbody version
  (the manager shows it, and its compatibility hash).

When Resonite updates
---------------------
The renderer fork is made for one exact Resonite version. After a Resonite update the
manager says the fork is out of date: take the "-RendererPath ..." part out of the
launch options (Resonite may not start with an old renderer) until Lenki puts out an
update for the new Resonite version. Then press Update in the manager: it makes the
fork again, and "Copy launch options" gives you the launch options to put back.

Removing it
-----------
Turn Softbody off, take the launch options out in Steam, and delete the folders
Resonite\RendererSoftbody and Resonite\SoftbodyBackups (and Libraries\Softbody.dll.off).
Backups of anything the manager replaced are in Resonite\SoftbodyBackups.
