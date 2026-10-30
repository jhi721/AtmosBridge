using System.Drawing;
using System.Windows.Forms;

namespace AtmosBridge;

/// <summary>
/// Notification-area icon: pick the output endpoint (auto or a fixed one) and exit.
/// </summary>
static class Tray {
    public static void Start() {
        var thread = new Thread(() => {
            var menu = new ContextMenuStrip();
            // The exe's own icon (ApplicationIcon) at the small size, not the 32 px one scaled down
            using var icon = new NotifyIcon {
                Icon = Icon.ExtractIcon(Environment.ProcessPath, 0, SystemInformation.SmallIconSize.Width),
                Text = "AtmosBridge", ContextMenuStrip = menu, Visible = true,
            };
            // Filled on every open: devices come and go, spatial sound is switched on and off
            menu.Opening += (_, e) => {
                Fill(menu, icon);
                e.Cancel = false; // an empty strip cancels its first opening
            };
            Application.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static void Fill(ContextMenuStrip menu, NotifyIcon icon) {
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
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => {
            icon.Visible = false;
            Environment.Exit(0); // helpers end with their stdin
        }));
    }
}
