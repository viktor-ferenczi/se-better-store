# In-game test rig

Tests the Store screen in a real game client: Linux, Pulsar Interim, headless and offline. It
follows the workspace's parallel test rules (`testing/parallel-test-work/README.md` in the SE
workspace), so it needs a claimed slot and about 5.5 GB of RAM.

```sh
~/ws/se/testing/parallel-test-work/slot.sh claim SE-1486   # prints the slot number
cd Tests/rig
BS_SLOT=21 uv run -q --no-project --with httpx python3 test_store.py
~/ws/se/testing/parallel-test-work/slot.sh release 21
```

`test_store.py` starts a fresh client, runs the checks, prints a PASS or FAIL line for each and
a summary, stops the client and exits with the number of failures. A run takes about 75 s,
20 s of it for the client and the world. `--keep` leaves the client running afterwards.
On a host whose Steam has no `steamapps/libraryfolders.vdf`, set `BS_BIN64` to the game's
`Bin64` folder. `rig.py start` and `rig.py stop` start and stop the client by hand, and `rig.py probe <command>`
sends the probe a command.

## What it sets up

`rig.py` makes its own Pulsar folder from the slot, with three development folders: the Remote
plugin (`se1/plugins/remote`, for the HTTP API the test drives the game with), this working copy,
and the probe plugin in `Tests/StoreProbe`. The world is Remote's empty test space with the
economy on. The rig pastes a station (Store block, connector, battery) and a ship (connector,
two small cargo containers, a hydrogen tank, a battery), docks them and gives the Store to an NPC
faction that anyone can trade with.

## The probe

`Tests/StoreProbe` is a hidden plugin that does what the Remote API can't: it adds the Store's
offers and orders (mod API custom items, so they need no stock of their own), fills and empties
inventories and tanks, lists inventories, and toggles the plugin's `Enabled` setting. The test
writes commands to `BetterStoreProbe/cmd-*.txt` in the game's user data folder, and the probe
answers in `out-*.txt` on the game thread.

The game has no 1,000 kg lots. The probe emulates them with Harmony patches on the Store's buy
and sell methods: a row whose item id is listed as a lot moves 1,000 units per count. Its `lots`
command sends the plugin the same lot message a server would send. Its `standin` command lists a
row of lots the way Octahedron's server does: with a stand-in item named after the lot,
`Component/OctahedronLot_Ore_Ice` shown as "Ice (1 t)", which the probe defines like OctahedronMod.

## Not covered

The Windows .NET Framework build, other resolutions, the Administration button of a Store you
own, real lots on a server, and the 10 s timeout when the server doesn't answer.
