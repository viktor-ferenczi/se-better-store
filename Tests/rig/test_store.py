"""Checks the Better Store screen in a real, headless, offline game client.

Prints one PASS or FAIL line per check, a summary, and exits with the number of failures.
Screenshots of the Buy and the Sell tab go to Docs/screenshots. See README.md.

    BS_SLOT=21 uv run --no-project --with httpx python3 Tests/rig/test_store.py [--keep]

Each run starts a fresh client and world. --keep leaves the client running afterwards, for a
look by hand.
"""

from __future__ import annotations

import re
import sys
import time
from pathlib import Path

import rig

SCREEN = "StoreScreen"  # the type of BetterStoreScreen
VANILLA = "MyGuiScreenStoreBlock"
SHOTS = rig.REPO / "Docs" / "screenshots"

# The Store: rows the probe adds through the mod API, with the lot size of the rows of lots.
# Stone has a unit row and a row of 1,000 kg lots priced 950 times it, so the merged line buys
# whole lots first; Nickel the same on the Sell side, where the lots pay more.
ROWS = [
    ("offer", "Ore/Stone", 200000, 1, None),
    ("offer", "Ore/Stone", 100, 950, 1000),
    ("offer", "Ingot/Iron", 300000, 14, None),
    ("offer", "Ingot/Platinum", 50000, 300, None),
    ("offer", "Component/SteelPlate", 20000, 112, None),
    ("offer", "Component/Motor", 500, 195, None),
    ("offer", "Hydrogen", 5000, 40, None),
    ("order", "Ingot/Iron", 500000, 13, None),
    ("order", "Ingot/Nickel", 100000, 25, None),
    ("order", "Ingot/Nickel", 100, 25500, 1000),
    ("order", "Component/SteelPlate", 10000, 100, None),
    ("order", "Ingot/Gold", 1000, 500, None),
    ("order", "Hydrogen", 1000, 30, None),
]

results: list[tuple[str, bool]] = []
api = None
row_ids: dict[tuple[str, str, int], int] = {}


def check(name: str, ok: bool, detail="") -> bool:
    results.append((name, bool(ok)))
    print(f"{'PASS' if ok else 'FAIL'} {name}{'' if ok else f': {detail}'}", flush=True)
    return bool(ok)


def equal(name: str, actual, expected) -> bool:
    return check(name, actual == expected, f"{actual!r} (expected {expected!r})")


# --- reading the game ---


def screens() -> list[dict]:
    return api.list_screens()


def screen_index(kind: str = SCREEN) -> int | None:
    return next(
        (
            s["index"]
            for s in screens()
            if s.get("type") == kind and s.get("state") != "CLOSED"
        ),
        None,
    )


def controls() -> dict[str, dict]:
    found: dict[str, dict] = {}

    def walk(control):
        if control.get("name"):
            found[control["name"]] = control
        for child in control.get("children", []):
            walk(child)

    for control in api.get_screen(screen_index())["controls"]:
        walk(control)
    return found


def text(name: str) -> str:
    return controls()[name]["properties"].get("text", "")


def table() -> list[list[str]]:
    return [r["cells"] for r in controls()["BetterStoreTable"]["properties"]["rows"]]


def names() -> list[str]:
    return [r[1] for r in table()]


def control(name: str) -> dict:
    lines = rig.probe(f"control {name}")
    head = lines[0].split(" ")
    return {
        "enabled": "enabled=True" in head,
        "visible": "visible=True" in head,
        "items": [line[5:] for line in lines[1:] if line.startswith("ITEM ")],
    }


def inventory() -> dict[str, dict[str, float]]:
    """{block: {item: amount}}, and {"Hydrogen Tank": {"gas": liters}}"""
    found: dict[str, dict[str, float]] = {}
    for line in rig.probe("inv"):
        if line.startswith("TANK "):
            name, liters = line[5:].rsplit(" ", 1)
            found.setdefault(name, {})["gas"] = float(liters)
        elif line.startswith("INV "):
            name, _, items = line[4:].partition("|")
            block = found.setdefault(name, {})
            for item in filter(None, items.split(";")):
                key, _, amount = item.partition("=")
                block[key] = block.get(key, 0.0) + float(amount)
    return found


def held(item: str) -> float:
    return sum(block.get(item, 0.0) for block in inventory().values())


def balance() -> int:
    return int(api.get_balance()["balance"])


def number(value: str) -> float:
    """1,234 -> 1234; 2.5 t -> 2500"""
    match = re.search(r"([\d,]+(?:\.\d+)?)\s*(t|kg|pcs|pc|m³)?", value)
    amount = float(match.group(1).replace(",", ""))
    return amount * 1000 if match.group(2) == "t" else amount


# --- driving the screen ---


def wait(condition, what: str, timeout: float = 15.0):
    return rig.wait_until(condition, timeout, what, 0.1)


def press_use() -> dict:
    """The F key: the character stands in front of the Store's screen, looks at it and uses it"""
    x, y, z = map(float, rig.probe("usespot")[0].split()[1:])
    api.character_teleport(x, y - 1.6, z + 1.6)
    wait(
        lambda: abs(api.get_character()["position"][2] - (z + 1.6)) < 0.3,
        "the teleport",
    )
    # Twice: the use target follows the view a few frames late after a large turn
    api.character_look_at(x, y, z)
    time.sleep(0.3)
    api.character_look_at(x, y, z)
    return api.character_use()


def open_store(key: bool = False) -> None:
    if key:
        press_use()
    else:
        rig.probe("use")
    wait(
        lambda: screen_index() is not None and text("BetterStoreBalance"),
        "the Better Store screen",
    )
    wait(lambda: len(table()) > 0, "the Store's rows")


def ships() -> list:
    return control("BetterStoreShip")["items"]


def rect(name: str) -> tuple[float, float, float, float]:
    """Left, top, width and height of a control, in the game's screen units"""
    line = next(l for l in rig.probe(f"control {name}") if l.startswith("RECT "))
    return tuple(float(v) for v in line.split(" ")[1:])


def close_store() -> None:
    api.key("Escape")
    wait(lambda: screen_index() is None, "the screen to close")


def click(name: str) -> None:
    api.control_click(name=name, screen=screen_index())
    time.sleep(0.15)


def select(item: str) -> None:
    index = names().index(item)
    api.control_click(name="BetterStoreTable", row=index, screen=screen_index())
    wait(
        lambda: text("BetterStoreTrade").startswith(("Buy", "Sell"))
        and item.split(" (")[0] in text_of_item(),
        f"{item} selected",
    )


def text_of_item() -> str:
    labels = [
        c
        for c in api.get_screen(screen_index())["controls"]
        if c.get("type") == "MyGuiControlLabel"
    ]
    return " ".join((c.get("properties", {}).get("text") or "") for c in labels)


def set_amount(value: str) -> None:
    api.control_set("BetterStoreAmount", value, screen=screen_index())
    time.sleep(0.15)


def side(name: str) -> None:
    click(f"BetterStore{name}Tab")
    wait(lambda: table() and table()[0], f"the {name} tab")


def trade(button: str = "BetterStoreTrade") -> str:
    """Clicks a trade button and returns the status line once the trade has ended"""
    before = text("BetterStoreStatus")
    click(button)
    wait(
        lambda: text("BetterStoreStatus") not in ("Trading...", before)
        or screen_index("MyGuiScreenMessageBox") is not None,
        "the trade",
    )
    wait(lambda: text("BetterStoreStatus") != "Trading...", "the trade to end")
    return text("BetterStoreStatus")


def refresh() -> None:
    click("BetterStoreRefresh")
    time.sleep(0.5)


def prepare(money: int = 10_000_000) -> None:
    rig.probe("clear")
    api.set_balance(money)


# --- the checks ---


def setup_store() -> None:
    rig.probe("reset")
    for kind, item, amount, price, lot in ROWS:
        line = rig.probe(
            f"{kind} {item} {amount} {price}" + (f" {lot}" if lot else "")
        )[0]
        row_ids[(kind, item, price)] = int(line.split(" ")[1])


def check_open() -> None:
    used = press_use()
    wait(lambda: screen_index() is not None, "the Better Store screen")
    check(
        "1. F on the Store opens BetterStoreScreen",
        screen_index() is not None and screen_index(VANILLA) is None,
        used,
    )
    close_store()
    rig.probe("enabled false")
    press_use()
    vanilla = wait(lambda: screen_index(VANILLA), "the vanilla Store screen", 10)
    check(
        "1. With Enabled off the vanilla Store screen opens",
        vanilla is not None and screen_index() is None,
    )
    api.close_screen(vanilla)
    wait(lambda: screen_index(VANILLA) is None, "the vanilla screen to close")
    rig.probe("enabled true")


def check_lists() -> None:
    prepare()
    rig.probe("fill Cargo_A Ingot/Iron 40000")
    rig.probe("fill Cargo_B Ingot/Iron 30000")
    open_store()
    wait(lambda: len(ships()) == 2, "the docked ship in the list")
    equal(
        "2. Ship combo lists the docked ship and Character",
        ships(),
        ["BS Ship", "Character"],
    )
    equal(
        "2. No Administration button at a Store of another faction",
        control("BetterStoreAdministration")["visible"],
        False,
    )
    rows = {r[1]: r for r in table()}
    equal(
        "2. Stone (merged) Price / In stock",
        rows["Stone"][2:4],
        ["1 /kg · 950 /t", "300 t"],
    )
    equal(
        "2. Iron Ingot Price / In stock", rows["Iron Ingot"][2:4], ["14 /kg", "300 t"]
    )
    equal(
        "2. Motor Price / In stock / Fits",
        rows["Motor"][2:5],
        ["195 /pc", "500 pcs", "500 pcs"],
    )
    equal(
        "2. Hydrogen Price / In stock / Fits",
        rows["Hydrogen"][2:5],
        ["40 /m³", "5,000 m³", "1,000 m³"],
    )
    equal("2. Platinum Fits is limited by money", rows["Platinum Ingot"][4], "33.333 t")
    equal(
        "2. Buy tab lists the offers, sorted by name",
        names(),
        ["Hydrogen", "Iron Ingot", "Motor", "Platinum Ingot", "Steel Plate", "Stone"],
    )
    api.control_click(name="BetterStoreTable", header=2, screen=screen_index())
    equal(
        "2. A click on Price sorts by it",
        names(),
        ["Stone", "Iron Ingot", "Hydrogen", "Steel Plate", "Motor", "Platinum Ingot"],
    )
    equal(
        "2. Buy tab headers",
        [c["name"] for c in controls()["BetterStoreTable"]["properties"]["columns"]][
            1:
        ],
        ["Item", "Price (SC)", "In stock", "Fits"],
    )
    side("Sell")
    rows = {r[1]: r for r in table()}
    equal(
        "2. Sell tab: no gas, the most held first, then by name",
        names(),
        ["Iron Ingot", "Gold Ingot", "Nickel Ingot", "Steel Plate"],
    )
    equal(
        "2. Sell tab headers",
        [c["name"] for c in controls()["BetterStoreTable"]["properties"]["columns"]][
            1:
        ],
        ["Item", "Store pays (SC)", "Store wants", "You have"],
    )
    equal(
        "2. Iron Ingot Store pays / wants / You have",
        rows["Iron Ingot"][2:5],
        ["13 /kg", "500 t", "70 t"],
    )
    equal(
        "2. Nickel (merged) Store pays", rows["Nickel Ingot"][2], "25 /kg · 25,500 /t"
    )
    equal("2. Gold You have nothing", rows["Gold Ingot"][4], "-")
    close_store()


def check_character() -> None:
    prepare()
    open_store()
    api.control_set("BetterStoreShip", 1, screen=screen_index())
    select("Motor")
    set_amount("2")
    equal(
        "2. Character: goes into the suit",
        text_of_item().split("Goes into ")[1].split(" ")[0],
        "Character",
    )
    trade()
    equal(
        "2. Character: bought into the suit",
        inventory()["Character"].get("Component/Motor"),
        2.0,
    )
    select("Motor")
    set_amount("3")
    api.control_click(
        name="BetterStoreTable",
        row=names().index("Motor"),
        double_click=True,
        screen=screen_index(),
    )
    wait(
        lambda: text("BetterStoreStatus").startswith("Bought 3"),
        "the double click's trade",
    )
    equal(
        "3. A double click trades the amount in the box",
        text("BetterStoreStatus"),
        "Bought 3 pcs of Motor for 585 SC.",
    )
    close_store()


def check_ship_access() -> None:
    """Only a docked ship the player has access to is listed; the character always is"""

    def listed(label: str, expected: list) -> None:
        open_store()
        if len(expected) > 1:
            wait(lambda: len(ships()) == len(expected), "the ship in the list")
        else:
            time.sleep(1.0)  # the server's answer, which must not add the ship
        equal(label, ships(), expected)
        close_store()

    def docked() -> None:
        check("2. The ship is docked", "connected=True" in rig.probe("ship")[0])

    prepare()
    rig.probe("shipowner npc")
    docked()
    listed("2. A docked ship of another faction is not listed", ["Character"])
    rig.probe("shipowner me")
    docked()
    rig.probe("trading off")
    listed("2. A ship at a connector with trading off is not listed", ["Character"])
    rig.probe("trading on")
    # The Store of an NPC trades only with the player's own grids. The server lists a ship of
    # the player's faction too, and then drops a trade with it without an answer.
    rig.probe("mate on")
    docked()
    check(
        "2. The server lists the ship of a faction mate",
        int(rig.probe("listed")[0].split(" ")[1]) > 0,
    )
    listed("2. A faction mate's ship is not listed at an NPC's Store", ["Character"])
    rig.probe("mate off")
    docked()
    listed("2. The player's docked ship is listed again", ["BS Ship", "Character"])


def check_buy_two_containers() -> None:
    prepare()
    open_store()
    select("Iron Ingot")
    set_amount("200t")
    equal("3. Amount box reads 200t", text("BetterStoreAmount"), "200,000")
    button = text("BetterStoreTrade")
    equal("3. Buy button shows amount and total", button, "Buy 200 t for 2,800,000 SC")
    before = balance()
    status = trade()
    inv = inventory()
    a, b = inv["Cargo A"].get("Ingot/Iron", 0), inv["Cargo B"].get("Ingot/Iron", 0)
    check(
        "3. Both containers got iron, 200 t in all",
        a > 0 and b > 0 and a + b == 200000,
        f"A {a}, B {b}",
    )
    equal("3. Balance dropped by the shown total", before - balance(), 2_800_000)
    equal("3. Status line", status, "Bought 200 t of Iron Ingot for 2,800,000 SC.")
    close_store()


def check_buy_max() -> None:
    prepare(money=1_000_000)
    open_store()
    select("Platinum Ingot")
    equal(
        "4. Buy what fits (money): button",
        text("BetterStoreTradeMax"),
        "Buy what fits: 3.333 t",
    )
    trade("BetterStoreTradeMax")
    equal("4. Buy what fits (money): bought 3,333 kg", held("Ingot/Platinum"), 3333.0)
    equal("4. Buy what fits (money): 100 SC left", balance(), 100)
    close_store()

    prepare(money=100_000_000)
    open_store()
    select("Steel Plate")
    fits = number(text("BetterStoreTradeMax"))
    check("4. Buy what fits (space): less than the stock", 0 < fits < 20000, fits)
    trade("BetterStoreTradeMax")
    equal(
        "4. Buy what fits (space): bought what fit", held("Component/SteelPlate"), fits
    )
    rows = {r[1]: r for r in table()}
    equal("4. Buy what fits (space): nothing more fits", rows["Steel Plate"][4], "-")
    close_store()


def check_sell() -> None:
    prepare()
    rig.probe("fill Cargo_A Ingot/Iron 40000")
    rig.probe("fill Cargo_B Ingot/Iron 30000")
    open_store()
    side("Sell")
    select("Iron Ingot")
    set_amount("60t")
    before = balance()
    status = trade()
    inv = inventory()
    equal(
        "5. Sell 60 t: taken from A first, then B",
        (inv["Cargo A"].get("Ingot/Iron", 0), inv["Cargo B"].get("Ingot/Iron", 0)),
        (0, 10000.0),
    )
    equal("5. Sell 60 t: paid", balance() - before, 780_000)
    equal("5. Sell 60 t: status", status, "Sold 60 t of Iron Ingot for 780,000 SC.")
    select("Iron Ingot")
    equal("5. Sell all: button", text("BetterStoreTradeMax"), "Sell all: 10 t")
    trade("BetterStoreTradeMax")
    equal("5. Sell all: no iron left", held("Ingot/Iron"), 0)

    rig.probe("fill Cargo_A Component/SteelPlate 300")
    rig.probe("fill Cargo_B Ingot/Nickel 2500")
    rig.probe("fill Cargo_A Ingot/Gold 10")
    rig.probe("fill Cargo_A Ore/Stone 1000")  # not wanted, stays
    refresh()
    expected = 300 * 100 + 2 * 25500 + 500 * 25 + 10 * 500
    equal(
        "5. Sell all cargo: button",
        text("BetterStoreSellAllCargo"),
        f"Sell all cargo: 3 items, {expected:,} SC",
    )
    before = balance()
    click("BetterStoreSellAllCargo")
    box = wait(lambda: screen_index("MyGuiScreenMessageBox"), "the question")
    question = " ".join(
        (c.get("properties", {}).get("text") or "")
        for c in api.get_screen(box)["controls"]
    )
    check(
        "5. Sell all cargo asks first",
        "Sell all of 3 items from BS Ship" in question and screen_index() is not None,
        question,
    )
    check("5. Sell all cargo: nothing sold before the answer", held("Ingot/Gold") == 10)
    api.control_click(text="Yes", screen=box)
    wait(lambda: text("BetterStoreStatus").startswith("Sold"), "the sale")
    inv = inventory()
    left = {
        k: v
        for name, block in inv.items()
        if name != "Character"
        for k, v in block.items()
        if k != "gas" and v
    }
    equal(
        "5. Sell all cargo: only the unwanted stone left", left, {"Ore/Stone": 1000.0}
    )
    equal("5. Sell all cargo: paid", balance() - before, expected)
    equal(
        "5. Sell all cargo: status",
        text("BetterStoreStatus"),
        f"Sold 3 items for {expected:,} SC.",
    )
    close_store()


def check_amounts() -> None:
    prepare()
    open_store()
    select("Iron Ingot")
    less, box, more = (rect(f"BetterStore{n}") for n in ("Less", "Amount", "More"))
    # A text box draws its frame from its top, a button 0.002 above its own
    check(
        "6. Amount box is level with - and +",
        abs(box[1] - (less[1] - 0.002)) < 0.0005 and abs(more[1] - less[1]) < 0.0005,
        f"- {less[1]}, box {box[1]}, + {more[1]}",
    )
    check(
        "6. Unit and + follow the amount box",
        0.01 < more[0] - (box[0] + box[2]) < 0.05,
        f"box ends at {box[0] + box[2]:.4f}, + at {more[0]:.4f}",
    )
    wide = box[2]
    select("Motor")
    narrow = rect("BetterStoreAmount")[2]
    check(
        "6. Amount box is only as wide as the item's largest amount",
        narrow < wide < 0.12,
        f"Iron Ingot {wide}, Motor {narrow}",
    )
    select("Iron Ingot")
    equal(
        "6. Presets for kg",
        [text(f"BetterStorePreset{i}") for i in range(6)],
        ["100 kg", "1 t", "10 t", "100 t", "Half", "Fits"],
    )
    amounts = []
    for step in (
        "BetterStorePreset0",
        "BetterStorePreset1",
        "BetterStoreLess",
        "BetterStoreMore",
        "BetterStoreMore",
        "BetterStorePreset3",
    ):
        click(step)
        amounts.append(text("BetterStoreAmount"))
    equal(
        "6. 100 kg, 1 t, -, +, +, 100 t",
        amounts,
        ["100", "1,000", "900", "1,000", "2,000", "100,000"],
    )
    set_amount("2.5t")
    equal("6. Typing 2.5t", text("BetterStoreAmount"), "2,500")
    set_amount("12,000")
    click("BetterStoreMore")
    equal("6. + from 12 t steps by 10 t", text("BetterStoreAmount"), "22,000")
    click("BetterStorePreset5")
    fits = number(text("BetterStoreAmount"))
    click("BetterStorePreset4")
    equal(
        "6. Half is half of Fits",
        number(text("BetterStoreAmount")),
        float(int(fits) // 2),
    )
    select("Motor")
    equal(
        "6. Presets for pieces",
        [text(f"BetterStorePreset{i}") for i in range(6)],
        ["1", "10", "100", "1,000", "Half", "Fits"],
    )
    equal(
        "6. A preset over the maximum is disabled",
        [control(f"BetterStorePreset{i}")["enabled"] for i in (2, 3)],
        [True, False],
    )
    close_store()


def check_gas() -> None:
    prepare()
    open_store()
    select("Hydrogen")
    equal("7. Fill tanks button", text("BetterStoreTradeMax"), "Fill tanks: 1,000 m³")
    before = balance()
    status = trade("BetterStoreTradeMax")
    equal("7. The tank is full", inventory()["Hydrogen Tank"].get("gas"), 1_000_000.0)
    equal("7. Paid for 1,000 m³", before - balance(), 40_000)
    equal("7. Status", status, "Bought 1,000 m³ of Hydrogen for 40,000 SC.")
    close_store()


def check_lots() -> None:
    prepare()
    open_store()
    equal(
        "8. Merged: one Stone line",
        [n for n in names() if n.startswith("Stone")],
        ["Stone"],
    )
    select("Stone")
    set_amount("2.5t")
    lines = [text_of_item()]
    check(
        "8. Merged: 2.5 t of Stone is 2 lots and 500 kg",
        "2 × 1 t lot × 950 SC" in lines[0] and "500 kg × 1 SC" in lines[0],
        lines[0],
    )
    before = balance()
    trade()
    equal("8. Merged: 2,500 kg of Stone arrived", held("Ore/Stone"), 2500.0)
    equal("8. Merged: paid 2 × 950 + 500 × 1", before - balance(), 2400)
    api.control_set("BetterStoreMergeLots", False, screen=screen_index())
    wait(lambda: "Stone (1 t lot)" in names(), "the separate lot line")
    rows = {r[1]: r for r in table()}
    equal(
        "8. Separate: two Stone lines",
        [n for n in names() if n.startswith("Stone")],
        ["Stone", "Stone (1 t lot)"],
    )
    equal(
        "8. Separate: the lot line in lots",
        rows["Stone (1 t lot)"][2:4],
        ["950 /lot", "98 lots"],
    )
    select("Stone (1 t lot)")
    click("BetterStorePreset0")
    click("BetterStoreMore")
    equal("8. Separate: + steps a lot at a time", text("BetterStoreAmount"), "2")
    rig.probe("fill Cargo_B Ingot/Nickel 2500")
    side("Sell")
    refresh()
    equal(
        "8. Separate: Sell all cargo counts the nickel once",
        text("BetterStoreSellAllCargo"),
        "Sell all cargo: 1 item, 62,500 SC",
    )
    click("BetterStoreSellAllCargo")
    box = wait(lambda: screen_index("MyGuiScreenMessageBox"), "the question")
    api.control_click(text="Yes", screen=box)
    wait(lambda: text("BetterStoreStatus").startswith("Sold"), "the sale")
    equal(
        "8. Separate: Sell all cargo sold all the nickel",
        (held("Ingot/Nickel"), text("BetterStoreStatus")),
        (0, "Sold 2.5 t of Nickel Ingot for 62,500 SC."),
    )
    close_store()
    equal("8. The checkbox state is saved", rig.plugin_config("MergeLots"), "false")
    open_store()
    equal(
        "8. Reopened separate",
        controls()["BetterStoreMergeLots"]["properties"]["isChecked"],
        False,
    )
    api.control_set("BetterStoreMergeLots", True, screen=screen_index())
    wait(lambda: "Stone (1 t lot)" not in names(), "the merged line")
    close_store()
    equal("8. Merge back on is saved", rig.plugin_config("MergeLots"), "true")


def check_stand_ins() -> None:
    """Octahedron lists its rows of lots with stand-in items, "Ice (1 t)" is
    Component/OctahedronLot_Ore_Ice (SE-1535): they merge into their item, in tonnes"""
    prepare()

    def row(kind: str, item: str, amount: int, price: int, lot: int) -> int:
        line = rig.probe(f"{kind} {item} {amount} {price} {lot}")[0]
        rid = int(line.split(" ")[1])
        rig.probe(f"standin {rid}")
        return rid

    # Moon Station's Store: ice in lots only, cobalt by the kg and in lots
    added = [
        row("offer", "Ore/Ice", 200, 82, 1000),
        row("order", "Ore/Ice", 150, 39, 1000),
        row("offer", "Ingot/Cobalt", 30, 112000, 1000),
    ]
    cobalt = int(rig.probe("offer Ingot/Cobalt 30271 112")[0].split(" ")[1])
    open_store()
    equal(
        "11. Stand-ins: merged by default",
        controls()["BetterStoreMergeLots"]["properties"]["isChecked"],
        True,
    )
    rows = {r[1]: r for r in table()}
    equal(
        "11. Stand-ins: no lot line of their own",
        [
            n
            for n in names()
            if "(1 t" in n or "Lot" in n or n.startswith(("Ice", "Cobalt"))
        ],
        ["Cobalt Ingot", "Ice"],
    )
    equal(
        "11. Stand-ins: Ice per tonne, in tonnes", rows["Ice"][2:4], ["82 /t", "200 t"]
    )
    equal(
        "11. Stand-ins: Cobalt merged with its lots",
        rows["Cobalt Ingot"][2:4],
        ["112 /kg · 112,000 /t", "60.271 t"],
    )
    select("Ice")
    set_amount("2.5t")
    equal("11. Stand-ins: Ice trades whole tonnes", text("BetterStoreAmount"), "2,000")
    detail = text_of_item()
    check("11. Stand-ins: the detail pane counts kg", " pcs" not in detail, detail)
    check(
        "11. Stand-ins: the price shown is 2 x 82 SC",
        "2 × 1 t lot × 82 SC" in detail,
        detail,
    )
    before = balance()
    trade()
    equal(
        "11. Stand-ins: 2 t of Ice arrived for 164 SC",
        (held("Ore/Ice"), before - balance()),
        (2000.0, 164),
    )
    side("Sell")
    equal(
        "11. Stand-ins: Store pays per tonne for Ice",
        {r[1]: r for r in table()}["Ice"][2:4],
        ["39 /t", "150 t"],
    )
    side("Buy")
    shot = rig.APPDATA / "stand-ins-buy.png"
    api.mouse_move(10, 1070)
    time.sleep(0.5)
    api.screenshot_save(str(shot))
    api.control_set("BetterStoreMergeLots", False, screen=screen_index())
    wait(lambda: "Ice (1 t lot)" in names(), "the separate lot lines")
    rows = {r[1]: r for r in table()}
    equal(
        "11. Stand-ins, separate: the lot lines",
        (
            rows["Ice (1 t lot)"][2:4],
            rows["Cobalt Ingot (1 t lot)"][2:4],
            rows["Cobalt Ingot"][2],
        ),
        (["82 /lot", "198 lots"], ["112,000 /lot", "30 lots"], "112 /kg"),
    )
    api.control_set("BetterStoreMergeLots", True, screen=screen_index())
    wait(lambda: "Ice (1 t lot)" not in names(), "the merged lines")
    close_store()
    for rid in added + [cobalt]:
        rig.probe(f"stock {rid} 0")
    print(f"Screenshot: {shot}", flush=True)


def network_trade(button: str = "BetterStoreTrade") -> tuple[str, int | None]:
    """With "network on": the status line, and the error box if one came; it is closed"""
    status = trade(button)
    box = screen_index("MyGuiScreenMessageBox")
    if box is not None:
        api.control_click(text="OK", screen=box)
        wait(lambda: screen_index("MyGuiScreenMessageBox") is None, "the box to close")
    return status, box


def check_network_buy() -> None:
    """A server's answers come a frame after the request, unlike offline. A trade of more than one
    request lost the second answer and ended with "The server did not answer" (SE-1580)
    """
    rig.probe("network on")
    try:
        prepare()
        rig.probe(f"stock {row_ids[('offer', 'Ingot/Iron', 14)]} 300000")
        open_store()
        select("Iron Ingot")
        set_amount("200t")
        equal(
            "12. Network: buy into two containers",
            network_trade(),
            ("Bought 200 t of Iron Ingot for 2,800,000 SC.", None),
        )
        close_store()
    finally:
        rig.probe("network off")


def check_network_sell_all() -> None:
    """Sell all of Ice in 1 t lots, 3 lots from one container and 8 from another, as at Delta
    Station (SE-1580)"""
    rig.probe("network on")
    ice = int(rig.probe("order Ore/Ice 100 39 1000")[0].split(" ")[1])
    rig.probe(f"standin {ice}")
    try:
        prepare()
        rig.probe("fill Cargo_A Ore/Ice 3400")
        rig.probe("fill Cargo_B Ore/Ice 8200")
        open_store()
        side("Sell")
        select("Ice")
        equal(
            "13. Network: Sell all of Ice",
            text("BetterStoreTradeMax"),
            "Sell all: 11 t",
        )
        before = balance()
        equal(
            "13. Network: Sell all status, no error box",
            network_trade("BetterStoreTradeMax"),
            ("Sold 11 t of Ice for 429 SC.", None),
        )
        equal(
            "13. Network: 3 + 8 lots sold, what is less than a lot left",
            (held("Ore/Ice"), balance() - before),
            (600.0, 429),
        )
        close_store()
    finally:
        rig.probe("network off")
        rig.probe(f"stock {ice} 0")


def check_server_lots() -> None:
    """The server names the rows of lots (channel 14860): then an item sold in lots only shows in
    tonnes, which no guess can tell"""
    prepare()
    ice = int(rig.probe("offer Ore/Ice 50 10 1000")[0].split(" ")[1])
    open_store()
    equal(
        "8. Guessed: a lone lot row looks like single kg",
        {r[1]: r for r in table()}["Ice"][2:4],
        ["10 /kg", "50 kg"],
    )
    close_store()
    rig.probe("lots")
    open_store()
    rows = {r[1]: r for r in table()}
    equal("8. Server message: Ice in tonnes", rows["Ice"][2:4], ["10 /t", "50 t"])
    equal(
        "8. Server message: Stone still merged",
        (rows["Stone"][2], "Stone (1 t lot)" in rows),
        ("1 /kg · 950 /t", False),
    )
    select("Ice")
    set_amount("2.5t")
    equal(
        "8. Server message: Ice trades whole tonnes", text("BetterStoreAmount"), "2,000"
    )
    before = balance()
    trade()
    equal(
        "8. Server message: 2 t of Ice arrived for 20 SC",
        (held("Ore/Ice"), before - balance()),
        (2000.0, 20),
    )
    close_store()
    rig.probe(f"stock {ice} 0")


def screenshots() -> None:
    """The Buy and the Sell tab with realistic data, at 1920x1080"""
    SHOTS.mkdir(parents=True, exist_ok=True)
    prepare()
    setup_store()
    rig.probe("lots")
    rig.probe("fill Cargo_A Ingot/Iron 40000")
    rig.probe("fill Cargo_B Ingot/Nickel 2500")
    rig.probe("fill Cargo_B Component/SteelPlate 300")
    rig.probe("fill Cargo_A Ingot/Gold 10")
    rig.probe("tank 0.4")
    open_store()
    select("Steel Plate")
    set_amount("100")
    trade()
    select("Stone")
    set_amount("2.5t")
    api.mouse_move(10, 1070)
    time.sleep(0.5)
    api.screenshot_save(str(SHOTS / "buy.png"))
    side("Sell")
    select("Nickel Ingot")
    set_amount("2.5t")
    time.sleep(0.5)
    api.screenshot_save(str(SHOTS / "sell.png"))
    close_store()
    check(
        "Screenshots saved",
        (SHOTS / "buy.png").exists() and (SHOTS / "sell.png").exists(),
    )


def check_refused() -> None:
    prepare()
    open_store()
    select("Motor")
    set_amount("100")
    rig.probe(f"stock {row_ids[('offer', 'Component/Motor', 195)]} 10")
    before = balance()
    click("BetterStoreTrade")
    box = wait(lambda: screen_index("MyGuiScreenMessageBox"), "the error box")
    message = " ".join(
        (c.get("properties", {}).get("text") or "")
        for c in api.get_screen(box)["controls"]
    )
    check("9. A refused trade shows the game's error box", box is not None, message)
    equal("9. Nothing was paid", balance(), before)
    api.control_click(text="OK", screen=box)
    wait(lambda: screen_index("MyGuiScreenMessageBox") is None, "the box to close")
    equal(
        "9. Status says nothing was traded",
        text("BetterStoreStatus"),
        "Nothing was traded.",
    )
    rows = {r[1]: r for r in table()}
    equal("9. The screen reloaded the stock", rows["Motor"][3], "10 pcs")
    select("Motor")
    set_amount("5")
    equal("9. The screen still trades", trade(), "Bought 5 pcs of Motor for 975 SC.")
    close_store()


def check_close() -> None:
    open_store(key=True)
    close_store()
    open_store(key=True)
    check("10. Esc, then F opens it again", screen_index() is not None)
    rig.probe("closex")
    wait(lambda: screen_index() is None, "the X to close it")
    open_store(key=True)
    check("10. The X, then F opens it again", screen_index() is not None)
    close_store()


def main() -> int:
    global api
    started = time.monotonic()
    keep = "--keep" in sys.argv
    # A fresh client and world each run: the lot message of the last check stays for the session
    rig.stop()
    api = rig.start()
    print(
        f"Client up and world set up in {time.monotonic() - started:.0f} s", flush=True
    )
    try:
        setup_store()
        tests = (
            check_open, check_lists, check_character, check_ship_access, check_buy_two_containers, check_buy_max, check_sell,
            check_amounts, check_gas, check_lots, check_refused, check_close, check_stand_ins, check_network_buy, check_network_sell_all,
            # Last: after the server's message the plugin stops guessing lots for the session
            check_server_lots, screenshots,
        )  # fmt: skip
        for test in tests:
            try:
                test()
            except (
                Exception
            ) as e:  # noqa: BLE001 -- one broken check doesn't stop the rest
                check(f"{test.__name__} ran to the end", False, repr(e))
                for index in [
                    s["index"]
                    for s in screens()
                    if s.get("type") in (SCREEN, VANILLA, "MyGuiScreenMessageBox")
                ][::-1]:
                    api.close_screen(index)
                rig.probe("enabled true")
    finally:
        if not keep:
            rig.stop()
    failed = [name for name, ok in results if not ok]
    print(
        f"\n{len(results) - len(failed)}/{len(results)} checks passed in {time.monotonic() - started:.0f} s"
    )
    for name in failed:
        print(f"FAILED: {name}")
    return len(failed)


if __name__ == "__main__":
    sys.exit(main())
