"""One isolated, headless, offline game client for the Better Store tests.

Everything comes from a claimed slot of the workspace's parallel test rules
(testing/parallel-test-work/README.md): BS_SLOT=<n> selects it. Client 0 of slot n
gets the Pulsar folder ~/.se-test/<task>-c0, the game user data folder
~/.se-test/<task>-c0-data, Remote port 25000 + 10n and client id
76561199500010000 + 10n. Pulsar compiles Remote (se1/plugins/remote), this working
copy (BetterStore.xml) and the test probe (Tests/StoreProbe) as dev folders.

    BS_SLOT=21 python3 Tests/rig/rig.py start   # client up, test world loaded and set up
    BS_SLOT=21 python3 Tests/rig/rig.py stop
"""

from __future__ import annotations

import os
import re
import shutil
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

HOME = Path.home()
REPO = Path(__file__).resolve().parents[2]
WORKSPACE = Path(os.environ.get("SE_WORKSPACE", HOME / "ws/se"))
PARALLEL = WORKSPACE / "testing" / "parallel-test-work"
REMOTE_REPO = Path(os.environ.get("BS_REMOTE_REPO", WORKSPACE / "se1/plugins/remote"))
PULSAR_TEMPLATE = Path(os.environ.get("BS_PULSAR_TEMPLATE", HOME / ".config/Pulsar"))

sys.path.insert(0, str(REMOTE_REPO / "skills" / "se-remote"))
from se_remote import RemoteAPI  # noqa: E402

PLUGIN_ID = ET.parse(REPO / "BetterStore.xml").getroot().findtext("Id")
PROBE_ID = "better-store-probe"
LAUNCHER = "BetterStoreInterim.bin"
WORLD_NAME = "BetterStoreTest"
# A rendering headless client in a small world (parallel-test-work README, section 3)
NEED_GIB = 5.5
RESOLUTION = "1920x1080"


def _slot() -> tuple[int, str]:
    value = os.environ.get("BS_SLOT")
    owner = HOME / ".se-test/slots" / str(value) / "owner"
    if not value or not owner.exists():
        raise SystemExit(
            f"Set BS_SLOT to a claimed slot, see {PARALLEL / 'README.md'}: "
            f"{PARALLEL / 'slot.sh'} claim <task>"
        )
    return int(value), owner.read_text().strip()


SLOT, TASK = _slot()
PULSAR = HOME / f".se-test/{TASK}-c0"
APPDATA = HOME / f".se-test/{TASK}-c0-data"
PORT = 25000 + 10 * SLOT
CLIENT_ID = 76561199500010000 + 10 * SLOT
PID_FILE = APPDATA / "game.pid"
WORLD = APPDATA / "Saves" / "tests" / WORLD_NAME
PROBE_DIR = APPDATA / "BetterStoreProbe"
CONFIG = APPDATA / "Storage" / "BetterStore.cfg"


def connect() -> RemoteAPI:
    return RemoteAPI(
        f"http://127.0.0.1:{PORT}",
        username="admin",
        password="SpaceEngineers",
        timeout=60,
    )


def wait_until(condition, timeout: float, what: str, interval: float = 0.2):
    deadline = time.monotonic() + timeout
    while True:
        value = condition()
        if value:
            return value
        if time.monotonic() > deadline:
            raise TimeoutError(f"Timed out waiting for {what}")
        time.sleep(interval)


# ---------------------------------------------------------------------------
# Pulsar folder, configuration and the world
# ---------------------------------------------------------------------------


def _register_source(sources: Path, name: str, folder: Path, file: str) -> None:
    tree = ET.parse(sources)
    local = tree.getroot().find("LocalPluginSources")
    if local is None:
        local = ET.SubElement(tree.getroot(), "LocalPluginSources")
    for plugin in local.findall("LocalPlugin"):
        if plugin.findtext("Name") == name:
            local.remove(plugin)
    plugin = ET.SubElement(local, "LocalPlugin")
    for tag, value in (
        ("Name", name),
        ("Folder", str(folder)),
        ("File", file),
        ("Enabled", "true"),
    ):
        ET.SubElement(plugin, tag).text = value
    tree.write(sources, encoding="utf-8", xml_declaration=True)


def ensure_pulsar() -> None:
    """The client's own Pulsar folder, made from the machine's Pulsar install on first use,
    with a renamed launcher and a profile of Remote, DirectTransport, Better Store and the probe
    """
    if not (PULSAR / LAUNCHER).exists():
        subprocess.run(
            [
                str(WORKSPACE / "testing/pulsar-dev-instances/new-pulsar-instance.sh"),
                str(PULSAR),
                str(PULSAR_TEMPLATE),
            ],
            check=True,
            stdout=subprocess.DEVNULL,
        )
        shutil.copy2(PULSAR / "Interim.bin", PULSAR / LAUNCHER)
    legacy = PULSAR / "Legacy"
    sources = legacy / "Sources" / "sources.xml"
    _register_source(sources, "remote", REMOTE_REPO, "Remote.xml")
    _register_source(sources, "se-better-store", REPO, "BetterStore.xml")
    _register_source(sources, PROBE_ID, REPO / "Tests" / "StoreProbe", "StoreProbe.xml")
    folders = "".join(
        f"<LocalFolderConfig><Id>{i}</Id><DebugBuild>true</DebugBuild></LocalFolderConfig>"
        for i in ("remote", "direct-transport", PLUGIN_ID, PROBE_ID)
    )
    (legacy / "Profiles").mkdir(exist_ok=True)
    (legacy / "Profiles" / "Current.xml").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<Name>Current</Name><GitHub /><DevFolder>{folders}</DevFolder><Local /><Mods /></Profile>\n",
        encoding="utf-8",
    )


def write_configs(merge_lots: bool = True) -> None:
    (APPDATA / "Storage").mkdir(parents=True, exist_ok=True)
    write_plugin_config(merge_lots)
    (APPDATA / "Remote.cfg").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<PluginConfig>\n'
        "  <Enabled>true</Enabled>\n  <ListenIP>127.0.0.1</ListenIP>\n"
        f"  <ListenPort>{PORT}</ListenPort>\n  <AdminPassword>SpaceEngineers</AdminPassword>\n"
        "  <GridGetRateLimit>1000</GridGetRateLimit>\n  <GridGetBurstSize>2000</GridGetBurstSize>\n"
        "</PluginConfig>\n",
        encoding="utf-8",
    )
    # The world is experimental; a fresh user data folder says no to that
    game_cfg = APPDATA / "SpaceEngineers.cfg"
    if not game_cfg.exists():
        shutil.copy(HOME / ".config/SpaceEngineers/SpaceEngineers.cfg", game_cfg)
    text = re.sub(
        r"(<Key>ExperimentalMode</Key>\s*<Value>\s*<Value[^>]*>)\w+(</Value>)",
        r"\1True\2",
        game_cfg.read_text(encoding="utf-8"),
    )
    game_cfg.write_text(text, encoding="utf-8")


def write_plugin_config(merge_lots: bool = True) -> None:
    CONFIG.parent.mkdir(parents=True, exist_ok=True)
    CONFIG.write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n<Config>\n  <Enabled>true</Enabled>\n'
        f"  <MergeLots>{str(merge_lots).lower()}</MergeLots>\n</Config>\n",
        encoding="utf-8",
    )


def plugin_config(key: str) -> str | None:
    match = re.search(rf"<{key}>(.*?)</{key}>", CONFIG.read_text(encoding="utf-8"))
    return match.group(1) if match else None


def prepare_world() -> Path:
    """A fresh copy of Remote's empty space world: survival with economy, nothing that spawns
    or removes grids, no autosave"""
    shutil.rmtree(WORLD, ignore_errors=True)
    WORLD.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(REMOTE_REPO / "Worlds" / "RemoteAPITestSpace.zip") as archive:
        archive.extractall(WORLD.parent)
    (WORLD.parent / "RemoteAPITestSpace").rename(WORLD)
    settings = {
        "SessionName": WORLD_NAME,
        "GameMode": "Survival",
        "OnlineMode": "OFFLINE",
        "EnableEconomy": "true",
        "TradeFactionsCount": "0",
        "TrashRemovalEnabled": "false",
        "CargoShipsEnabled": "false",
        "EnableEncounters": "false",
        "EnableDrones": "false",
        "EnableContainerDrops": "false",
        "AutoSaveInMinutes": "0",
        "ExperimentalMode": "true",
        "EnableSunRotation": "false",
    }
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = WORLD / name
        text = path.read_text(encoding="utf-8")
        for key, value in settings.items():
            text = re.sub(rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text)
        path.write_text(text, encoding="utf-8")
    return WORLD


# ---------------------------------------------------------------------------
# The client process. It runs in a session of its own, started through run-test.sh
# (RAM guard and the shared test lock), so stopping kills exactly that process group.
# ---------------------------------------------------------------------------


def _alive() -> int | None:
    try:
        pid = int(PID_FILE.read_text().strip())
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes()
    except (OSError, ValueError):
        return None
    return pid if str(PULSAR / LAUNCHER).encode() in cmdline else None


def launch() -> None:
    if _alive():
        raise RuntimeError(f"The client of {PULSAR} is already running")
    ensure_pulsar()
    write_configs()
    shutil.rmtree(PROBE_DIR, ignore_errors=True)
    PROBE_DIR.mkdir(parents=True)
    (APPDATA / "SpaceEngineers.log").unlink(missing_ok=True)
    args = [
        "-multiInstance", "-lazySteam", "-noprompt", "-noupdate", "-nosplash", "-stablelogs",
        "-sources", "--no-steam", "-appdata", str(APPDATA), "--client-id", str(CLIENT_ID),
        "--quality", "minimal", "--resolution", RESOLUTION, "--no-audio", "--headless",
    ]  # fmt: skip
    # A host whose Steam has no library list, like a test host, names the game folder
    if os.environ.get("BS_BIN64"):
        args += ["-bin64", os.environ["BS_BIN64"]]
    process = subprocess.Popen(
        [
            str(PARALLEL / "run-test.sh"),
            "se1",
            str(NEED_GIB),
            "--",
            str(PULSAR / LAUNCHER),
            *args,
        ],
        cwd=PULSAR,
        stdout=open(APPDATA / "launch.log", "w"),
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        start_new_session=True,
    )
    PID_FILE.write_text(str(process.pid))


def stop() -> None:
    pid = _alive()
    if pid is None:
        PID_FILE.unlink(missing_ok=True)
        return
    os.killpg(pid, signal.SIGTERM)
    deadline = time.monotonic() + 30
    while _alive() and time.monotonic() < deadline:
        time.sleep(0.5)
    try:
        os.killpg(pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    PID_FILE.unlink(missing_ok=True)


def wait_api(timeout: float = 2400.0) -> RemoteAPI:
    """The RAM guard may hold the launch back for up to 30 minutes"""
    api = connect()

    def up():
        if not _alive():
            raise RuntimeError(f"The client exited, see {APPDATA / 'launch.log'}")
        try:
            api.ping()
            return True
        except Exception:  # noqa: BLE001 -- not listening yet
            return False

    wait_until(up, timeout, "the Remote API", 1)
    return api


def load_world(api: RemoteAPI) -> None:
    try:
        api.load(str(prepare_world()))
    except Exception:  # noqa: BLE001 -- the load outlives the HTTP timeout
        pass

    def ready():
        try:
            boxes = [
                s
                for s in api.list_screens()
                if s.get("type") == "MyGuiScreenMessageBox"
            ]
            for box in boxes:
                api.control_click(text="OK", screen=box["index"])
            return not boxes and api.get_state().get("ready")
        except Exception:  # noqa: BLE001 -- the API times out while a world loads
            return False

    wait_until(ready, 600, "the test world", 1)

    def character():
        try:
            return api.get_character().get("state") not in (None, "dead")
        except Exception:  # noqa: BLE001 -- 503 without a character
            screens = api.list_screens()
            for i, s in enumerate(screens):
                if "Medical" in s.get("type", ""):
                    try:
                        api.control_click(text="Respawn", screen=i)
                    except Exception:  # noqa: BLE001
                        pass
            return False

    wait_until(character, 300, "a live character", 2)


# ---------------------------------------------------------------------------
# The probe (Tests/StoreProbe)
# ---------------------------------------------------------------------------

_sequence = [0]


def probe(command: str, timeout: float = 10.0) -> list[str]:
    """Runs a probe command on the game thread and returns its answer lines"""
    _sequence[0] += 1
    name = f"{time.time_ns()}-{_sequence[0]:05d}.txt"
    (PROBE_DIR / f"cmd-{name}").write_text(command, encoding="utf-8")
    out = PROBE_DIR / f"out-{name}"
    wait_until(out.exists, timeout, f"the probe's answer to {command!r}", 0.05)
    lines = out.read_text(encoding="utf-8").splitlines()
    out.unlink()
    if any(line.startswith("ERROR") for line in lines):
        raise RuntimeError(f"probe {command!r}: {lines}")
    return lines


# ---------------------------------------------------------------------------
# The grids: a station with a Store, a battery and a trading connector, and a ship
# with two small cargo containers, a battery and a small hydrogen tank. Both are pasted
# with the same orientation; the probe moves the ship's connector in front of the
# station's and locks them.
# ---------------------------------------------------------------------------


def _block(
    kind: str, subtype: str, cell, name: str, forward: str = "Forward", extra: str = ""
) -> str:
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{kind}">'
        f'<SubtypeName>{subtype}</SubtypeName><Min x="{cell[0]}" y="{cell[1]}" z="{cell[2]}" />'
        f'<BlockOrientation Forward="{forward}" Up="Up" /><CustomName>{name}</CustomName>'
        f"<Enabled>true</Enabled>{extra}</MyObjectBuilder_CubeBlock>"
    )


def _blueprint(name: str, blocks: str, static: bool) -> str:
    return (
        '<?xml version="1.0"?><Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><ShipBlueprints>'
        '<ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="BetterStoreTest" />'
        "<CubeGrids><CubeGrid><SubtypeName /><EntityId>0</EntityId>"
        "<PersistentFlags>CastShadows InScene</PersistentFlags>"
        '<PositionAndOrientation><Position x="0" y="0" z="0" />'
        '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{blocks}</CubeBlocks>"
        f"<IsStatic>{str(static).lower()}</IsStatic><IsUnsupportedStation>{str(static).lower()}</IsUnsupportedStation>"
        f"<DisplayName>{name}</DisplayName></CubeGrid></CubeGrids>"
        "</ShipBlueprint></ShipBlueprints></Definitions>"
    )


BATTERY = "<CurrentStoredPower>3</CurrentStoredPower>"
STATION = _blueprint(
    "BS Station",
    _block("ShipConnector", "Connector", (0, 0, 0), "Trade Connector", "Left")
    + _block(
        "BatteryBlock",
        "LargeBlockBatteryBlock",
        (1, 0, 0),
        "Station Battery",
        extra=BATTERY,
    )
    # Its screen, where the F key opens it, faces +z, away from the battery
    + _block("StoreBlock", "StoreBlock", (2, 0, 0), "Store", "Right"),
    static=True,
)
SHIP = _blueprint(
    "BS Ship",
    _block("ShipConnector", "Connector", (0, 0, 0), "Ship Connector", "Right")
    + _block("CargoContainer", "LargeBlockSmallContainer", (-1, 0, 0), "Cargo A")
    + _block("CargoContainer", "LargeBlockSmallContainer", (-2, 0, 0), "Cargo B")
    + _block(
        "BatteryBlock",
        "LargeBlockBatteryBlock",
        (-3, 0, 0),
        "Ship Battery",
        extra=BATTERY,
    )
    + _block("OxygenTank", "LargeHydrogenTankSmall", (-4, 0, 0), "Hydrogen Tank"),
    static=False,
)


def build_grids(api: RemoteAPI) -> None:
    """Pastes the station and the ship next to the character and docks the ship"""
    x, y, z = api.get_character()["position"]
    station = (x + 15.0, y, z - 15.0)
    api.paste_blueprint(xml=STATION, position=station, forward=(0, 0, -1), up=(0, 1, 0))
    api.paste_blueprint(
        xml=SHIP,
        position=(station[0] - 30.0, y, z - 15.0),
        forward=(0, 0, -1),
        up=(0, 1, 0),
    )
    wait_until(lambda: probe("setup") == ["OK"], 60, "the probe's setup", 0.5)


def start() -> RemoteAPI:
    launch()
    api = wait_api()
    load_world(api)
    build_grids(api)
    return api


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "start":
        start()
        print(f"Ready. Remote API on port {PORT}, probe folder {PROBE_DIR}")
    elif command == "stop":
        stop()
    elif command == "probe":
        print("\n".join(probe(" ".join(sys.argv[2:]))))
    else:
        raise SystemExit(__doc__)
