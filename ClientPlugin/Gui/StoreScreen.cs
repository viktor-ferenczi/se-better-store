using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClientPlugin.Game;
using ClientPlugin.Settings;
using ClientPlugin.Trading;
using EmptyKeys.UserInterface.Mvvm;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.ViewModels;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineers.Game.GUI;
using VRage;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Gui;

/// <summary>The Store screen: pick a ship, a line and an amount; the trade is split over the ship's inventories.</summary>
internal sealed class StoreScreen : MyGuiScreenBase
{
    // The size of the game's terminal screen: it fits a 4:3 window
    private static readonly Vector2 ScreenSize = new Vector2(1.0157f, 0.9172f);

    private const float Left = -0.478f;
    private const float Right = 0.478f;
    private const float PanelLeft = 0.09f;
    private const float RowHeight = 0.0333f;
    private const int TableRows = 15;
    private const int PriceLines = 4;
    private const int SlotLines = 3;
    private const int Presets = 6;
    private const float CellMargin = 0.01f;
    private const float Inset = 0.01f;

    private static readonly Vector4 Dim = new Vector4(0.62f, 0.72f, 0.8f, 1f);
    private static readonly Vector4 Good = new Vector4(0.62f, 0.88f, 0.63f, 1f);
    private static readonly Vector4 Bad = new Vector4(0.94f, 0.54f, 0.48f, 1f);
    private static readonly Vector4 White = Vector4.One;

    private readonly StoreModel model;

    private Side side = Side.Buy;
    private bool defaultSort = true;
    private int shipIndex;
    private List<Entry> entries = new List<Entry>();
    private Entry selected;
    private long amount;
    private long max;
    private bool filling;
    private bool settingAmountText;

    private MyGuiControlCombobox shipBox;
    private MyGuiControlLabel cargoLabel;
    private MyGuiControlProgressBar cargoBar;
    private MyGuiControlLabel hydrogenLabel;
    private MyGuiControlProgressBar hydrogenBar;
    private MyGuiControlLabel balanceLabel;
    private MyGuiControlButton buyTab;
    private MyGuiControlButton sellTab;
    private MyGuiControlCheckbox mergeBox;
    private MyGuiControlTable table;
    private MyGuiControlLabel itemLabel;
    private MyGuiControlLabel infoLabel;
    private MyGuiControlTextbox amountBox;
    private MyGuiControlLabel unitLabel;
    private MyGuiControlButton moreButton;
    private readonly MyGuiControlButton[] presetButtons = new MyGuiControlButton[Presets];
    private readonly long[] presetAmounts = new long[Presets];
    private readonly MyGuiControlLabel[] priceNames = new MyGuiControlLabel[PriceLines];
    private readonly MyGuiControlLabel[] priceValues = new MyGuiControlLabel[PriceLines];
    private MyGuiControlLabel slotsTitle;
    private readonly MyGuiControlLabel[] slotNames = new MyGuiControlLabel[SlotLines];
    private readonly MyGuiControlLabel[] slotValues = new MyGuiControlLabel[SlotLines];
    private MyGuiControlButton tradeButton;
    private MyGuiControlButton maxButton;
    private MyGuiControlButton sellAllButton;
    private MyGuiControlLabel statusLabel;

    public override string GetFriendlyName() => "BetterStoreScreen";

    public StoreScreen(MyStoreBlock store)
        : base(
            new Vector2(0.5f, 0.5f),
            MyGuiConstants.SCREEN_BACKGROUND_COLOR,
            ScreenSize,
            false,
            null,
            MySandboxGame.Config.UIBkOpacity,
            MySandboxGame.Config.UIOpacity
        )
    {
        model = new StoreModel(store);
        EnabledBackgroundFade = true;
        m_closeOnEsc = true;
        CanHideOthers = true;
        CanBeHidden = true;
        CloseButtonEnabled = true;
    }

    /// <summary>Opens the screen for a Store, the way the game opens its own.</summary>
    public static void Open(MyStoreBlock store)
    {
        var service = ServiceManager.Instance.GetService<IMyGuiScreenFactoryService>();
        if (service == null || service.IsAnyScreenOpen)
            return;
        service.IsAnyScreenOpen = true;
        var screen = new StoreScreen(store);
        screen.Closed += (source, isUnloading) => service.IsAnyScreenOpen = false;
        MyGuiSandbox.AddScreen(screen);
    }

    public override void LoadContent()
    {
        base.LoadContent();
        RecreateControls(true);
        model.Changed += OnModelChanged;
        model.Refused += OnRefused;
        model.Open();
    }

    public override void OnRemoved()
    {
        model.Changed -= OnModelChanged;
        model.Refused -= OnRefused;
        model.Close();
        ConfigStorage.Save(Config.Current);
        base.OnRemoved();
    }

    public override bool Update(bool hasFocus)
    {
        model.Update();
        // The inventories and the balance change under the screen; the panel follows them
        if (!model.Trading)
            UpdatePanel();
        return base.Update(hasFocus);
    }

    // --- controls ---

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        var grid = model.Store.CubeGrid;
        AddCaption(
            "Store - "
                + (
                    string.IsNullOrEmpty(grid?.DisplayName)
                        ? model.Store.DisplayNameText
                        : grid.DisplayName
                )
        );

        // Top row: the ship, its cargo and hydrogen, the balance
        Label(Left, -0.352f, "Your ship or character", Dim);
        shipBox = new MyGuiControlCombobox(
            new Vector2(Left, -0.318f),
            new Vector2(0.3f, 0.04f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
        )
        {
            Name = "BetterStoreShip",
        };
        shipBox.ItemSelected += OnShipSelected;
        Controls.Add(shipBox);

        cargoLabel = Label(-0.15f, -0.352f, "", Dim);
        cargoBar = Bar(-0.15f, -0.318f, 0.21f);
        hydrogenLabel = Label(0.09f, -0.352f, "", Dim);
        hydrogenBar = Bar(0.09f, -0.318f, 0.21f);
        Label(
            Right,
            -0.352f,
            "Balance",
            Dim,
            MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER
        );
        balanceLabel = Label(
            Right,
            -0.32f,
            "",
            White,
            MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER,
            0.95f
        );
        balanceLabel.Name = "BetterStoreBalance";

        // Tabs and the lot checkbox
        buyTab = Button(Left, -0.268f, 0.1f, "Buy", () => SetSide(Side.Buy), "BetterStoreBuyTab");
        sellTab = Button(
            Left + 0.104f,
            -0.268f,
            0.1f,
            "Sell",
            () => SetSide(Side.Sell),
            "BetterStoreSellTab"
        );
        var mergeLabel = Label(
            Right,
            -0.268f,
            "Merge lots into their item",
            White,
            MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER
        );
        mergeBox = new MyGuiControlCheckbox(
            new Vector2(Right - mergeLabel.Size.X - 0.005f, -0.268f),
            isChecked: Config.Current.MergeLots,
            toolTip: "Shows an item sold both by the unit and in 1,000 unit lots as one line, and trades the cheapest mix.",
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER
        )
        {
            Name = "BetterStoreMergeLots",
        };
        mergeBox.IsCheckedChanged = box =>
        {
            Config.Current.MergeLots = box.IsChecked;
            Rebuild();
        };
        Controls.Add(mergeBox);

        // The table of the Store's lines
        table = new MyGuiControlTable
        {
            Name = "BetterStoreTable",
            Position = new Vector2(Left, -0.238f),
            Size = new Vector2(0.545f, RowHeight * (TableRows + 1)),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            ColumnsCount = 5,
            VisibleRowsCount = TableRows,
        };
        table.SetCustomColumnWidths(new[] { 0.05f, 0.3f, 0.35f, 0.15f, 0.15f });
        for (int column = 2; column < 5; column++)
        {
            table.SetColumnAlign(column, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
            table.SetHeaderColumnAlign(
                column,
                MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER
            );
            // The table moves a header right by the column's left margin and a right aligned cell
            // left by it and the cell's own. A negative margin and the cells' Margin line them up.
            table.SetHeaderColumnMargin(column, new Thickness(-CellMargin / 2, 0f, CellMargin, 0f));
            table.SetColumnComparison(
                column,
                (a, b) =>
                {
                    int order = ((double)a.UserData).CompareTo((double)b.UserData);
                    // Equal amounts go by name, A to Z when the larger amounts come first
                    return order != 0
                        ? order
                        : string.Compare(
                            b.Row.GetCell(1).Text.ToString(),
                            a.Row.GetCell(1).Text.ToString(),
                            StringComparison.CurrentCultureIgnoreCase
                        );
                }
            );
        }
        table.SetColumnComparison(
            1,
            (a, b) =>
                string.Compare(
                    a.Text.ToString(),
                    b.Text.ToString(),
                    StringComparison.CurrentCultureIgnoreCase
                )
        );
        table.ItemSelected += (source, args) => OnRowSelected();
        table.ItemDoubleClicked += (source, args) => Trade(amount);
        Controls.Add(table);

        // The trade panel
        itemLabel = Label(PanelLeft, -0.225f, "", White, scale: 1f);
        infoLabel = Label(PanelLeft, -0.193f, "", Dim);
        Button(PanelLeft, -0.148f, 0.035f, "-", () => SetAmount(StepDown()), "BetterStoreLess");
        // A text box draws its frame from its top at the height of its texture, as high as a
        // button, whatever its own height is. A button draws its frame a little above its own.
        amountBox = new MyGuiControlTextbox(
            new Vector2(PanelLeft + 0.04f, -0.148f - 0.02f - 0.002f),
            maxLength: 16
        )
        {
            Name = "BetterStoreAmount",
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
        };
        // The panel follows the amount as it is typed; the box shows it formatted once it loses focus
        amountBox.TextChanged += box =>
        {
            if (!settingAmountText)
                ReadAmount();
        };
        Controls.Add(amountBox);
        unitLabel = Label(PanelLeft + 0.295f, -0.148f, "", Dim);
        moreButton = Button(
            Right - 0.035f,
            -0.148f,
            0.035f,
            "+",
            () => SetAmount(amount + Step(amount)),
            "BetterStoreMore"
        );
        FitAmountBox("0");

        float presetWidth = (Right - PanelLeft - 0.004f * (Presets - 1)) / Presets;
        for (int i = 0; i < Presets; i++)
        {
            int index = i;
            presetButtons[i] = Button(
                PanelLeft + i * (presetWidth + 0.004f),
                -0.102f,
                presetWidth,
                "",
                () => SetAmount(presetAmounts[index]),
                "BetterStorePreset" + i
            );
        }

        Frame(-0.078f, 0.145f);
        Label(PanelLeft + Inset, -0.058f, "Price", Dim);
        for (int i = 0; i < PriceLines; i++)
        {
            priceNames[i] = Label(PanelLeft + Inset, -0.03f + i * 0.026f, "", White);
            priceValues[i] = Label(
                Right - Inset,
                -0.03f + i * 0.026f,
                "",
                White,
                MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER
            );
        }
        Frame(0.075f, 0.12f);
        slotsTitle = Label(PanelLeft + Inset, 0.095f, "", Dim);
        for (int i = 0; i < SlotLines; i++)
        {
            slotNames[i] = Label(PanelLeft + Inset, 0.123f + i * 0.026f, "", White);
            slotValues[i] = Label(
                Right - Inset,
                0.123f + i * 0.026f,
                "",
                White,
                MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER
            );
        }

        float half = (Right - PanelLeft - 0.004f) / 2;
        tradeButton = Button(PanelLeft, 0.282f, half, "", () => Trade(amount), "BetterStoreTrade");
        maxButton = Button(Right - half, 0.282f, half, "", () => Trade(max), "BetterStoreTradeMax");

        // Bottom row
        Button(Left, 0.39f, 0.11f, "Refresh", model.Refresh, "BetterStoreRefresh");
        var administration = Button(
            Left + 0.114f,
            0.39f,
            0.14f,
            "Administration",
            OpenVanilla,
            "BetterStoreAdministration"
        );
        administration.SetToolTip(
            "Opens the game's own Store screen, where the owner sets the offers and orders."
        );
        administration.Visible =
            model.Store.HasLocalPlayerAccess()
            && model.Store.OwnerId == MySession.Static.LocalPlayerId;
        sellAllButton = Button(
            Left + 0.258f,
            0.39f,
            0.287f,
            "",
            ConfirmSellAllCargo,
            "BetterStoreSellAllCargo"
        );
        statusLabel = Label(
            Right,
            0.39f,
            "",
            Good,
            MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER
        );
        statusLabel.Name = "BetterStoreStatus";

        Rebuild();
        FocusedControl = table;
    }

    // A dark box with a border around a part of the trade panel
    private void Frame(float top, float height) =>
        Controls.Add(
            new MyGuiControlPanel(
                new Vector2(PanelLeft, top),
                new Vector2(Right - PanelLeft, height),
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP
            )
            {
                BackgroundTexture = MyGuiConstants.TEXTURE_RECTANGLE_DARK_BORDER,
            }
        );

    private MyGuiControlLabel Label(
        float x,
        float y,
        string text,
        Vector4 color,
        MyGuiDrawAlignEnum align = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
        float scale = 0.8f
    )
    {
        var label = new MyGuiControlLabel(
            new Vector2(x, y),
            text: text,
            colorMask: color,
            textScale: scale,
            originAlign: align
        );
        Controls.Add(label);
        return label;
    }

    private MyGuiControlProgressBar Bar(float x, float y, float width)
    {
        var bar = new MyGuiControlProgressBar(
            new Vector2(x, y),
            new Vector2(width, 0.016f),
            new Color(0.44f, 0.65f, 0.79f),
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
            MyGuiConstants.TEXTURE_RECTANGLE_DARK
        );
        bar.PotentialBar.ColorMask = Good;
        Controls.Add(bar);
        return bar;
    }

    private MyGuiControlButton Button(
        float x,
        float y,
        float width,
        string text,
        Action onClick,
        string name
    )
    {
        var button = new MyGuiControlButton(
            new Vector2(x, y),
            MyGuiControlButtonStyleEnum.Rectangular,
            new Vector2(width, 0.04f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
            text: new StringBuilder(text),
            onButtonClick: source => onClick(),
            // Amounts and totals make long texts: smaller letters, then an ellipsis
            isAutoscaleEnabled: true,
            isEllipsisEnabled: true
        )
        {
            Name = name,
        };
        Controls.Add(button);
        return button;
    }

    /// <summary>
    /// Makes the amount box as wide as the largest amount and one more digit, and moves the unit
    /// and the + button next to it.
    /// </summary>
    private void FitAmountBox(string largest)
    {
        const float gap = 0.008f;
        float text = MyGuiManager
            .MeasureString(
                amountBox.TextFont,
                new StringBuilder(largest + "0"),
                amountBox.TextScaleWithLanguage
            )
            .X;
        amountBox.Size = new Vector2(
            text + 2f * MyGuiConstants.TEXTBOX_TEXT_OFFSET.X,
            amountBox.Size.Y
        );
        unitLabel.PositionX = amountBox.PositionX + amountBox.Size.X + gap;
        float unit = unitLabel.GetTextSize().X;
        moreButton.PositionX = unitLabel.PositionX + (unit > 0f ? unit + gap : 0f);
    }

    // --- state ---

    private Ship Ship =>
        shipIndex >= 0 && shipIndex < model.Ships.Count ? model.Ships[shipIndex] : null;

    private Listing Listing => selected == null ? null : model.ListingOf(selected);

    /// <summary>A line of lots shown on its own counts in lots; every other line in units of the item.</summary>
    private int Scale =>
        selected != null && selected.Unit == null && !Config.Current.MergeLots
            ? selected.LotRow.Lot
            : 1;

    private void OnModelChanged()
    {
        if (shipBox == null)
            return;
        filling = true;
        shipBox.ClearItems();
        for (int i = 0; i < model.Ships.Count; i++)
            shipBox.AddItem(i, model.Ships[i].Name, sort: false);
        if (shipIndex >= model.Ships.Count)
            shipIndex = 0;
        if (model.Ships.Count != 0)
            shipBox.SelectItemByKey(shipIndex, sendEvent: false);
        filling = false;
        Rebuild();
    }

    private void OnRefused(string reason)
    {
        MyGuiSandbox.AddScreen(
            MyGuiSandbox.CreateMessageBox(
                MyMessageBoxStyleEnum.Error,
                messageCaption: new StringBuilder("Store"),
                messageText: new StringBuilder(reason)
            )
        );
    }

    private void OnShipSelected()
    {
        if (filling)
            return;
        shipIndex = (int)shipBox.GetSelectedKey();
        amount = 0;
        Rebuild();
    }

    private void SetSide(Side newSide)
    {
        if (side == newSide)
            return;
        side = newSide;
        defaultSort = true;
        selected = null;
        amount = 0;
        Rebuild();
    }

    private void OnRowSelected()
    {
        if (filling)
            return;
        var entry = table.SelectedRow?.UserData as Entry;
        if (entry == selected)
            return;
        selected = entry;
        amount = 0;
        UpdatePanel();
    }

    /// <summary>Fills the table from the model, keeping the selected line.</summary>
    private void Rebuild()
    {
        if (table == null)
            return;
        var ship = Ship;
        string key = selected == null ? null : KeyOf(selected);
        entries = model.Entries(side, Config.Current.MergeLots);
        selected = entries.FirstOrDefault(e => KeyOf(e) == key) ?? entries.FirstOrDefault();

        buyTab.Checked = side == Side.Buy;
        sellTab.Checked = side == Side.Sell;
        bool buying = side == Side.Buy;
        table.SetColumnName(0, new StringBuilder());
        table.SetColumnName(1, new StringBuilder("Item"));
        table.SetColumnName(2, new StringBuilder(buying ? "Price (SC)" : "Store pays (SC)"));
        table.SetColumnName(3, new StringBuilder(buying ? "In stock" : "Store wants"));
        table.SetColumnName(4, new StringBuilder(buying ? "Fits" : "You have"));

        long balance = model.Balance;
        filling = true;
        table.Clear();
        foreach (var entry in entries)
        {
            var listing = model.ListingOf(entry);
            bool lots = entry.Unit == null && !Config.Current.MergeLots;
            int scale = lots ? entry.LotRow.Lot : 1;
            var room =
                ship == null
                    ? new List<long>()
                    : model.Slots(entry, ship).Select(s => s.Room).ToList();
            long last = buying
                ? Planner.Max(entry, room, balance)
                : Math.Min(room.Sum(), int.MaxValue);
            double price = (entry.Unit ?? entry.LotRow).PricePerItem;

            var row = new MyGuiControlTable.Row(entry);
            row.AddCell(
                new MyGuiControlTable.Cell(
                    "",
                    icon: Icon(listing.Icon),
                    iconOriginAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER
                )
            );
            row.AddCell(
                new MyGuiControlTable.Cell(
                    lots
                        ? $"{listing.Name} ({LotName(entry.LotRow.Lot, listing.Unit)})"
                        : listing.Name
                )
            );
            row.AddCell(RightCell(PriceText(entry, listing, lots), price));
            row.AddCell(
                RightCell(Shown(entry.Available, listing.Unit, scale), entry.Available, Dim)
            );
            row.AddCell(
                RightCell(
                    last == 0 ? "-" : Shown(last, listing.Unit, scale),
                    last,
                    last == 0 ? Dim : (Vector4?)null
                )
            );
            table.Add(row);
        }
        if (defaultSort)
        {
            // Buy: by name. Sell: what the player has the most of first. The table only toggles a
            // column: sorting by another one first leaves this one unsorted, then its first sort
            // is ascending and its second descending.
            table.SortByColumn(buying ? 2 : 1);
            table.SortByColumn(buying ? 1 : 4);
            if (!buying)
                table.SortByColumn(4);
            defaultSort = false;
        }
        else
            table.Sort(switchSort: false);
        int index = selected == null ? -1 : table.FindIndex(r => r.UserData == selected);
        table.SelectedRowIndex = index < 0 ? (int?)null : index;
        filling = false;

        amount = 0;
        UpdatePanel();
    }

    private static MyGuiControlTable.Cell RightCell(
        string text,
        double value,
        Vector4? color = null
    ) =>
        new MyGuiControlTable.Cell(
            text,
            value,
            textColor: color.HasValue ? new Color(color.Value) : (Color?)null
        )
        {
            Margin = new Thickness(CellMargin, 0f, 0f, 0f),
        };

    private static string KeyOf(Entry entry) => $"{entry.Item}|{entry.Unit?.Id}|{entry.LotRow?.Id}";

    private static MyGuiHighlightTexture? Icon(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        return new MyGuiHighlightTexture
        {
            Normal = path,
            Highlight = path,
            SizePx = new Vector2(30f, 30f),
        };
    }

    private static string LotName(int lot, Unit unit) =>
        unit == Unit.Kg && lot == 1000 ? "1 t lot" : Amounts.Number(lot) + " lot";

    private static string Shown(long units, Unit unit, int scale) =>
        scale > 1
            ? Amounts.Number(units / scale) + (units / scale == 1 ? " lot" : " lots")
            : Amounts.Format(units, unit);

    private static string PriceText(Entry entry, Listing listing, bool lots)
    {
        string per =
            listing.Unit == Unit.Kg ? "kg"
            : listing.Unit == Unit.Gas ? "m³"
            : "pc";
        var parts = new List<string>();
        if (entry.Unit != null)
            parts.Add($"{Amounts.Number(entry.Unit.ShownPrice)} /{per}");
        if (entry.LotRow != null)
            parts.Add(
                lots ? $"{Amounts.Number(entry.LotRow.ShownPrice)} /lot"
                : listing.Unit == Unit.Kg && entry.LotRow.Lot == 1000
                    ? $"{Amounts.Number(entry.LotRow.ShownPrice)} /t"
                : entry.LotRow.Lot % 1000 == 0
                    ? $"{Amounts.Number(entry.LotRow.ShownPrice)} /{Amounts.Number(entry.LotRow.Lot / 1000)}k"
                : $"{Amounts.Number(entry.LotRow.ShownPrice)} /{Amounts.Number(entry.LotRow.Lot)}"
            );
        return string.Join(" · ", parts);
    }

    // --- the amount ---

    // A line of lots shown on its own steps by whole lots, like pieces
    private Unit StepUnit => Scale > 1 ? Unit.Pieces : Listing?.Unit ?? Unit.Pieces;

    private long Step(long at) =>
        Amounts.StepAt(at / Scale, StepUnit, selected?.Step / Scale ?? 1) * Scale;

    private long StepDown() =>
        Amounts.StepDown(amount / Scale, StepUnit, selected?.Step / Scale ?? 1) * Scale;

    private void SetAmount(long units)
    {
        if (selected == null)
            return;
        long step = selected.Step;
        amount = Math.Max(0, Math.Min(max, units / step * step));
        UpdatePanel();
    }

    private void ShowAmount(string text)
    {
        if (amountBox.Text == text)
            return;
        settingAmountText = true;
        amountBox.Text = text;
        settingAmountText = false;
    }

    private void ReadAmount()
    {
        if (selected != null && Amounts.TryParse(amountBox.Text, out long typed))
            SetAmount(typed * Scale);
        else
            UpdatePanel();
    }

    // --- the panel ---

    private void UpdatePanel()
    {
        if (tradeButton == null)
            return;
        var ship = Ship;
        long balance = model.Balance;
        bool buying = side == Side.Buy;
        balanceLabel.Text = Amounts.Number(balance) + " SC";
        UpdateStatus();

        if (selected == null || ship == null)
        {
            ShowNothing(ship);
            return;
        }

        var listing = Listing;
        int scale = Scale;
        var slots = model.Slots(selected, ship);
        var room = slots.Select(s => s.Room).ToList();
        max = Planner.Max(selected, room, buying ? balance : 0);
        if (amount > max)
            amount = max;
        if (amount == 0 && !amountBox.HasFocus)
            amount = Math.Min(max, DefaultAmount(listing.Unit, selected.Step));

        var quote = Planner.Split(selected, amount);
        var requests = Planner.Place(quote, room, out long unplaced);
        long cost = quote.Cost;
        bool possible = !model.Trading && Planner.Possible(selected, amount, room, balance);

        itemLabel.Text =
            scale > 1 ? $"{listing.Name} ({LotName(scale, listing.Unit)})" : listing.Name;
        long onShip = buying ? model.Held(ship, selected) : room.Sum();
        infoLabel.Text =
            (
                listing.IsGas || listing.IsGrid
                    ? ""
                    : $"On your ship: {Amounts.Format(onShip, listing.Unit)} · "
            )
            + (buying ? "fits: " : "you can sell: ")
            + Shown(max, listing.Unit, scale);
        if (!amountBox.HasFocus)
            ShowAmount(Amounts.Number(amount / scale));
        unitLabel.Text = scale > 1 ? "lots" : Amounts.Name(listing.Unit);
        FitAmountBox(Amounts.Number(Math.Max(max, amount) / scale));

        FillPresets(listing, scale, buying);

        // The price, line by line
        var lines = new List<KeyValuePair<string, string>>();
        foreach (var part in quote.Parts)
            lines.Add(
                new KeyValuePair<string, string>(
                    (
                        part.Row.Lot > 1
                            ? $"{Amounts.Number(part.Count)} × {LotName(part.Row.Lot, listing.Unit)}"
                            : Amounts.Format(part.Count, listing.Unit)
                    ) + $" × {Amounts.Number(part.Row.ShownPrice)} SC",
                    Amounts.Number(part.Row.Cost(part.Count)) + " SC"
                )
            );
        while (lines.Count > 2)
            lines.RemoveAt(lines.Count - 1);
        lines.Add(new KeyValuePair<string, string>("Total", Amounts.Number(cost) + " SC"));
        lines.Add(
            new KeyValuePair<string, string>(
                "Balance after",
                Amounts.Number(balance + (buying ? -cost : cost)) + " SC"
            )
        );
        for (int i = 0; i < PriceLines; i++)
        {
            priceNames[i].Text = i < lines.Count ? lines[i].Key : "";
            priceValues[i].Text = i < lines.Count ? lines[i].Value : "";
            priceValues[i].ColorMask =
                buying && i == lines.Count - 1 && cost > balance ? Bad : White;
        }

        // Where it goes
        slotsTitle.Text =
            listing.IsGrid ? "Delivered"
            : buying ? "Goes into"
            : "Taken from";
        var placed = requests
            .GroupBy(r => r.Slot)
            .Select(g => new KeyValuePair<string, string>(
                slots[g.Key].Name,
                Amounts.Format(g.Sum(r => (long)r.Count * r.Row.Lot), listing.Unit)
            ))
            .ToList();
        if (listing.IsGrid)
            placed = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Next to the Store", ""),
            };
        bool missing = unplaced > 0 || quote.Left > 0;
        int shownSlots = missing ? SlotLines - 1 : SlotLines;
        if (placed.Count > shownSlots)
        {
            int more = placed.Count - (shownSlots - 1);
            placed = placed.Take(shownSlots - 1).ToList();
            placed.Add(new KeyValuePair<string, string>($"and {more} more", ""));
        }
        for (int i = 0; i < SlotLines; i++)
        {
            slotNames[i].Text = i < placed.Count ? placed[i].Key : "";
            slotValues[i].Text = i < placed.Count ? placed[i].Value : "";
            slotNames[i].ColorMask = White;
            slotValues[i].ColorMask = White;
        }
        if (missing)
        {
            int line = Math.Min(placed.Count, SlotLines - 1);
            slotNames[line].Text =
                unplaced > 0 ? (buying ? "Does not fit" : "Not on board")
                : buying ? "Not in stock, or less than a lot"
                : "Not wanted, or less than a lot";
            slotValues[line].Text = Amounts.Format(
                unplaced > 0 ? unplaced : quote.Left,
                listing.Unit
            );
            slotNames[line].ColorMask = Bad;
            slotValues[line].ColorMask = Bad;
        }

        string verb = buying ? "Buy" : "Sell";
        tradeButton.Text =
            $"{verb} {Amounts.Format(quote.Amount, listing.Unit)} for {Amounts.Number(cost)} SC";
        tradeButton.Enabled = possible;
        maxButton.Text =
            (buying ? (listing.IsGas ? "Fill tanks" : "Buy what fits") : "Sell all")
            + ": "
            + Amounts.Format(max, listing.Unit);
        maxButton.Enabled = !model.Trading && max > 0;

        UpdateGauges(ship, buying && !listing.IsGrid ? quote.Amount : 0, listing);
        UpdateSellAll(ship);
    }

    private void ShowNothing(Ship ship)
    {
        itemLabel.Text =
            model.Loading ? "Loading..."
            : ship == null ? "No ship is docked for trading"
            : "Nothing to trade here";
        infoLabel.Text = "";
        unitLabel.Text = "";
        if (!amountBox.HasFocus)
            ShowAmount("");
        FitAmountBox("0");
        slotsTitle.Text = "";
        foreach (var label in priceNames.Concat(priceValues).Concat(slotNames).Concat(slotValues))
            label.Text = "";
        foreach (var button in presetButtons)
            button.Visible = false;
        tradeButton.Text = side == Side.Buy ? "Buy" : "Sell";
        tradeButton.Enabled = false;
        maxButton.Text = side == Side.Buy ? "Buy what fits" : "Sell all";
        maxButton.Enabled = false;
        max = 0;
        if (ship != null)
            UpdateGauges(ship, 0, null);
        UpdateSellAll(ship);
    }

    private static long DefaultAmount(Unit unit, long step) =>
        step > 1 ? step
        : unit == Unit.Kg ? 1000
        : unit == Unit.Gas ? 100
        : 1;

    private void FillPresets(Listing listing, int scale, bool buying)
    {
        long[] amounts;
        string[] names;
        if (scale > 1)
        {
            amounts = new long[] { 1, 5, 10, 50 };
            names = new[] { "1", "5", "10", "50" };
        }
        else if (listing.Unit == Unit.Gas)
        {
            amounts = new long[] { 10, 100, 1000, 10000 };
            names = new[] { "10 m³", "100 m³", "1,000 m³", "10,000 m³" };
        }
        else if (listing.Unit == Unit.Kg)
        {
            bool lotsOnly = selected.Step > 1;
            amounts = lotsOnly
                ? new long[] { 1000, 5000, 10000, 100000 }
                : new long[] { 100, 1000, 10000, 100000 };
            names = lotsOnly
                ? new[] { "1 t", "5 t", "10 t", "100 t" }
                : new[] { "100 kg", "1 t", "10 t", "100 t" };
        }
        else
        {
            amounts = new long[] { 1, 10, 100, 1000 };
            names = new[] { "1", "10", "100", "1,000" };
        }
        for (int i = 0; i < 4; i++)
        {
            presetAmounts[i] = amounts[i] * scale;
            presetButtons[i].Text = names[i];
        }
        long step = selected.Step;
        presetAmounts[4] = max / 2 / step * step;
        presetButtons[4].Text = "Half";
        presetAmounts[5] = max;
        presetButtons[5].Text = buying ? (listing.IsGas ? "Fill" : "Fits") : "All";
        for (int i = 0; i < Presets; i++)
        {
            presetButtons[i].Visible = true;
            presetButtons[i].Enabled = presetAmounts[i] > 0 && presetAmounts[i] <= max;
        }
    }

    private void UpdateGauges(Ship ship, long buying, Listing listing)
    {
        StoreModel.Cargo(ship, out double used, out double capacity);
        cargoLabel.Text =
            $"Cargo: {Amounts.Number((long)used)} / {Amounts.Number((long)capacity)} L";
        Gauge(
            cargoBar,
            used,
            capacity,
            listing != null && !listing.IsGas ? buying * listing.Volume : 0
        );

        StoreModel.Hydrogen(ship, out double filled, out double tanks);
        hydrogenLabel.Text =
            tanks > 0
                ? $"Hydrogen: {Amounts.Number((long)(filled / 1000))} / {Amounts.Number((long)(tanks / 1000))} m³"
                : "Hydrogen: no tanks";
        bool hydrogen =
            listing != null
            && listing.IsGas
            && listing.Id
                == Sandbox.Game.Entities.Character.Components.MyCharacterOxygenComponent.HydrogenId;
        Gauge(hydrogenBar, filled, tanks, hydrogen ? buying * 1000.0 : 0);
    }

    // The bar shows what is there; its second, green part shows what the trade would add
    private static void Gauge(
        MyGuiControlProgressBar bar,
        double value,
        double capacity,
        double added
    )
    {
        bar.Value = capacity > 0 ? (float)(value / capacity) : 0f;
        float more = capacity > 0 ? (float)Math.Min(added / capacity, 1.0 - bar.Value) : 0f;
        bar.PotentialBar.Visible = more > 0f;
        bar.PotentialBar.Size = new Vector2(bar.Size.X * more, bar.Size.Y);
        bar.PotentialBar.Position = new Vector2(-bar.Size.X / 2f + bar.Size.X * bar.Value, 0f);
    }

    private void UpdateStatus()
    {
        statusLabel.Text = model.Trading ? "Trading..." : model.Status;
        statusLabel.ColorMask = model.StatusIsError ? Bad : Good;
    }

    // --- trading ---

    private void Trade(long units)
    {
        var ship = Ship;
        if (selected == null || ship == null || units <= 0)
            return;
        amount = 0;
        model.Trade(new[] { new KeyValuePair<Entry, long>(selected, units) }, ship);
    }

    /// <summary>All of every item on the ship that the Store wants.</summary>
    private List<KeyValuePair<Entry, long>> AllCargo(Ship ship) =>
        ship == null || side != Side.Sell
            ? new List<KeyValuePair<Entry, long>>()
            : model.AllCargo(entries, ship);

    private void UpdateSellAll(Ship ship)
    {
        var trades = AllCargo(ship);
        sellAllButton.Visible = side == Side.Sell;
        sellAllButton.Enabled = !model.Trading && trades.Count != 0;
        long total = trades.Sum(t => Planner.Split(t.Key, t.Value).Cost);
        sellAllButton.Text =
            trades.Count == 0
                ? "Sell all cargo"
                : $"Sell all cargo: {trades.Count} item{(trades.Count == 1 ? "" : "s")}, {Amounts.Number(total)} SC";
    }

    private void ConfirmSellAllCargo()
    {
        var ship = Ship;
        var trades = AllCargo(ship);
        if (trades.Count == 0)
            return;
        long total = trades.Sum(t => Planner.Split(t.Key, t.Value).Cost);
        MyGuiSandbox.AddScreen(
            MyGuiSandbox.CreateMessageBox(
                MyMessageBoxStyleEnum.Info,
                MyMessageBoxButtonsType.YES_NO,
                messageCaption: new StringBuilder("Sell all cargo"),
                messageText: new StringBuilder(
                    $"Sell all of {trades.Count} item{(trades.Count == 1 ? "" : "s")} from {ship.Name} for {Amounts.Number(total)} SC?"
                ),
                callback: answer =>
                {
                    if (answer == MyGuiScreenMessageBox.ResultEnum.YES)
                        model.Trade(trades, ship);
                }
            )
        );
    }

    /// <summary>The game's own Store screen, for the Store's administration.</summary>
    private void OpenVanilla()
    {
        var store = model.Store;
        CloseScreenNow();
        var service = ServiceManager.Instance.GetService<IMyGuiScreenFactoryService>();
        if (service == null)
            return;
        service.IsAnyScreenOpen = true;
        var screen = MyGuiSandbox.CreateScreen<MyGuiScreenStoreBlock>(
            new MyStoreBlockViewModel(store)
        );
        screen.Closed += (source, isUnloading) => service.IsAnyScreenOpen = false;
        MyGuiSandbox.AddScreen(screen);
    }
}
