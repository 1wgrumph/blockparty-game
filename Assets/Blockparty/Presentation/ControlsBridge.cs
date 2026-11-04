using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Blockparty.UI;

// CONTROLS-PLAY@1: live control surfaces over the landed UI packages.
// Owns one eight-resident greybox world and routes every driven input through
// the real CAMERA/HUD/INPUT-ACCESS/SELECT-ACTION controllers. Presentation
// state only: the world, clock and command log are the landed packages' own.
public sealed class ControlsBridge
{
    public const string ActivityCode = "ACT-FOOD-01";
    public const long WorldId = 27L;
    public const long Seed = 42L;

    static readonly StateWriter Core = StateWriter.SimCore;

    public readonly SettingsRecord Settings;
    public readonly Accessibility Accessibility;
    public readonly WorldState World;
    public readonly Clock Clock;
    public readonly CommandArbitrator Commands;
    public readonly ActivityExecutor Executor;
    public readonly AppearanceStore Appearances;
    public readonly InputMap Input;
    public readonly CameraControl Camera;
    public readonly WorldNavigation Navigation;
    public readonly Selection Selection;
    public readonly ActionMenu Menu;
    public readonly Hud Hud;
    public readonly CursorState Cursors;
    public readonly ResidentId[] Residents;
    public readonly ObjectId[] StationIds;
    public readonly ActivityId Activity = new ActivityId(ActivityCode);

    public ResidentId? LastSelected;
    public string LastMenuMessage = string.Empty;
    public string LastHudLabel = string.Empty;
    public CursorCue LastCue;
    public bool MenuOpen { get; private set; }

    public ControlsBridge(SettingsRecord settings)
    {
        Settings = settings;
        Accessibility = new Accessibility(settings);
        World = ResidentFactory.CreateNewGame(WorldId, Seed,
            new[] { "Alex", "Bea", "Cleo", "Dev", "Eli", "Fran", "Gio", "Harper" },
            out Appearances);
        Clock = new Clock(World);
        Commands = new CommandArbitrator(World, Clock);
        Executor = new ActivityExecutor(World, Commands, Clock);

        ResidentRecord[] rows = World.ResidentsSnapshot();
        System.Array.Sort(rows, (a, b) => ((long)a.Id).CompareTo((long)b.Id));
        Residents = new ResidentId[rows.Length];
        HomeRecord second = World.AllocateHome(Core, 8);
        HouseholdRecord household = World.AllocateHousehold(Core, "Second household", second.Id);
        StationIds = new ObjectId[8];
        for (int i = 0; i < rows.Length; i++)
        {
            Residents[i] = rows[i].Id;
            if (i >= 4) World.SetHousehold(Core, rows[i].Id, household.Id, second.Id);
            World.SetMoney(Core, rows[i].Id, 50L);
            for (int n = 0; n < StateSchema.NeedCount; n++)
                World.SetNeed(Core, rows[i].Id, (Need)n, 40f);
            StationIds[i] = World.AllocateObject(Core);
            Executor.RegisterObject(StationIds[i],
                new[] { new ApproachPose(-10f + i * 2.5f, 0f, -2f) });
            Executor.SetResidentPosition(rows[i].Id,
                new ApproachPose(-10f + i * 2.5f, 0f, 1f));
        }

        Input = new InputMap(Accessibility, Clock);
        Navigation = new WorldNavigation(World, Commands, Executor);
        Camera = new CameraControl(Accessibility, Input, Navigation, Executor);
        Selection = new Selection(World, Navigation, Accessibility);
        Menu = new ActionMenu(World, Commands, Accessibility, Input);
        Hud = new Hud(World, Commands, Accessibility, Input);
        Cursors = new CursorState(Accessibility);
    }

    public bool CameraActivate(string binding, double seconds)
    {
        return Camera.TryActivate(binding, seconds);
    }

    public bool SelectionNext(string binding, IReadOnlyList<SelectionTarget> targets)
    {
        bool ok = Selection.TryInput(binding, targets);
        if (ok) LastSelected = Navigation.SelectedResident;
        return ok;
    }

    public bool PointerSelect(ResidentId? hit, float x, float y,
        IReadOnlyList<SelectionTarget> targets)
    {
        Selection.PointerSelect(hit, x, y, targets);
        LastSelected = Navigation.SelectedResident;
        return LastSelected.HasValue;
    }

    public void OpenMenu(ResidentId resident)
    {
        Menu.Open(resident, StationIds[0], "Station " + ActivityCode,
            new[] { new AvailableAction(Activity, (long)StationIds[0]) });
        Input.EnterState("ActionMenu");
        MenuOpen = true;
    }

    public bool MenuFocus(string binding)
    {
        return Menu.TryInput(binding);
    }

    public bool MenuConfirm(string binding)
    {
        bool ok = Menu.TryInput(binding);
        LastMenuMessage = Menu.Message;
        return ok;
    }

    public bool MenuClick(string elementId)
    {
        bool ok = Menu.TryClick(elementId);
        LastMenuMessage = Menu.Message;
        return ok;
    }

    public void CloseMenu()
    {
        Input.TryActivate(Accessibility.BindingFor("CloseActionMenu"));
        MenuOpen = false;
    }

    public bool TogglePause(string binding)
    {
        return Input.TryActivate(binding);
    }

    public bool HudNext(string binding)
    {
        bool ok = Hud.TryInput(binding);
        RefreshHudLabel();
        return ok;
    }

    public bool HudClick(string elementId)
    {
        bool ok = Hud.TryClick(elementId);
        RefreshHudLabel();
        return ok;
    }

    public bool CursorDemoNext()
    {
        CursorKind[] kinds = (CursorKind[])System.Enum.GetValues(typeof(CursorKind));
        int next = 0;
        if (LastCue != null)
        {
            for (int i = 0; i < kinds.Length; i++)
                if (kinds[i] == LastCue.Kind) next = (i + 1) % kinds.Length;
        }
        LastCue = Cursors.Resolve(
            kinds[next] == CursorKind.HoverInteractable,
            kinds[next] == CursorKind.HoverResident,
            kinds[next] == CursorKind.Blocked ? (RejectReason?)RejectReason.ObjectUnavailable : null,
            kinds[next] == CursorKind.Busy,
            kinds[next] == CursorKind.Placement);
        return LastCue != null;
    }

    public CursorCue CursorFor(CursorKind kind)
    {
        return Cursors.Resolve(kind == CursorKind.HoverInteractable,
            kind == CursorKind.HoverResident,
            kind == CursorKind.Blocked ? (RejectReason?)RejectReason.ObjectUnavailable : null,
            kind == CursorKind.Busy, kind == CursorKind.Placement);
    }

    public void SelectResident(int index)
    {
        if (index < 0 || index >= Residents.Length) return;
        Navigation.SelectResident(Residents[index]);
        LastSelected = Residents[index];
    }

    public void SetPose(int index, float x, float z)
    {
        if (index < 0 || index >= Residents.Length) return;
        Executor.SetResidentPosition(Residents[index], new ApproachPose(x, 0f, z));
    }

    public string ResidentName(int index)
    {
        if (index < 0 || index >= Residents.Length) return string.Empty;
        ResidentRecord record;
        return World.TryGetResident(Residents[index], out record) ? record.DisplayName : string.Empty;
    }

    public ResidentRecord Resident(int index)
    {
        ResidentRecord record;
        World.TryGetResident(Residents[index], out record);
        return record;
    }

    private void RefreshHudLabel()
    {
        string id = Hud.FocusedElementId;
        LastHudLabel = id;
        if (string.IsNullOrEmpty(id)) return;
        foreach (object item in Hud.Snapshot(1920, 1080).Elements)
        {
            HudElement element = item as HudElement;
            if (element != null && element.Id == id)
            {
                LastHudLabel = element.Label;
                return;
            }
        }
    }
}
