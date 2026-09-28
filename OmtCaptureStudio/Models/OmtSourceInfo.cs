namespace OmtCaptureStudio.Models;

public class OmtSourceInfo
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool IsManual { get; set; }

    public string DisplayName
    {
        get
        {
            // If Name and Host are both available and distinct
            if (!string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Host))
            {
                if (string.Equals(Name, Host, StringComparison.OrdinalIgnoreCase))
                    return Name;

                if (Name.EndsWith($"({Host})", StringComparison.OrdinalIgnoreCase))
                    return Name;

                return $"{Name} ({Host})";
            }

            // If only Name is present
            if (!string.IsNullOrWhiteSpace(Name))
            {
                if (!string.IsNullOrWhiteSpace(Address) && !string.Equals(Name, Address, StringComparison.OrdinalIgnoreCase))
                {
                    if (Address.Contains(Name, StringComparison.OrdinalIgnoreCase) || Name.Contains(Address, StringComparison.OrdinalIgnoreCase))
                        return Name;

                    return $"{Name} ({Address})";
                }
                return Name;
            }

            // Fallback to Address
            return Address;
        }
    }

    public override string ToString() => DisplayName;

    public static OmtSourceInfo Parse(string address, bool isManual = false)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new OmtSourceInfo { Address = string.Empty, IsManual = isManual };
        }

        string trimmed = address.Trim();
        string originalAddress = trimmed;

        // 1. Detect and unpack redundant repeated strings like "X (X)" or "X ( X )"
        // e.g. "WINDOWS-LL3KVGQ (vMix - Output 1) (WINDOWS-LL3KVGQ (vMix - Output 1))"
        while (true)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"^(.+?)\s*\(\s*\1\s*\)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                trimmed = match.Groups[1].Value.Trim();
            }
            else
            {
                break;
            }
        }

        string name = trimmed;
        string host = string.Empty;

        // 2. Pattern: "Source @ Host" e.g. "vMix - Output 1 @ WINDOWS-LL3KVGQ"
        int atIndex = trimmed.IndexOf('@');
        if (atIndex > 0)
        {
            string candidateName = trimmed.Substring(0, atIndex).Trim();
            string candidateHost = trimmed.Substring(atIndex + 1).Trim();
            if (!string.IsNullOrWhiteSpace(candidateHost) && !string.IsNullOrWhiteSpace(candidateName))
            {
                name = candidateName;
                host = candidateHost;
            }
        }
        else
        {
            // 3. Pattern: "Host (Source Name)" e.g. "WINDOWS-LL3KVGQ (vMix - Output 1)"
            int firstParen = trimmed.IndexOf('(');
            int lastParen = trimmed.LastIndexOf(')');
            if (firstParen > 0 && lastParen == trimmed.Length - 1)
            {
                string candidateHost = trimmed.Substring(0, firstParen).Trim();
                string candidateName = trimmed.Substring(firstParen + 1, lastParen - firstParen - 1).Trim();

                // If candidateName also ends with (candidateHost), strip it
                if (candidateName.EndsWith($"({candidateHost})", StringComparison.OrdinalIgnoreCase))
                {
                    candidateName = candidateName.Substring(0, candidateName.Length - candidateHost.Length - 2).Trim();
                }

                if (!string.IsNullOrWhiteSpace(candidateHost) && !string.IsNullOrWhiteSpace(candidateName))
                {
                    host = candidateHost;
                    name = candidateName;
                }
            }
        }

        // Clean up redundant host in name if present
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(name))
        {
            if (name.EndsWith($"({host})", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - host.Length - 2).Trim();
            }
        }

        return new OmtSourceInfo
        {
            Name = name,
            Host = host,
            Address = originalAddress,
            IsManual = isManual
        };
    }
}
