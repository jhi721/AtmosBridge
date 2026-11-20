using System.Drawing;
using System.Windows.Forms;

namespace AtmosBridge;

/// <summary>
/// Notification-area icon: pick the output endpoint (auto or a fixed one) and exit.
/// </summary>
static class Tray {
    public static void Start() {
        var thread = new Thread(() => {
            Application.SetColorMode(SystemColorMode.System); // dark menu when Windows apps are dark
            var menu = new ContextMenuStrip();
            // The exe's own icon (ApplicationIcon) at the small size, not the 32 px one scaled down
            using var icon = new NotifyIcon {
                Icon = Icon.ExtractIcon(Environment.ProcessPath, 0, SystemInformation.SmallIconSize.Width),
                Text = "AtmosBridge", ContextMenuStrip = menu, Visible = true,
            };
            trayIcon = icon;
            ui = SynchronizationContext.Current; // installed by the menu (a Control) on this thread
            // Filled on every open: devices come and go, spatial sound is switched on and off
            menu.Opening += (_, e) => {
                Fill(menu);
                e.Cancel = false; // an empty strip cancels its first opening
            };
            Application.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static NotifyIcon trayIcon;
    static volatile SynchronizationContext ui; // the tray thread's, set after trayIcon

    /// <summary>
    /// Remove the icon before the process exits: Windows leaves a dead one in the tray until the mouse passes over it.
    /// Runs on the tray thread (inline when called from there, e.g. the Exit item), from any thread.
    /// </summary>
    public static void Hide() => ui?.Send(_ => trayIcon.Visible = false, null);

    /// <summary>
    /// Rebuild the menu: Auto, every active endpoint (spatial state, the one playing), Exit.
    /// </summary>
    static void Fill(ContextMenuStrip menu) {
        menu.Items.Clear();
        string chosen = Program.ChosenDevice, open = Program.OpenDevice;
        menu.Items.Add(new ToolStripMenuItem("Auto (default device with spatial sound)", null, (_, _) => Program.Choose(null)) {
            Checked = chosen == null,
        });
        menu.Items.Add(new ToolStripSeparator());
        foreach ((string id, string name, bool spatial) in SpatialOutput.Devices()) {
            string label = name + (spatial ? "" : " (spatial sound off)") + (id == open ? " - playing" : "");
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => Program.Choose(id)) { Checked = id == chosen });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Program.Quit("Exiting: handing playback back to mpv")));
    }
}
