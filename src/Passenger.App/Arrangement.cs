namespace Passenger.App;

/// <summary>Where each editor group's tabs stand: a row across its top, or a column down its left or right side, which
/// fits more of them.</summary>
public enum EditorTabsPlace
{
    Top,
    Left,
    Right,
}

/// <summary>How docked tool windows are named: by their icons on the stripes alone, or also by titled tabs along the foot
/// of each place where several dock, the open one's tab joined to it.</summary>
public enum ToolWindowLook
{
    StripeIcons,
    TitledTabs,
}
