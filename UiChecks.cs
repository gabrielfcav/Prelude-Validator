using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class UiChecks
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr pointer);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static string[] Split(string arguments) {
        int count;
        IntPtr memory = CommandLineToArgvW("PRELUDE.exe " + arguments, out count);
        Check(memory != IntPtr.Zero, "Windows command-line parser failed");
        try {
            var result = new string[count - 1];
            for (int i = 1; i < count; i++) result[i-1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size));
            return result;
        } finally { LocalFree(memory); }
    }
    static Control Field(Form form, string name) {
        return (Control)typeof(PreludeWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
    }
    [STAThread]
    static int Main(string[] args) {
        string action, target;
        Check(InstallerArguments.TryParse(new string[0], out action, out target) && action == null && target == null, "normal launch");
        foreach (string path in new [] {
            @"C:\Program Files (x86)\Steam\steamapps\common\The Isle\TheIsle\Binaries\Win64\TheIsleClient-Win64-Shipping.exe",
            @"D:\Jogos & eventos\São Paulo\TheIsle\Binaries\Win64\TheIsleClient-Win64-Shipping.exe" }) {
            foreach (string op in new [] { "install", "uninstall" }) {
                string arguments = InstallerArguments.ForElevation(op, path);
                Check(InstallerArguments.TryParse(Split(arguments), out action, out target) && action == op && target == path,
                      "Windows UAC argument roundtrip must preserve exact path");
            }
        }
        foreach (string[] invalid in new [] {
            new [] { "--action" },
            new [] { "--action", "install" },
            new [] { "--action", "delete", "--target", @"C:\game.exe" },
            new [] { "--unknown", "value" },
            new [] { "--target", "relative.exe" },
            new [] { "--action", "install", "--action", "uninstall" }
        }) Check(!InstallerArguments.TryParse(invalid, out action, out target), "reject invalid launch arguments");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new PreludeWindow(null, null)) {
            Check(form.FormBorderStyle == FormBorderStyle.FixedSingle && !form.MaximizeBox, "fixed-size form");
            Field(form, "folder").Text = PreludeInstallerBackend.DefaultGameFolder();
            foreach (Control control in form.Controls)
                Check(form.ClientRectangle.Contains(control.Bounds), "control must stay inside client area: " + control.Text);
            bool creditFound = false;
            foreach (Control control in form.Controls)
                if (control.Text == "feito por Gildo Lanches (colaborador Prelude)") creditFound = true;
            Check(creditFound, "credit text exact");
            using (var bitmap = new Bitmap(form.Width, form.Height)) {
                form.CreateControl();
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                Point origin = form.PointToScreen(Point.Empty);
                origin.Offset(-form.Left, -form.Top);
                foreach (Control child in form.Controls)
                    child.DrawToBitmap(bitmap, new Rectangle(origin.X + child.Left, origin.Y + child.Top, child.Width, child.Height));
                bitmap.Save(args[0], System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        Console.WriteLine("PASS: UI bounds/footer/fixed size and Windows command-line roundtrip. No elevation or installation invoked.");
        return 0;
    }
}
