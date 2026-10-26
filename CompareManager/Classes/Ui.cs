using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace CompareManager.Classes
{
    /// <summary>
    /// The app's colours and the few styled pieces every window uses. One palette, so the
    /// windows read as one app: slate greys, the logo's indigo for anything that acts, and a
    /// tint per result so a long list can be scanned by colour.
    /// </summary>
    public static class Ui
    {
        public static readonly Color Background = Color.FromArgb(248, 250, 252);
        public static readonly Color Card       = Color.White;
        public static readonly Color Border     = Color.FromArgb(226, 232, 240);
        public static readonly Color Text       = Color.FromArgb(15, 23, 42);
        public static readonly Color Muted      = Color.FromArgb(100, 116, 139);
        public static readonly Color Accent     = Color.FromArgb(79, 70, 229);
        public static readonly Color AccentText = Color.White;
        public static readonly Color Header     = Color.FromArgb(30, 27, 75);

        // Result tints: background for the row, a stronger shade for the status text.
        public static readonly Color DifferentBack = Color.FromArgb(254, 243, 199);
        public static readonly Color DifferentFore = Color.FromArgb(146, 64, 14);
        public static readonly Color DateBack      = Color.FromArgb(224, 242, 254);
        public static readonly Color DateFore      = Color.FromArgb(3, 105, 161);
        public static readonly Color LeftOnlyBack  = Color.FromArgb(255, 228, 230);
        public static readonly Color LeftOnlyFore  = Color.FromArgb(159, 18, 57);
        public static readonly Color RightOnlyBack = Color.FromArgb(220, 252, 231);
        public static readonly Color RightOnlyFore = Color.FromArgb(22, 101, 52);
        public static readonly Color SameFore      = Color.FromArgb(100, 116, 139);

        public static Font Body     => new Font("Segoe UI", 9.5f);
        public static Font Bold     => new Font("Segoe UI Semibold", 9.5f);
        public static Font Small    => new Font("Segoe UI", 8.5f);
        public static Font Caption  => new Font("Segoe UI Semibold", 8f);
        public static Font Title    => new Font("Segoe UI Semibold", 15f);
        public static Font Code     => new Font("Consolas", 9.5f);

        /// <summary>The indigo button for the one thing a window is for.</summary>
        public static Button Primary(string text) => Style(new Button { Text = text }, primary: true);

        /// <summary>A plain outlined button for everything else.</summary>
        public static Button Secondary(string text) => Style(new Button { Text = text }, primary: false);

        public static readonly Color Danger = Color.FromArgb(220, 38, 38);

        /// <summary>The red button for the one thing that changes a database.</summary>
        public static Button Destructive(string text)
        {
            var button = Style(new Button { Text = text }, primary: true);
            button.BackColor = Danger;
            button.FlatAppearance.BorderColor = Danger;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(185, 28, 28);
            return button;
        }

        public static Button Style(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.Font      = primary ? Bold : Body;
            button.AutoSize  = true;
            button.Padding   = new Padding(10, 3, 10, 3);
            button.Cursor    = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            button.BackColor = primary ? Accent : Card;
            button.ForeColor = primary ? AccentText : Text;
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(67, 56, 202) : Color.FromArgb(241, 245, 249);
            return button;
        }

        public static Label Label(string text, Font font = null, Color? color = null) => new Label
        {
            Text      = text,
            AutoSize  = true,
            Font      = font ?? Body,
            ForeColor = color ?? Text,
            BackColor = Color.Transparent
        };

        /// <summary>
        /// The logo at <paramref name="size"/> pixels, from the .ico embedded in the assembly
        /// — every size intact, where the icon Windows extracts from the exe is only 32px.
        /// </summary>
        public static Bitmap Logo(int size)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CompareManager.AppIcon.ico");
                if (stream == null) return null;
                using var icon    = new Icon(stream, size, size);
                using var closest = icon.ToBitmap();
                return closest.Width == size ? new Bitmap(closest) : new Bitmap(closest, size, size);
            }
            catch
            {
                return null;
            }
        }

        public static Icon AppIcon()
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CompareManager.AppIcon.ico");
                return stream == null ? null : new Icon(stream);
            }
            catch
            {
                return null;
            }
        }
    }
}
