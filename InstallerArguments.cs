using System;
using System.IO;

internal static class InstallerArguments
{
    internal static bool TryParse(string[] args, out string action, out string target)
    {
        action = target = null;
        for (int i = 0; i < args.Length; i++) {
            if (i + 1 >= args.Length) return false;
            if (args[i] == "--action" && action == null) action = args[++i];
            else if (args[i] == "--target" && target == null) target = args[++i];
            else return false;
        }
        if (action != null && action != "install" && action != "uninstall") return false;
        if (target != null && (String.IsNullOrWhiteSpace(target) || target.IndexOf('"') >= 0 ||
                               !Path.IsPathRooted(target) ||
                               !String.Equals(Path.GetFileName(target), "TheIsleClient-Win64-Shipping.exe",
                                              StringComparison.OrdinalIgnoreCase))) return false;
        return action == null || target != null;
    }

    internal static string ForElevation(string action, string target)
    {
        string parsedAction, parsedTarget;
        if (!TryParse(new [] { "--action", action, "--target", target }, out parsedAction, out parsedTarget))
            throw new IOException("Argumentos de instalação inválidos.");
        return "--action " + action + " --target \"" + target + "\"";
    }
}
