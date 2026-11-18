namespace Passenger.App;

/// <summary>Where each editor group's tabs stand: a row across its top, or a column down its left or right side, which
/// fits more of them.</summary>
public enum EditorTabsPlace
{
    Top,
    Left,
    Right,
}

/// <summary>How tool windows are named and gone between, each a whole of its own: by their icons on the stripes alone, or,
/// no stripe standing, by titled tabs, a place's windows along its foot (the open one's joined to it) and a hidden one's
/// along the window's edge.</summary>
public enum ToolWindowLook
{
    StripeIcons,
    TitledTabs,
}

/// <summary>What closing a tool window does to its stripe icon or its tab: keeps it, or takes it away until the window is
/// opened again from the View menu, Find Action or its keys.</summary>
public enum ClosedToolWindows
{
    KeepIcons,
    LeaveStripe,
}

/// <summary>Where Find in Files' results stay once a match is gone to: in its popup alone, closed with it, or kept in the
/// Find window as well, to go back to.</summary>
public enum FindResults
{
    Popup,
    FindWindow,
}
