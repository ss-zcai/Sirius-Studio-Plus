using System.Windows;
using Sirius.Studio.App;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Menus open under their heading's left edge, whatever Windows'
/// handedness setting says.
/// </summary>
[Collection(WpfCollection.Name)]
public class MenuAlignmentTests
{
    public MenuAlignmentTests(WpfTestHost host) => _ = host;

    /// <summary>
    /// On a machine set right-handed - this one, as it happens - the setting
    /// reads true until the app overrides it. Either way it reads false
    /// after.
    /// </summary>
    [Fact]
    public void Menus_drop_from_the_left_once_the_app_has_started()
    {
        WpfTestHost.OnStaThread(() =>
        {
            App.DropMenusFromTheLeft();

            Assert.False(SystemParameters.MenuDropAlignment);
        });
    }
}
