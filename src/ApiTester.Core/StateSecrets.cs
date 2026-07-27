using System.Collections.Generic;

namespace ApiTester.Core;

/// <summary>What a save did that the user needs to be told about.</summary>
public sealed class StateSaveResult
{
    /// <summary>The timestamped copy taken of the previous file before the first rewrite in the
    /// encrypted format; null when no backup was needed.</summary>
    public string? BackupPath { get; init; }

    /// <summary>False when at least one secret had to be written in the clear because per-user
    /// encryption is unavailable on this system — losing the value would be worse than storing it.</summary>
    public bool SecretsEncrypted { get; init; } = true;
}

/// <summary>Encrypts and decrypts the secret-bearing fields of a workspace, leaving every other
/// field readable so the file stays diffable and debuggable.</summary>
public static class StateSecrets
{
    private const string ClosingWarning =
        "Secrets are encrypted for your Windows user — a workspace file copied from another user or machine cannot be read.";

    /// <summary>Encrypt every secret-bearing field in place. Call it on a CLONE — never on live
    /// state, whose objects are data-bound in the desktop application. Returns false when at least
    /// one non-empty secret had to be left in the clear.</summary>
    public static bool Protect(AppState state, ISecretProtector protector)
    {
        bool allEncrypted = true;

        foreach (var t in state.SessionTokens)
            t.Token = ProtectValue(t.Token, protector, ref allEncrypted);

        foreach (var c in state.SessionCookies)
            c.Value = ProtectValue(c.Value, protector, ref allEncrypted);

        foreach (var tab in state.Tabs)
            tab.AuthSecret = ProtectValueOptional(tab.AuthSecret, protector, ref allEncrypted);

        foreach (var h in state.History)
            h.AuthSecret = ProtectValueOptional(h.AuthSecret, protector, ref allEncrypted);

        foreach (var root in state.Collections)
            ProtectNode(root, protector, ref allEncrypted);

        foreach (var env in state.Environments)
            foreach (var v in env.Variables)
                if (v.Secret)
                    v.Value = ProtectValue(v.Value, protector, ref allEncrypted);

        return allEncrypted;

        static void ProtectNode(CollectionNode node, ISecretProtector p, ref bool allEncrypted)
        {
            if (node.Request is { } r) r.AuthSecret = ProtectValueOptional(r.AuthSecret, p, ref allEncrypted);
            foreach (var child in node.Children) ProtectNode(child, p, ref allEncrypted);
        }
    }

    // Skips empty and already-protected values; on a null result from the protector, leaves
    // the plaintext in place and flags that something was left unencrypted. For the non-nullable
    // fields (SessionToken.Token, SessionCookie.Value, Variable.Value): never returns null.
    private static string ProtectValue(string value, ISecretProtector protector, ref bool allEncrypted)
    {
        if (string.IsNullOrEmpty(value) || SecretProtection.LooksProtected(value)) return value;
        var protectedValue = protector.Protect(value);
        if (protectedValue is null)
        {
            allEncrypted = false;
            return value;
        }
        return protectedValue;
    }

    // Same rules, for the nullable auth-secret fields — null and empty both pass through unchanged.
    private static string? ProtectValueOptional(string? value, ISecretProtector protector, ref bool allEncrypted) =>
        string.IsNullOrEmpty(value) ? value : ProtectValue(value, protector, ref allEncrypted);

    /// <summary>Decrypt every stored secret in place. A value that cannot be decrypted is treated
    /// as absent (per the table below) and described in <paramref name="warnings"/>.</summary>
    public static void Unprotect(AppState state, ISecretProtector protector, List<string> warnings)
    {
        bool anyDropped = false;

        var survivingTokens = new List<SessionToken>(state.SessionTokens.Count);
        foreach (var t in state.SessionTokens)
        {
            if (SecretProtection.LooksProtected(t.Token))
            {
                var plain = protector.Unprotect(t.Token);
                if (plain is null)
                {
                    warnings.Add($"Could not decrypt the captured token for {t.Origin} — it was dropped.");
                    anyDropped = true;
                    continue;
                }
                t.Token = plain;
            }
            survivingTokens.Add(t);
        }
        state.SessionTokens.Clear();
        state.SessionTokens.AddRange(survivingTokens);

        var survivingCookies = new List<SessionCookie>(state.SessionCookies.Count);
        foreach (var c in state.SessionCookies)
        {
            if (SecretProtection.LooksProtected(c.Value))
            {
                var plain = protector.Unprotect(c.Value);
                if (plain is null)
                {
                    warnings.Add($"Could not decrypt the captured cookie \"{c.Name}\" for {c.Origin} — it was dropped.");
                    anyDropped = true;
                    continue;
                }
                c.Value = plain;
            }
            survivingCookies.Add(c);
        }
        state.SessionCookies.Clear();
        state.SessionCookies.AddRange(survivingCookies);

        for (int i = 0; i < state.Tabs.Count; i++)
        {
            var tab = state.Tabs[i];
            if (!SecretProtection.LooksProtected(tab.AuthSecret)) continue;
            var plain = protector.Unprotect(tab.AuthSecret!);
            if (plain is null)
            {
                tab.AuthSecret = null;
                warnings.Add($"Could not decrypt the auth secret on open tab {i + 1} ({tab.Method} {tab.Path}) — it was left empty.");
                anyDropped = true;
            }
            else
            {
                tab.AuthSecret = plain;
            }
        }

        for (int i = 0; i < state.History.Count; i++)
        {
            var h = state.History[i];
            if (!SecretProtection.LooksProtected(h.AuthSecret)) continue;
            var plain = protector.Unprotect(h.AuthSecret!);
            if (plain is null)
            {
                h.AuthSecret = null;
                warnings.Add($"Could not decrypt the auth secret on history entry {i + 1} ({h.Method} {h.EffectiveUrl}) — it was left empty.");
                anyDropped = true;
            }
            else
            {
                h.AuthSecret = plain;
            }
        }

        foreach (var root in state.Collections)
            UnprotectNode(root, protector, root.Name, warnings, ref anyDropped);

        foreach (var env in state.Environments)
        {
            foreach (var v in env.Variables)
            {
                if (!v.Secret || !SecretProtection.LooksProtected(v.Value)) continue;
                var plain = protector.Unprotect(v.Value);
                if (plain is null)
                {
                    v.Value = "";
                    warnings.Add($"Could not decrypt environment variable \"{v.Key}\" in \"{env.Name}\" — it was left empty.");
                    anyDropped = true;
                }
                else
                {
                    v.Value = plain;
                }
            }
        }

        if (anyDropped) warnings.Add(ClosingWarning);

        static void UnprotectNode(CollectionNode node, ISecretProtector p, string path, List<string> warnings, ref bool anyDropped)
        {
            if (node.Request is { } r && SecretProtection.LooksProtected(r.AuthSecret))
            {
                var plain = p.Unprotect(r.AuthSecret!);
                if (plain is null)
                {
                    r.AuthSecret = null;
                    warnings.Add($"Could not decrypt the auth secret on saved request \"{path}\" — it was left empty.");
                    anyDropped = true;
                }
                else
                {
                    r.AuthSecret = plain;
                }
            }
            foreach (var child in node.Children)
                UnprotectNode(child, p, path + "/" + child.Name, warnings, ref anyDropped);
        }
    }

    /// <summary>Remove every secret from a workspace, leaving it usable with the secrets simply
    /// absent. Call it on a CLONE — never on live state. This is what an export does by default:
    /// an exported workspace is a file people email to each other.</summary>
    public static SecretStripSummary Strip(AppState state)
    {
        int tokens = state.SessionTokens.Count;
        state.SessionTokens.Clear();

        int cookies = state.SessionCookies.Count;
        state.SessionCookies.Clear();

        int authSecrets = 0;
        foreach (var tab in state.Tabs)
            if (!string.IsNullOrEmpty(tab.AuthSecret)) { tab.AuthSecret = null; authSecrets++; }

        foreach (var h in state.History)
            if (!string.IsNullOrEmpty(h.AuthSecret)) { h.AuthSecret = null; authSecrets++; }

        foreach (var root in state.Collections)
            authSecrets += StripNode(root);

        int variables = 0;
        foreach (var env in state.Environments)
            foreach (var v in env.Variables)
                if (v.Secret && !string.IsNullOrEmpty(v.Value)) { v.Value = ""; variables++; }

        return new SecretStripSummary(tokens, cookies, authSecrets, variables);

        static int StripNode(CollectionNode node)
        {
            int count = 0;
            if (node.Request is { } r && !string.IsNullOrEmpty(r.AuthSecret)) { r.AuthSecret = null; count++; }
            foreach (var child in node.Children) count += StripNode(child);
            return count;
        }
    }
}

/// <summary>How many secrets a strip removed, so the caller can say what left the building.</summary>
public sealed record SecretStripSummary(int Tokens, int Cookies, int AuthSecrets, int Variables)
{
    /// <summary>True when anything at all was stripped.</summary>
    public bool Any => Tokens > 0 || Cookies > 0 || AuthSecrets > 0 || Variables > 0;

    /// <summary>A human list of what went, e.g. "2 captured tokens, 1 saved auth secret".
    /// Empty string when nothing was stripped.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Tokens > 0) parts.Add(Pluralize(Tokens, "captured token"));
        if (Cookies > 0) parts.Add(Pluralize(Cookies, "captured cookie"));
        if (AuthSecrets > 0) parts.Add(Pluralize(AuthSecrets, "saved auth secret"));
        if (Variables > 0) parts.Add(Pluralize(Variables, "secret variable"));
        return string.Join(", ", parts);

        static string Pluralize(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
    }
}
