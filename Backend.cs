using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal sealed class InstallerPolicy
{
    internal string OriginalSha256;
    internal string ClientSha256;
    internal string LegacyClientSha256;
    internal long OriginalBytes;
    internal long ClientBytes;
    internal HashSet<string> LegacyReceiptHashes;
#if PRELUDE_FIXTURE_TEST
    internal bool SkipProcessGuardForOwnedFixtures;
#endif

    internal static InstallerPolicy Production()
    {
        return new InstallerPolicy {
            OriginalSha256 = "b73116f56a3512c4bcbb8916049e3f4831567e5fe806f98e9ea3693ff5a0aa1b",
            ClientSha256 = "ecfe336d75287a7d25df07ca7f5881326e3269c4bcaef397a71786baf4a44423",
            LegacyClientSha256 = "08b3a5cee4bea47a67ee410036881cb3dfa79baf0ea1346adb7673ee8b50e49c",
            OriginalBytes = 72139776,
            ClientBytes = 74727936,
            LegacyReceiptHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "d7b67a829f15af36df8eb1d05d16085b80c94e623aa6b3d9bd2bf42dd7d78dd0",
                "2a2a8c0ec17e3c580ff26a6f2218ba4a6e2f13c564bb75c3f6dd64c25ecbe9fa",
                "ce85e0f966e4311dfd9983ee918745b51325367e26da4b8ea89b77071ba0c4cb"
            }
        };
    }
}

internal static class PreludeInstallerBackend
{
    private const string ClientName = "TheIsleClient-Win64-Shipping.exe";
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    internal static string DefaultGameFolder()
    {
        string programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        return Path.Combine(String.IsNullOrEmpty(programFiles) ? @"C:\Program Files (x86)" : programFiles,
                            @"Steam\steamapps\common\The Isle\TheIsle\Binaries\Win64");
    }

    internal static string ValidateSelectedTarget(string selection, InstallerPolicy policy = null)
    {
        Require(!String.IsNullOrWhiteSpace(selection), "Informe ou selecione a pasta Win64 do The Isle.");
        string path = Environment.ExpandEnvironmentVariables(selection.Trim().Trim('"'));
        Require(Regex.IsMatch(path, @"^(?:[A-Za-z]:[\\/]|\\\\)"), "Informe o caminho completo da pasta Win64.");
        path = CheckPath(path);
        string target = Directory.Exists(path) ? Path.Combine(path, ClientName) : path;
        Layout(target);
        policy = policy ?? InstallerPolicy.Production();
        string hash = FileState(target);
        Require(SameHash(hash, policy.OriginalSha256) || SameHash(hash, policy.ClientSha256) ||
                SameHash(hash, policy.LegacyClientSha256),
                "Versão do jogo diferente da validada. Nenhum executável será substituído.");
        return target;
    }

    internal static string DiscoverGame()
    {
        string defaultTarget = Path.Combine(DefaultGameFolder(), ClientName);
        if (File.Exists(defaultTarget)) return CheckPath(defaultTarget);
        var steamCandidates = new List<string>();
        string programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        steamCandidates.Add(Path.Combine(String.IsNullOrEmpty(programFiles) ? @"C:\Program Files (x86)" : programFiles, "Steam"));
        AddRegistrySteam(steamCandidates, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        AddRegistrySteam(steamCandidates, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");

        var libraries = new List<string>(steamCandidates);
        foreach (string steam in steamCandidates)
        {
            foreach (string relative in new [] { @"steamapps\libraryfolders.vdf", @"config\libraryfolders.vdf" })
            {
                string vdf = Path.Combine(steam, relative);
                if (!File.Exists(vdf)) continue;
                string contents = File.ReadAllText(vdf, Encoding.UTF8);
                foreach (Match match in Regex.Matches(contents, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
                    libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
        }

        var found = new HashSet<string>(PathComparer);
        foreach (string library in libraries)
        {
            if (String.IsNullOrWhiteSpace(library)) continue;
            string manifest = Path.Combine(library, "steamapps", "appmanifest_376210.acf");
            if (!File.Exists(manifest)) continue;
            string contents = File.ReadAllText(manifest, Encoding.UTF8);
            Match app = Regex.Match(contents, "\"appid\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            Match folder = Regex.Match(contents, "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!app.Success || app.Groups[1].Value != "376210" || !folder.Success) continue;
            string name = folder.Groups[1].Value;
            Require(!String.IsNullOrEmpty(name) && name != "." && name != ".." &&
                    Path.GetFileName(name) == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
                    "Diretório inválido no manifesto Steam.");
            string target = Path.Combine(library, "steamapps", "common", name,
                                         "TheIsle", "Binaries", "Win64", ClientName);
            if (File.Exists(target)) found.Add(CheckPath(target));
        }
        Require(found.Count == 1, "Não foi possível identificar uma única instalação Steam do The Isle.");
        return found.First();
    }

    private static void AddRegistrySteam(List<string> candidates, RegistryKey hive, string path, string valueName)
    {
        try
        {
            using (RegistryKey key = hive.OpenSubKey(path))
            {
                if (key != null)
                {
                    string value = key.GetValue(valueName) as string;
                    if (!String.IsNullOrWhiteSpace(value)) candidates.Add(value);
                }
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    internal static void Perform(string action, string target, string supportDirectory,
                                 Action<int, string> progress, InstallerPolicy policy = null,
                                 Func<string, bool> failPoint = null)
    {
        Require(action == "install" || action == "uninstall", "Operação inválida.");
        policy = policy ?? InstallerPolicy.Production();
        ValidatePolicy(policy);
        string lockAnchor;
        if (supportDirectory == null)
            lockAnchor = CheckPath(Assembly.GetExecutingAssembly().Location);
        else
        {
            Require(!String.IsNullOrWhiteSpace(supportDirectory), "Suporte do instalador ausente.");
            supportDirectory = CheckPath(supportDirectory);
            Require(Directory.Exists(supportDirectory), "Suporte do instalador ausente.");
            lockAnchor = supportDirectory;
        }
        Report(progress, 5, "Localizando o The Isle pela Steam…");
        target = String.IsNullOrWhiteSpace(target) ? DiscoverGame() : CheckPath(target);
        Report(progress, 10, "Verificando o jogo…");
        string state = Layout(target);
        EnsureGameClosed(policy, target);
        string backup = Path.Combine(state, "original.client");
        using (var operation = new OperationLock(lockAnchor, target, backup))
        {
            state = Layout(target);
            IDictionary<string, object> oldReceipt = ExistingReceipt(state, target, policy);
            string current = FileState(target);
            Require(SameHash(current, policy.OriginalSha256) ||
                    SameHash(current, policy.ClientSha256) ||
                    SameHash(current, policy.LegacyClientSha256),
                    "Versão do jogo diferente da validada. Nenhum executável será substituído.");
            Report(progress, 25, "Verificando a instalação…");
            if (action == "install")
                Install(target, state, current, supportDirectory, policy, progress, failPoint);
            else
                Uninstall(target, state, current, oldReceipt, policy, progress, failPoint);
        }
    }

    private static void Install(string target, string state, string current, string supportDirectory,
                                InstallerPolicy policy, Action<int, string> progress, Func<string, bool> failPoint)
    {
        string backup = BackupOriginal(target, state, policy);
        string rollbackSource = backup;
        if (SameHash(current, policy.LegacyClientSha256))
        {
            rollbackSource = Path.Combine(state, "prior.client");
            string saved = FileState(rollbackSource);
            Require(saved == null || SameHash(saved, policy.LegacyClientSha256),
                    "Backup do cliente anterior divergente; preservado.");
            if (saved == null) CopyCommit(target, rollbackSource, policy.LegacyClientSha256, null, policy, target);
        }
        Report(progress, 40, "Preparando a instalação…");
        var receipt = NewReceipt(target, "preparing", policy);
        string staged = SameHash(current, policy.ClientSha256) ? null : StageFullClient(state, supportDirectory, policy, progress);
        Report(progress, 55, "Instalando…");
        try
        {
            WriteReceipt(state, receipt);
            FailAt(failPoint, "after_receipt_preparing");
            if (staged != null)
            {
                EnsureGameClosed(policy, target);
                Commit(staged, target, current, policy.ClientSha256);
                staged = null;
            }
            FailAt(failPoint, "after_client_commit");
            Report(progress, 80, "Conferindo a instalação…");
            Require(SameHash(FileState(target), policy.ClientSha256), "Instalação não confirmada.");
            Report(progress, 90, "Finalizando a instalação…");
            FailAt(failPoint, "before_receipt_installed");
            receipt["phase"] = "installed";
            WriteReceipt(state, receipt);
            Report(progress, 100, "Instalado com sucesso");
        }
        catch (Exception initial)
        {
            bool recovered = false;
            try
            {
                if (!SameHash(current, policy.ClientSha256) && SameHash(FileState(target), policy.ClientSha256))
                    CopyCommit(rollbackSource, target, current, policy.ClientSha256, policy, target);
                Require(SameHash(FileState(target), current), "Cliente anterior não confirmado.");
                recovered = true;
            }
            catch { }
            receipt["phase"] = recovered ? "rolled_back" : "recovery_needed";
            try { WriteReceipt(state, receipt); } catch { }
            throw new IOException(initial.Message + (recovered ? " A tentativa foi revertida." :
                    " Preserve a pasta Prelude para recuperação."), initial);
        }
        finally { DeleteTemporary(staged); }
    }

    private static void Uninstall(string target, string state, string current,
                                  IDictionary<string, object> oldReceipt, InstallerPolicy policy,
                                  Action<int, string> progress, Func<string, bool> failPoint)
    {
        Require(oldReceipt != null, "Registro não encontrado. Use o desinstalador da instalação anterior.");
        string backup = BackupOriginal(target, state, policy);
        Require(SameHash(FileState(backup), policy.OriginalSha256),
                "Backup original não confirmado. Nenhum executável será substituído.");
        Report(progress, 45, "Preparando a desinstalação…");
        var receipt = NewReceipt(target, "uninstalling", policy);
        string rollback = null;
        try
        {
            if (!SameHash(current, policy.OriginalSha256))
                rollback = StageCopy(target, Path.Combine(state, "rollback.client"), current);
            WriteReceipt(state, receipt);
            FailAt(failPoint, "after_receipt_uninstalling");
            Report(progress, 65, "Desinstalando…");
            if (!SameHash(current, policy.OriginalSha256))
                CopyCommit(backup, target, policy.OriginalSha256, current, policy, target);
            FailAt(failPoint, "after_restore_commit");
            Report(progress, 85, "Conferindo a restauração…");
            Require(SameHash(FileState(target), policy.OriginalSha256), "Desinstalação não confirmada.");
            Report(progress, 95, "Finalizando a desinstalação…");
            FailAt(failPoint, "before_receipt_uninstalled");
            receipt["phase"] = "uninstalled";
            WriteReceipt(state, receipt);
            Report(progress, 100, "Desinstalado com sucesso");
        }
        catch (Exception initial)
        {
            bool recovered = false;
            try
            {
                if (!SameHash(FileState(target), current))
                {
                    Require(rollback != null, "Cliente anterior não disponível para restauração.");
                    Commit(rollback, target, policy.OriginalSha256, current);
                    rollback = null;
                }
                Require(SameHash(FileState(target), current), "Cliente anterior não confirmado.");
                recovered = true;
            }
            catch { }
            receipt["phase"] = recovered ? "rolled_back" : "recovery_needed";
            try { WriteReceipt(state, receipt); } catch { }
            throw new IOException(initial.Message + (recovered ? " A tentativa foi revertida." :
                    " Preserve a pasta Prelude para recuperação."), initial);
        }
        finally { DeleteTemporary(rollback); }
    }

    private static string Layout(string target)
    {
        target = CheckPath(target);
        DirectoryInfo win64 = Directory.GetParent(target);
        Require(Path.GetFileName(target).Equals(ClientName, StringComparison.OrdinalIgnoreCase) && win64 != null &&
                win64.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase) &&
                win64.Parent != null && win64.Parent.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) &&
                win64.Parent.Parent != null && win64.Parent.Parent.Name.Equals("TheIsle", StringComparison.OrdinalIgnoreCase),
                "Instalação do The Isle não reconhecida.");
        Require(File.Exists(target), "Executável do The Isle não encontrado.");
        return CheckPath(Path.Combine(win64.Parent.Parent.FullName, "Prelude", "Recuperacao"));
    }

    private static string BackupOriginal(string target, string state, InstallerPolicy policy)
    {
        string backup = Path.Combine(state, "original.client");
        string saved = FileState(backup);
        if (SameHash(saved, policy.OriginalSha256)) return backup;
        Require(saved == null, "Backup original divergente. Preserve a pasta Prelude do jogo.");
        var sources = new List<string> { target };
        string previous = LegacyState(target);
        if (previous != null) sources.Add(Path.Combine(previous, "original.client"));
        string found = null;
        foreach (string source in sources)
        {
            if (File.Exists(source) && SameHash(FileState(source), policy.OriginalSha256))
            {
                found = CheckPath(source);
                break;
            }
        }
        Require(found != null, "Backup original não confirmado. Nenhum executável será substituído.");
        CopyCommit(found, backup, policy.OriginalSha256, null, policy, target);
        return backup;
    }

    private static string LegacyState(string target)
    {
        string root = Directory.GetParent(Directory.GetParent(Directory.GetParent(target).FullName).FullName).FullName;
        string previous = Path.Combine(root, "Prelude", "AES");
        if (!Directory.Exists(previous)) return null;
        CheckPath(previous);
        return previous;
    }

    private static IDictionary<string, object> ExistingReceipt(string state, string target, InstallerPolicy policy)
    {
        IDictionary<string, object> receipt = ReadReceipt(state, target, policy);
        if (receipt != null) return receipt;
        string previous = LegacyState(target);
        return previous == null ? null : ReadReceipt(previous, target, policy);
    }

    private static IDictionary<string, object> ReadReceipt(string state, string target, InstallerPolicy policy)
    {
        string path = Path.Combine(state, "receipt.json");
        if (!File.Exists(path)) return null;
        CheckPath(path);
        IDictionary<string, object> data = Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>;
        Require(data != null, "Registro de instalação inválido. Preserve a pasta Prelude do jogo.");
        long schema = Number(data, "schema");
        string phase = StringField(data, "phase");
        string oldTarget = StringField(data, "target");
        Require((schema == 1 || schema == 2) &&
                SameHash(StringField(data, "original_sha256"), policy.OriginalSha256) &&
                (SameHash(StringField(data, "client_sha256"), policy.ClientSha256) ||
                 SameHash(StringField(data, "client_sha256"), policy.LegacyClientSha256)) &&
                new [] { "preparing", "installed", "uninstalling", "uninstalled", "rolled_back", "recovery_needed" }.Contains(phase) &&
                !String.IsNullOrEmpty(oldTarget),
                "Registro de instalação inválido. Preserve a pasta Prelude do jogo.");
        if (schema == 1)
            Require(policy.LegacyReceiptHashes != null && policy.LegacyReceiptHashes.Contains(StringField(data, "pak_sha256")),
                    "Registro anterior de instalação inválido.");
        else
            Require(StringField(data, "component") == "prelude_client", "Registro de instalação inválido.");
        Require(PathComparer.Equals(CheckPath(oldTarget), CheckPath(target)), "Registro pertence a outra instalação.");
        return data;
    }

    private static IDictionary<string, object> NewReceipt(string target, string phase, InstallerPolicy policy)
    {
        return new Dictionary<string, object> {
            { "schema", 2 }, { "component", "prelude_client" }, { "target", target },
            { "original_sha256", policy.OriginalSha256 }, { "client_sha256", policy.ClientSha256 },
            { "phase", phase }
        };
    }

    private static void WriteReceipt(string state, IDictionary<string, object> receipt)
    {
        CheckPath(state);
        Directory.CreateDirectory(state);
        string destination = CheckPath(Path.Combine(state, "receipt.json"));
        string temporary = Path.Combine(state, ".receipt-" + Guid.NewGuid().ToString("N") + ".new");
        try
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(receipt));
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            ReplaceOrMove(temporary, destination);
        }
        finally { DeleteTemporary(temporary); }
    }

    private static string StageFullClient(string state, string supportDirectory,
                                          InstallerPolicy policy, Action<int, string> progress)
    {
        string temporary = Path.Combine(state, ".client-" + Guid.NewGuid().ToString("N") + ".new");
        Stream source = null;
        try
        {
            if (supportDirectory == null)
                source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Prelude.Client");
            else
                source = File.OpenRead(CheckPath(Path.Combine(supportDirectory, ClientName)));
            Require(source != null && source.Length == policy.ClientBytes,
                    "Executável do pacote ausente ou com tamanho incorreto.");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[1024 * 1024];
                long total = 0;
                int count, lastPercent = 40;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    Require(count <= policy.ClientBytes - total, "Executável do pacote acima do tamanho esperado.");
                    output.Write(buffer, 0, count);
                    total += count;
                    int percent = 40 + (int)(14 * total / policy.ClientBytes);
                    if (percent > lastPercent) {
                        Report(progress, percent, "Preparando o arquivo do jogo…");
                        lastPercent = percent;
                    }
                }
                Require(total == policy.ClientBytes, "Executável do pacote incompleto.");
                output.Flush(true);
            }
            Require(SameHash(FileState(temporary), policy.ClientSha256),
                    "Executável do pacote diferente da versão validada.");
            return temporary;
        }
        catch { DeleteTemporary(temporary); throw; }
        finally { if (source != null) source.Dispose(); }
    }

    private static string StageCopy(string source, string destination, string expected)
    {
        CheckPath(source);
        CheckPath(destination);
        string temporary = Path.Combine(Path.GetDirectoryName(destination),
                                        ".Prelude-" + Guid.NewGuid().ToString("N") + ".new");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            Require(SameHash(FileState(temporary), expected), "Hash incorreto ao preparar: " + Path.GetFileName(destination));
            return temporary;
        }
        catch { DeleteTemporary(temporary); throw; }
    }

    private static void CopyCommit(string source, string destination, string expected, string previous,
                                   InstallerPolicy policy, string gameTarget)
    {
        CheckPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        string temporary = StageCopy(source, destination, expected);
        try { EnsureGameClosed(policy, gameTarget); Commit(temporary, destination, previous, expected); }
        finally { DeleteTemporary(temporary); }
    }

    private static void Commit(string staged, string destination, string previous, string expected)
    {
        Require(SameHash(FileState(destination), previous), "O destino mudou durante a operação: " + Path.GetFileName(destination));
        ReplaceOrMove(staged, destination);
        Require(SameHash(FileState(destination), expected), "Hash final incorreto: " + Path.GetFileName(destination));
    }

    private static void ReplaceOrMove(string source, string destination)
    {
        CheckPath(source);
        CheckPath(destination);
        if (File.Exists(destination)) File.Replace(source, destination, null);
        else File.Move(source, destination);
    }

    private static string FileState(string path)
    {
        path = CheckPath(path);
        if (!File.Exists(path))
        {
            Require(!Directory.Exists(path), "O destino não é um arquivo: " + path);
            return null;
        }
        using (var input = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    private static string CheckPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = full;
        while (!String.IsNullOrEmpty(current))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                Require((attributes & FileAttributes.ReparsePoint) == 0, "Link/junction inesperado: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            string parent = Path.GetDirectoryName(current);
            if (String.IsNullOrEmpty(parent) || PathComparer.Equals(parent, current)) break;
            current = parent;
        }
        return full;
    }

    private static void EnsureGameClosed(InstallerPolicy policy, string target)
    {
#if PRELUDE_FIXTURE_TEST
        string ownedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
                             Path.DirectorySeparatorChar + "PreludeNativeFixture-";
        if (policy.SkipProcessGuardForOwnedFixtures &&
            CheckPath(target).StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase)) return;
#endif
        string[] names = {
            "theisle", "theisleclient-win64-shipping", "theisleclient-preludecrypto-test01",
            "theisleclient-unchangedbaseline"
        };
        foreach (string name in names)
        {
            Process[] running = Process.GetProcessesByName(name);
            try { Require(running.Length == 0, "Feche o The Isle antes de instalar ou restaurar."); }
            finally { foreach (Process process in running) process.Dispose(); }
        }
    }

    private sealed class OperationLock : IDisposable
    {
        private readonly List<System.Threading.Mutex> handles = new List<System.Threading.Mutex>();
        internal OperationLock(params string[] paths)
        {
            try
            {
                foreach (string path in paths)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(CheckPath(path).ToLowerInvariant());
                    string token;
                    using (var sha = SHA256.Create())
                        token = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    var mutex = new System.Threading.Mutex(false, @"Local\PreludeCryptoTEST01_" + token);
                    bool entered;
                    try { entered = mutex.WaitOne(0); }
                    catch (System.Threading.AbandonedMutexException) { entered = true; }
                    if (!entered)
                    {
                        mutex.Dispose();
                        throw new IOException("Outra operação está usando esta pasta ou cliente.");
                    }
                    handles.Add(mutex);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            for (int i = handles.Count - 1; i >= 0; i--)
            {
                try { handles[i].ReleaseMutex(); } finally { handles[i].Dispose(); }
            }
            handles.Clear();
        }
    }

    private static void ValidatePolicy(InstallerPolicy policy)
    {
        foreach (string hash in new [] { policy.OriginalSha256, policy.ClientSha256,
                                          policy.LegacyClientSha256 })
            Require(hash != null && Regex.IsMatch(hash, "\\A[0-9a-fA-F]{64}\\z"), "Política de instalação inválida.");
        Require(policy.OriginalBytes > 0 && policy.ClientBytes > 0, "Política de instalação inválida.");
    }

    private static long Number(IDictionary<string, object> data, string key)
    {
        object value;
        Require(data.TryGetValue(key, out value) && (value is int || value is long), "Número inválido: " + key);
        return Convert.ToInt64(value);
    }

    private static string StringField(IDictionary<string, object> data, string key)
    {
        object value;
        return data.TryGetValue(key, out value) ? value as string : null;
    }

    private static bool SameHash(string a, string b)
    {
        return String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static void Report(Action<int, string> callback, int percent, string message)
    {
        if (callback != null) callback(percent, message);
    }

    private static void FailAt(Func<string, bool> callback, string name)
    {
        if (callback != null && callback(name)) throw new IOException("Falha de teste: " + name);
    }

    private static void DeleteTemporary(string path)
    {
        if (path != null && File.Exists(path)) File.Delete(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }
}
