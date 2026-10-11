using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal static class NativeBackendTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int checks;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        checks++;
    }

    private static string Sha(byte[] bytes)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private static string ShaFile(string path)
    {
        return Sha(File.ReadAllBytes(path));
    }

    private static void ExpectFailure(Action action, string label)
    {
        try { action(); }
        catch (IOException) { checks++; return; }
        throw new Exception("FAIL: expected failure at " + label);
    }

    private static string Phase(string receiptPath)
    {
        var receipt = Json.DeserializeObject(File.ReadAllText(receiptPath, Encoding.UTF8))
            as IDictionary<string, object>;
        Assert(receipt != null, "receipt is valid JSON");
        return receipt["phase"] as string;
    }

    private static void CheckProgress(List<int> points, bool success)
    {
        Assert(points.Count > 0, "progress events were reported");
        for (int i = 1; i < points.Count; i++)
            Assert(points[i] >= points[i - 1], "progress is monotonic");
        Assert((points[points.Count - 1] == 100) == success,
            "100% is reported only after successful completion");
    }

    private static void CheckPreserved(IDictionary<string, string> hashes)
    {
        foreach (var entry in hashes)
            Assert(ShaFile(entry.Key) == entry.Value, "PAK/SIG was preserved: " + Path.GetFileName(entry.Key));
    }

    private static void Run(string action, string target, string support, InstallerPolicy policy,
                            bool success, string failStep)
    {
        var points = new List<int>();
        Action perform = delegate {
            PreludeInstallerBackend.Perform(action, target, support,
                delegate(int value, string message) { points.Add(value); }, policy,
                failStep == null ? null : new Func<string, bool>(delegate(string step) { return step == failStep; }));
        };
        if (success) perform();
        else ExpectFailure(perform, failStep ?? action);
        CheckProgress(points, success);
    }

    private static void CheckRealClientInTemporaryGame(string root)
    {
        string source = Environment.GetEnvironmentVariable("PRELUDE_ORIGINAL_CLIENT_FOR_TEST");
        string support = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "Support"));
        if (!File.Exists(Path.Combine(support, "TheIsleClient-Win64-Shipping.exe")))
            throw new Exception("Full client payload is missing.");
        if (String.IsNullOrWhiteSpace(source) || !File.Exists(source) ||
            !File.Exists(Path.Combine(support, "TheIsleClient-Win64-Shipping.exe")))
        {
            Console.WriteLine("SKIP: validated original backup or production full client is unavailable.");
            return;
        }
        InstallerPolicy policy = InstallerPolicy.Production();
#if PRELUDE_FIXTURE_TEST
        policy.SkipProcessGuardForOwnedFixtures = true;
#endif
        Assert(ShaFile(source) == policy.OriginalSha256,
            "read-only original backup matches validated SHA-256");
        string target = Path.Combine(root, "RealClientFakeGame", "TheIsle", "Binaries", "Win64",
                                     "TheIsleClient-Win64-Shipping.exe");
        string paks = Path.Combine(root, "RealClientFakeGame", "TheIsle", "Content", "Paks");
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        Directory.CreateDirectory(paks);
        File.Copy(source, target);
        string pak = Path.Combine(paks, "TheIsle-Zzz_PreludeModServerV09.3.pak");
        string sig = Path.ChangeExtension(pak, ".sig");
        File.WriteAllText(pak, "REAL-DELTA TEST PAK SENTINEL", Encoding.ASCII);
        File.WriteAllText(sig, "REAL-DELTA TEST SIG SENTINEL", Encoding.ASCII);
        var preserved = new Dictionary<string, string> {
            { pak, ShaFile(pak) }, { sig, ShaFile(sig) }
        };
        Run("install", target, support, policy, true, null);
        Assert(ShaFile(target) == policy.ClientSha256,
            "production full client recreates exact modified EXE SHA-256 in temporary tree");
        CheckPreserved(preserved);
        Run("uninstall", target, support, policy, true, null);
        Assert(ShaFile(target) == policy.OriginalSha256,
            "production uninstall restores original EXE SHA-256 in temporary tree");
        CheckPreserved(preserved);
        Run("install", target, null, policy, true, null);
        Assert(ShaFile(target) == policy.ClientSha256,
            "embedded production full client recreates exact modified EXE SHA-256");
        CheckPreserved(preserved);
        Run("uninstall", target, null, policy, true, null);
        Assert(ShaFile(target) == policy.OriginalSha256,
            "embedded production uninstall restores original EXE SHA-256");
        CheckPreserved(preserved);
        Assert(ShaFile(source) == policy.OriginalSha256,
            "read-only original backup remains unchanged");
        Console.WriteLine("PASS: file and embedded production full client reconstructed and restored in temporary fake game trees.");
    }

    private static void CheckLegacyReceiptMigration(string root, string support,
                                                     InstallerPolicy policy, byte[] original)
    {
        string target = Path.Combine(root, "LegacyFakeGame", "TheIsle", "Binaries", "Win64",
                                     "TheIsleClient-Win64-Shipping.exe");
        string theIsle = Path.Combine(root, "LegacyFakeGame", "TheIsle");
        string legacy = Path.Combine(theIsle, "Prelude", "AES");
        string recovery = Path.Combine(theIsle, "Prelude", "Recuperacao");
        string paks = Path.Combine(theIsle, "Content", "Paks");
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(paks);
        byte[] legacyClient = Encoding.ASCII.GetBytes("legacy-fixture");
        Assert(Sha(legacyClient) == policy.LegacyClientSha256,
            "legacy client fixture matches policy SHA-256");
        File.WriteAllBytes(target, legacyClient);
        File.WriteAllBytes(Path.Combine(legacy, "original.client"), original);
        string legacyPakHash = Sha(Encoding.ASCII.GetBytes("legacy-receipt-pak"));
        policy.LegacyReceiptHashes.Add(legacyPakHash);
        var oldReceipt = new {
            schema = 1, target = target, original_sha256 = policy.OriginalSha256,
            client_sha256 = policy.LegacyClientSha256, phase = "installed",
            pak_sha256 = legacyPakHash
        };
        File.WriteAllText(Path.Combine(legacy, "receipt.json"), Json.Serialize(oldReceipt),
                          new UTF8Encoding(false));
        string pak = Path.Combine(paks, "LegacyPlayer.pak");
        string sig = Path.ChangeExtension(pak, ".sig");
        File.WriteAllText(pak, "LEGACY PAK SENTINEL", Encoding.ASCII);
        File.WriteAllText(sig, "LEGACY SIG SENTINEL", Encoding.ASCII);
        var preserved = new Dictionary<string, string> {
            { pak, ShaFile(pak) }, { sig, ShaFile(sig) }
        };
        Run("install", target, support, policy, true, null);
        Assert(ShaFile(target) == policy.ClientSha256, "legacy client migrated to current EXE");
        Assert(ShaFile(Path.Combine(recovery, "original.client")) == policy.OriginalSha256,
            "original backup migrated into recovery directory");
        Assert(ShaFile(Path.Combine(recovery, "prior.client")) == policy.LegacyClientSha256,
            "prior client saved for recovery");
        var receipt = Json.DeserializeObject(File.ReadAllText(Path.Combine(recovery, "receipt.json"), Encoding.UTF8))
            as IDictionary<string, object>;
        Assert(receipt != null && Convert.ToInt32(receipt["schema"]) == 2 &&
               (string)receipt["component"] == "prelude_client" && (string)receipt["phase"] == "installed",
               "schema 1 receipt migrated to schema 2");
        CheckPreserved(preserved);
        Run("uninstall", target, support, policy, true, null);
        Assert(ShaFile(target) == policy.OriginalSha256, "migrated install restores original");
        CheckPreserved(preserved);
    }

    [STAThread]
    private static int Main()
    {
        string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string root = Path.Combine(temporary, "PreludeNativeFixture-" + Guid.NewGuid().ToString("N"));
        string expectedPrefix = temporary + Path.DirectorySeparatorChar + "PreludeNativeFixture-";
        Assert(Path.GetFullPath(root).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase),
            "fixture remains inside the temporary directory");
        try
        {
            string target = Path.Combine(root, "FakeGame", "TheIsle", "Binaries", "Win64",
                                         "TheIsleClient-Win64-Shipping.exe");
            string paks = Path.Combine(root, "FakeGame", "TheIsle", "Content", "Paks");
            string support = Path.Combine(root, "Support");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            Directory.CreateDirectory(paks);
            Directory.CreateDirectory(support);

            byte[] original = new byte[12288];
            for (int i = 0; i < original.Length; i++) original[i] = (byte)((i * 17 + 29) % 251);
            byte[] client = new byte[original.Length + 96];
            Array.Copy(original, client, original.Length);
            byte[] first = Encoding.ASCII.GetBytes("DEMO");
            byte[] middle = Encoding.ASCII.GetBytes("NATIVE-OK");
            byte[] appended = new byte[96];
            for (int i = 0; i < appended.Length; i++) appended[i] = (byte)(255 - i);
            Array.Copy(first, 0, client, 0, first.Length);
            Array.Copy(middle, 0, client, 8192, middle.Length);
            Array.Copy(appended, 0, client, original.Length, appended.Length);
            File.WriteAllBytes(target, original);
            File.WriteAllBytes(Path.Combine(support, "TheIsleClient-Win64-Shipping.exe"), client);
            var policy = new InstallerPolicy {
                OriginalSha256 = Sha(original), ClientSha256 = Sha(client),
                LegacyClientSha256 = Sha(Encoding.ASCII.GetBytes("legacy-fixture")),
                OriginalBytes = original.Length,
                ClientBytes = client.Length,
                LegacyReceiptHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
#if PRELUDE_FIXTURE_TEST
            policy.SkipProcessGuardForOwnedFixtures = true;
#endif
            Assert(PreludeInstallerBackend.ValidateSelectedTarget(Path.GetDirectoryName(target), policy) == target,
                   "manual Win64 folder resolves exact target");
            Assert(PreludeInstallerBackend.ValidateSelectedTarget("\"" + target + "\"", policy) == target,
                   "quoted full executable path is supported");
            Environment.SetEnvironmentVariable("PRELUDE_TEST_WIN64", Path.GetDirectoryName(target));
            try {
                Assert(PreludeInstallerBackend.ValidateSelectedTarget("%PRELUDE_TEST_WIN64%", policy) == target,
                       "environment variables in manual path are expanded");
            } finally { Environment.SetEnvironmentVariable("PRELUDE_TEST_WIN64", null); }
            ExpectFailure(delegate { PreludeInstallerBackend.ValidateSelectedTarget("", policy); }, "empty manual path");
            ExpectFailure(delegate { PreludeInstallerBackend.ValidateSelectedTarget("Win64", policy); }, "relative manual path");
            ExpectFailure(delegate { PreludeInstallerBackend.ValidateSelectedTarget(paks, policy); }, "wrong folder");
            ExpectFailure(delegate { PreludeInstallerBackend.ValidateSelectedTarget(Path.Combine(root, "missing.exe"), policy); }, "missing client");
            Assert(!Directory.Exists(Path.Combine(root, "FakeGame", "TheIsle", "Prelude")),
                   "preflight performs no filesystem writes");

            string pak = Path.Combine(paks, "TheIsle-Zzz_PreludeModServerV09.3.pak");
            string sig = Path.ChangeExtension(pak, ".sig");
            string unrelated = Path.Combine(paks, "Unrelated.pak");
            File.WriteAllText(pak, "FAKE PAK SENTINEL", Encoding.ASCII);
            File.WriteAllText(sig, "FAKE SIG SENTINEL", Encoding.ASCII);
            File.WriteAllText(unrelated, "UNRELATED", Encoding.ASCII);
            var preserved = new Dictionary<string, string> {
                { pak, ShaFile(pak) }, { sig, ShaFile(sig) }, { unrelated, ShaFile(unrelated) }
            };
            string state = Path.Combine(root, "FakeGame", "TheIsle", "Prelude", "Recuperacao");
            string backup = Path.Combine(state, "original.client");
            string receipt = Path.Combine(state, "receipt.json");

            Run("install", target, support, policy, true, null);
            Assert(ShaFile(target) == policy.ClientSha256, "modified EXE matches expected SHA-256");
            Assert(ShaFile(backup) == policy.OriginalSha256, "backup matches original SHA-256");
            Assert(Phase(receipt) == "installed", "install receipt is final");
            CheckPreserved(preserved);
            Run("install", target, support, policy, true, null);
            Assert(ShaFile(target) == policy.ClientSha256, "repeat install is idempotent");
            CheckPreserved(preserved);

            Assert(PreludeInstallerBackend.ValidateSelectedTarget(target, policy) == target,
                   "preflight accepts already installed client");
            Run("uninstall", target, support, policy, true, null);
            Assert(ShaFile(target) == policy.OriginalSha256, "uninstall restores original SHA-256");
            Assert(Phase(receipt) == "uninstalled", "uninstall receipt is final");
            CheckPreserved(preserved);
            Run("uninstall", target, support, policy, true, null);
            Assert(ShaFile(target) == policy.OriginalSha256, "repeat uninstall is idempotent");

            Run("install", target, support, policy, false, "after_client_commit");
            Assert(ShaFile(target) == policy.OriginalSha256, "failed install rolls back EXE");
            Assert(Phase(receipt) == "rolled_back", "failed install records rollback");
            CheckPreserved(preserved);

            Run("install", target, support, policy, true, null);
            Run("uninstall", target, support, policy, false, "after_restore_commit");
            Assert(ShaFile(target) == policy.ClientSha256, "failed uninstall rolls back EXE");
            Assert(Phase(receipt) == "rolled_back", "failed uninstall records rollback");
            CheckPreserved(preserved);
            Run("uninstall", target, support, policy, true, null);
            Assert(ShaFile(target) == policy.OriginalSha256, "uninstall succeeds after rollback");

            byte[] changedClient = (byte[])client.Clone();
            changedClient[changedClient.Length - 1] ^= 1;
            File.WriteAllBytes(Path.Combine(support, "TheIsleClient-Win64-Shipping.exe"), changedClient);
            Run("install", target, support, policy, false, null);
            Assert(ShaFile(target) == policy.OriginalSha256, "tampered full client does not change EXE");
            CheckPreserved(preserved);
            File.WriteAllBytes(Path.Combine(support, "TheIsleClient-Win64-Shipping.exe"), client);

            File.WriteAllBytes(Path.Combine(support, "TheIsleClient-Win64-Shipping.exe"), new byte[1]);
            Run("install", target, support, policy, false, null);
            Assert(ShaFile(target) == policy.OriginalSha256, "truncated client leaves original untouched");
            Assert(Directory.GetFiles(state, ".client-*.new").Length == 0, "invalid payload staging is removed");
            File.WriteAllBytes(Path.Combine(support, "TheIsleClient-Win64-Shipping.exe"), client);

            File.WriteAllText(target, "unknown game version", Encoding.ASCII);
            ExpectFailure(delegate { PreludeInstallerBackend.ValidateSelectedTarget(target, policy); }, "unsupported client preflight");
            Run("install", target, support, policy, false, null);
            Assert(File.ReadAllText(target, Encoding.ASCII) == "unknown game version",
                "unknown EXE remains untouched");
            CheckPreserved(preserved);
            CheckLegacyReceiptMigration(root, support, policy, original);
            CheckRealClientInTemporaryGame(root);
            Console.WriteLine("PASS: " + checks + " fixture checks; no installed game or Steam discovery used.");
            return 0;
        }
        finally
        {
            string full = Path.GetFullPath(root);
            Assert(full.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase),
                "cleanup target remains inside the owned temporary directory");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
