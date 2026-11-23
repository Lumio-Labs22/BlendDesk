using System.Windows;

namespace BlenderLibrary;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        Appearance.StopFollowingWindows();
        base.OnExit(e);
    }
}
