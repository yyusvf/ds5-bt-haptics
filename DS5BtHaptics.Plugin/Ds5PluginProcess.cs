namespace DS5BtHaptics.Plugin;

/// <summary>
/// What ysfUtil.exe runs for this plugin in a process of its own: the bridge, and the elevated
/// HidHide steps of the driver setup.
/// </summary>
public sealed class Ds5PluginProcess : YsfUtil.Modules.IPluginProcess
{
    public int Run(string[] arguments)
    {
        if (arguments.Contains(BridgeProcess.Argument)) return BridgeProcess.RunChild(arguments);
        if (arguments.Contains(Dependencies.HidHideSetupArgument)) return Dependencies.RunElevatedHidHideSetup();
        if (arguments.Contains(Dependencies.HidHideAppsArgument)) return Dependencies.RunElevatedHidHideApps(arguments);
        if (arguments.Contains(Dependencies.HidHideUndoArgument)) return Dependencies.RunElevatedHidHideUndo();
        return 2;
    }
}
