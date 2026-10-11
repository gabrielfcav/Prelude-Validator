using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;

[assembly: AssemblyTitle("PRELUDE")]
[assembly: AssemblyDescription("Instalador PRELUDE nativo")]
[assembly: AssemblyCompany("Prelude")]
[assembly: AssemblyProduct("PRELUDE")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

internal sealed class PreludeWindow : Form
{
    readonly Button install = new Button(), uninstall = new Button(), browse = new Button();
    readonly TextBox folder = new TextBox();
    readonly Label status = new Label(), percentage = new Label();
    readonly ProgressBar progress = new ProgressBar();
    readonly TextBox log = new TextBox();
    readonly BackgroundWorker worker = new BackgroundWorker();
    string pending, currentAction, selectedTarget;
    readonly string initialTarget;
    bool running;

    internal PreludeWindow(string autoAction, string target)
    {
        pending = autoAction;
        initialTarget = target;
        Text = "PRELUDE";
        ClientSize = new Size(650, 456);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 24, 19);
        ForeColor = Color.FromArgb(183, 239, 207);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;

        var title = new Label {
            Text = "PRELUDE", TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 45, FontStyle.Bold),
            Location = new Point(20, 18), Size = new Size(610, 82),
            ForeColor = Color.FromArgb(113, 215, 158),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        SetupButton(install, "Instalar", new Point(56, 198));
        SetupButton(uninstall, "Desinstalar", new Point(346, 198));
        install.Click += delegate { StartAction("install"); };
        uninstall.Click += delegate { StartAction("uninstall"); };

        status.Text = "Feche o jogo antes de continuar.";
        status.TextAlign = ContentAlignment.MiddleCenter;
        status.Location = new Point(20, 316);
        status.Size = new Size(610, 30);
        status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        progress.Location = new Point(20, 284);
        progress.Size = new Size(554, 22);
        progress.Minimum = 0;
        progress.Maximum = 100;
        progress.Style = ProgressBarStyle.Continuous;
        percentage.Text = "0%";
        percentage.Location = new Point(580, 282);
        percentage.Size = new Size(50, 26);
        percentage.TextAlign = ContentAlignment.MiddleRight;
        log.Location = new Point(20, 350);
        log.Size = new Size(610, 58);
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.BackColor = Color.FromArgb(7, 13, 10);
        log.ForeColor = ForeColor;
        log.Font = new Font("Segoe UI", 9);
        log.BorderStyle = BorderStyle.FixedSingle;
        log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        var folderLabel = new Label {
            Text = "Pasta Win64 do The Isle", Location = new Point(20, 108), Size = new Size(610, 24)
        };
        folder.Location = new Point(20, 139);
        folder.Size = new Size(478, 27);
        folder.BackColor = Color.FromArgb(7, 13, 10);
        folder.ForeColor = ForeColor;
        folder.BorderStyle = BorderStyle.FixedSingle;
        browse.Text = "Procurar…";
        browse.Location = new Point(510, 137);
        browse.Size = new Size(120, 31);
        browse.FlatStyle = FlatStyle.Flat;
        browse.BackColor = Color.FromArgb(29, 65, 45);
        browse.Click += delegate {
            using (var dialog = new FolderBrowserDialog()) {
                dialog.Description = "Selecione a pasta TheIsle\\\\Binaries\\\\Win64.";
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(folder.Text)) dialog.SelectedPath = folder.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
            }
        };
        var credit = new Label {
            Text = "feito por Gildo Lanches (colaborador Prelude)",
            Location = new Point(20, 423), Size = new Size(610, 18),
            Font = new Font("Segoe UI", 8), TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(72, 91, 81), BackColor = Color.Transparent
        };
        folder.TabIndex = 0; browse.TabIndex = 1; install.TabIndex = 2; uninstall.TabIndex = 3;
        Controls.AddRange(new Control[] { title, folderLabel, folder, browse, install, uninstall,
                                          progress, percentage, status, log, credit });

        worker.DoWork += Work;
        worker.RunWorkerCompleted += Completed;
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (running) {
                e.Cancel = true;
                status.Text = "Aguarde a operação terminar para fechar.";
            }
        };
        Shown += delegate {
            if (!String.IsNullOrWhiteSpace(initialTarget))
                folder.Text = Path.GetDirectoryName(initialTarget);
            else {
                try {
                    folder.Text = Path.GetDirectoryName(PreludeInstallerBackend.DiscoverGame());
                } catch (Exception error) {
                    folder.Text = PreludeInstallerBackend.DefaultGameFolder();
                    status.Text = "Informe o caminho ou use Procurar para localizar o jogo.";
                    log.AppendText(error.Message + Environment.NewLine);
                }
            }
            if (pending != null) {
                string action = pending;
                pending = null;
                StartAction(action);
            }
        };
    }

    static void SetupButton(Button button, string text, Point location)
    {
        button.Text = text;
        button.Location = location;
        button.Size = new Size(250, 56);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(113, 215, 158);
        button.BackColor = Color.FromArgb(29, 65, 45);
        button.ForeColor = Color.FromArgb(215, 249, 229);
        button.Font = new Font("Segoe UI", 15, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    void Report(int value, string message)
    {
        if (InvokeRequired) {
            BeginInvoke(new Action<int, string>(Report), value, message);
            return;
        }
        if (!running) return;
        if (value >= 0 && value <= 100) {
            progress.Value = Math.Max(progress.Value, Math.Min(value, 99));
            percentage.Text = progress.Value.ToString() + "%";
        }
        if (!String.IsNullOrWhiteSpace(message)) {
            status.Text = value == 100 ? "Finalizando… Aguarde." : message;
            log.AppendText(message + Environment.NewLine);
        }
    }

    void StartAction(string action)
    {
        if (running) return;
        try {
            UseWaitCursor = true;
            selectedTarget = PreludeInstallerBackend.ValidateSelectedTarget(folder.Text);
            folder.Text = Path.GetDirectoryName(selectedTarget);
        } catch (Exception error) {
            status.Text = "Confira a pasta do jogo.";
            log.AppendText(error.Message + Environment.NewLine);
            return;
        } finally { UseWaitCursor = false; }
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (!elevated) {
            try {
                var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location);
                start.UseShellExecute = true;
                start.Verb = "runas";
                start.Arguments = InstallerArguments.ForElevation(action, selectedTarget);
                start.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                Process.Start(start);
                Close();
            }
            catch (Win32Exception error) {
                status.Text = error.NativeErrorCode == 1223
                    ? "Operação cancelada; nenhum arquivo alterado."
                    : "Não foi possível pedir autorização.";
                log.AppendText(error.Message + Environment.NewLine);
            }
            return;
        }
        running = true;
        currentAction = action;
        install.Enabled = uninstall.Enabled = browse.Enabled = folder.Enabled = false;
        progress.Value = 0;
        percentage.Text = "0%";
        status.Text = action == "install" ? "Instalando… Aguarde." : "Desinstalando… Aguarde.";
        log.Clear();
        log.AppendText("Verificando a instalação…" + Environment.NewLine);
        worker.RunWorkerAsync(new string[] { action, selectedTarget });
    }

    void Work(object sender, DoWorkEventArgs e)
    {
        string[] operation = (string[])e.Argument;
        PreludeInstallerBackend.Perform(operation[0], operation[1], null, Report);
    }

    void Completed(object sender, RunWorkerCompletedEventArgs e)
    {
        running = false;
        install.Enabled = uninstall.Enabled = browse.Enabled = folder.Enabled = true;
        if (e.Error != null) {
            status.Text = "Não foi possível concluir. Confira os detalhes abaixo.";
            log.AppendText("ERRO: " + e.Error.Message + Environment.NewLine);
            return;
        }
        progress.Value = 100;
        percentage.Text = "100%";
        status.Text = currentAction == "uninstall"
            ? "Desinstalado com sucesso" : "Instalado com sucesso";
    }
}

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string action, target;
        if (!InstallerArguments.TryParse(args, out action, out target)) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PreludeWindow(action, target));
        return 0;
    }
}
