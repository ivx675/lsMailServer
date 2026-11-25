using LumiSoft.Net.SMTP;

namespace lsMailServer.Services 
{
    /// <summary>
    /// Evaluates whether an authenticated user is authorized to send mail using a specific
    /// envelope <c>MAIL FROM</c> or RFC 5322 <c>From:</c> address, supporting exact addresses,
    /// user aliases, and semicolon-separated <c>*@domain.com</c> / <c>user@domain.com</c> delegation rules.
    /// </summary>
    public static class SendAsPolicy
    {
        #region record EvaluationResult

        /// <summary>
        /// Represents the result of a Send-As authorization check.
        /// </summary>
        /// <param name="IsAllowed"><see langword="true"/> if the sender address is permitted; otherwise <see langword="false"/>.</param>
        /// <param name="MatchedRule">The rule or alias that granted permission (for audit logging), or <see langword="null"/> if denied.</param>
        /// <param name="DenialResponse">An <see cref="SMTP_ServerResponse"/> with RFC 3463 enhanced status code when <paramref name="IsAllowed"/> is <see langword="false"/>.</param>
        public sealed record EvaluationResult(
            bool IsAllowed,
            string? MatchedRule,
            SMTP_ServerResponse? DenialResponse)
        {
            /// <summary>
            /// Creates an approved <see cref="EvaluationResult"/> recording the matching rule or alias.
            /// </summary>
            /// <param name="matchedRule">A descriptive identifier of the rule or owned address that authorized the sender.</param>
            /// <returns>An <see cref="EvaluationResult"/> with <see cref="IsAllowed"/> set to <see langword="true"/>.</returns>
            public static EvaluationResult Allow(string matchedRule) => new(true, matchedRule, null);

            /// <summary>
            /// Creates a denied <see cref="EvaluationResult"/> with a structured SMTP server rejection response.
            /// </summary>
            /// <param name="response">The <see cref="SMTP_ServerResponse"/> containing the SMTP reply code, enhanced status code, and reason text.</param>
            /// <returns>An <see cref="EvaluationResult"/> with <see cref="IsAllowed"/> set to <see langword="false"/>.</returns>
            public static EvaluationResult Deny(SMTP_ServerResponse response) => new(false, null, response);
        }

        #endregion

        #region record SendAsRule

        /// <summary>
        /// Represents a single parsed Send-As delegation rule.
        /// </summary>
        /// <param name="CanonicalText">The canonical lowercase representation of the rule (e.g., <c>"*"</c>, <c>"*@example.com"</c>, or <c>"user@example.com"</c>).</param>
        /// <param name="IsGlobalWildcard"><see langword="true"/> if the rule is <c>*</c> (any hosted local address).</param>
        /// <param name="IsDomainWildcard"><see langword="true"/> if the rule is <c>*@domain</c> (any address under <paramref name="Domain"/>).</param>
        /// <param name="Domain">The target domain name when <paramref name="IsDomainWildcard"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
        /// <param name="ExactAddress">The exact delegated email address when neither wildcard flag is set; otherwise <see langword="null"/>.</param>
        private sealed record SendAsRule(
            string CanonicalText,
            bool IsGlobalWildcard,
            bool IsDomainWildcard,
            string? Domain,
            string? ExactAddress);

        #endregion


        #region static method Evaluate

        /// <summary>
        /// Validates whether <paramref name="candidateAddress"/> is permitted for an authenticated user.
        /// </summary>
        /// <param name="candidateAddress">
        /// The normalized or raw email address from <c>MAIL FROM:&lt;...&gt;</c> or header <c>From:</c>.
        /// </param>
        /// <param name="ownedUserAddresses">
        /// The user's primary and alias email addresses (locked down by default).
        /// </param>
        /// <param name="sendAsRulesRaw">
        /// Optional semicolon-separated delegation string (e.g., <c>"*@example.com; support@other.com"</c>).
        /// </param>
        /// <param name="hostedLocalDomains">
        /// Optional set of enabled local domains on this mail server. When provided, wildcard rules
        /// (<c>*@domain.com</c> and <c>*</c>) only match domains that are actively hosted on the server.
        /// </param>
        /// <param name="allowNullSender">
        /// Whether an empty envelope sender (<c>&lt;&gt;</c>) is permitted. Defaults to <see langword="false"/>
        /// for authenticated user submission.
        /// </param>
        /// <returns>An <see cref="EvaluationResult"/> indicating approval or denial with an <see cref="SMTP_ServerResponse"/>.</returns>
        public static EvaluationResult Evaluate(
            string? candidateAddress,
            IEnumerable<string>? ownedUserAddresses,
            string? sendAsRulesRaw,
            ISet<string>? hostedLocalDomains = null,
            bool allowNullSender = false)
        {
            string normalizedAddress = NormalizeAddress(candidateAddress);

            // 1. Handle null sender (<>)
            if (string.IsNullOrEmpty(normalizedAddress)) {
                return allowNullSender
                    ? EvaluationResult.Allow("<> (null-sender)")
                    : EvaluationResult.Deny(new SMTP_ServerResponse(
                        550,
                        new SMTP_t_EnhancedStatusCode(5, 7, 1),
                        "Null sender (<>) is not permitted for authenticated submission."));
            }

            if (!TrySplitAddress(normalizedAddress, out string localPart, out string domainPart)) {
                return EvaluationResult.Deny(new SMTP_ServerResponse(
                    553,
                    new SMTP_t_EnhancedStatusCode(5, 1, 7),
                    $"Invalid sender address syntax '{normalizedAddress}'."));
            }

            // 2. Default lockdown: check user's own assigned addresses/aliases first.
            if (ownedUserAddresses != null) {
                foreach (string owned in ownedUserAddresses) {
                    string normalizedOwned = NormalizeAddress(owned);
                    if (!string.IsNullOrEmpty(normalizedOwned) &&
                        string.Equals(normalizedOwned, normalizedAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        return EvaluationResult.Allow($"owned:{normalizedOwned}");
                    }
                }
            }

            // 3. Evaluate semicolon-separated Send-As / Send-on-Behalf delegation rules.
            foreach (SendAsRule rule in ParseRules(sendAsRulesRaw)) {
                if (rule.IsGlobalWildcard) {
                    if (hostedLocalDomains == null || hostedLocalDomains.Contains(domainPart)) {
                        return EvaluationResult.Allow("rule:*");
                    }
                    continue;
                }

                if (rule.IsDomainWildcard) {
                    if (string.Equals(rule.Domain, domainPart, StringComparison.OrdinalIgnoreCase)) {
                        // Guardrail: ensure wildcard send-as only applies to domains hosted on this server
                        if (hostedLocalDomains != null && !hostedLocalDomains.Contains(domainPart)) {
                            return EvaluationResult.Deny(new SMTP_ServerResponse(
                                550,
                                new SMTP_t_EnhancedStatusCode(5, 7, 1),
                                $"Wildcard Send-As for '@{domainPart}' is not permitted because the domain is not active locally."));
                        }

                        return EvaluationResult.Allow($"rule:*@{rule.Domain}");
                    }
                    continue;
                }

                // Exact delegated address match (e.g., "billing@example.com")
                if (string.Equals(rule.ExactAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase)) {
                    return EvaluationResult.Allow($"rule:{rule.ExactAddress}");
                }
            }

            return EvaluationResult.Deny(new SMTP_ServerResponse(
                550,
                new SMTP_t_EnhancedStatusCode(5, 7, 1),
                $"Sender address '{normalizedAddress}' is not authorized for this authenticated account."));
        }

        #endregion

        #region static method ParseRules

        /// <summary>
        /// Parses and validates a semicolon-separated list of Send-As rules, returning normalized entries.
        /// Invalid or blank entries are skipped.
        /// </summary>
        /// <param name="rawRules">Raw semicolon-delimited string (e.g., <c>"*@domain.com; user@other.com"</c>).</param>
        /// <returns>A read-only list of parsed <see cref="SendAsRule"/> items.</returns>
        private static IReadOnlyList<SendAsRule> ParseRules(string? rawRules)
        {
            if (string.IsNullOrWhiteSpace(rawRules))
                return Array.Empty<SendAsRule>();

            var rules = new List<SendAsRule>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] tokens = rawRules.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string rawToken in tokens) {
                string token = NormalizeAddress(rawToken);
                if (string.IsNullOrEmpty(token) || !seen.Add(token))
                    continue;

                if (token == "*") {
                    rules.Add(new SendAsRule("*", IsGlobalWildcard: true, IsDomainWildcard: false, Domain: null, ExactAddress: null));
                    continue;
                }

                if (token.StartsWith("*@", StringComparison.Ordinal)) {
                    string domain = token.Substring(2).Trim().ToLowerInvariant();
                    if (IsValidDomain(domain)) {
                        rules.Add(new SendAsRule($"*@{domain}", IsGlobalWildcard: false, IsDomainWildcard: true, Domain: domain, ExactAddress: null));
                    }
                    continue;
                }

                if (TrySplitAddress(token, out _, out _)) {
                    rules.Add(new SendAsRule(token, IsGlobalWildcard: false, IsDomainWildcard: false, Domain: null, ExactAddress: token));
                }
            }

            return rules;
        }

        #endregion

        #region satic method NormalizeAndValidateRulesInput

        /// <summary>
        /// Canonicalizes a raw semicolon-separated rule list into a clean, deduplicated string suitable for database storage.
        /// </summary>
        /// <param name="rawRules">User-supplied input from the admin UI or API.</param>
        /// <returns>Normalized semicolon-separated string, or <see langword="null"/> if empty.</returns>
        /// <exception cref="ArgumentException">Thrown when any non-empty rule token has invalid email or wildcard domain syntax.</exception>
        public static string? NormalizeAndValidateRulesInput(string? rawRules)
        {
            if (string.IsNullOrWhiteSpace(rawRules))
                return null;

            var normalizedTokens = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] tokens = rawRules.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string rawToken in tokens) {
                string token = NormalizeAddress(rawToken);
                if (string.IsNullOrEmpty(token))
                    continue;

                if (token == "*") {
                    if (seen.Add("*")) normalizedTokens.Add("*");
                    continue;
                }

                if (token.StartsWith("*@", StringComparison.Ordinal)) {
                    string domain = token.Substring(2).Trim().ToLowerInvariant();
                    if (!IsValidDomain(domain))
                        throw new ArgumentException($"Invalid wildcard Send-As domain rule: '{rawToken}'. Expected format '*@domain.com'.");

                    string canonical = $"*@{domain}";
                    if (seen.Add(canonical)) normalizedTokens.Add(canonical);
                    continue;
                }

                if (!TrySplitAddress(token, out _, out _))
                    throw new ArgumentException($"Invalid Send-As email rule: '{rawToken}'. Expected 'user@domain.com' or '*@domain.com'.");

                if (seen.Add(token)) normalizedTokens.Add(token);
            }

            return normalizedTokens.Count > 0 ? string.Join("; ", normalizedTokens) : null;
        }

        #endregion

        #region static method NormalizeAddress

        /// <summary>
        /// Strips optional angle brackets (<c>&lt;</c>, <c>&gt;</c>) and trims/lowercases an email address.
        /// </summary>
        /// <param name="address">The raw email address or rule token.</param>
        /// <returns>A trimmed, lowercase address without enclosing angle brackets, or <see cref="string.Empty"/> if blank.</returns>
        private static string NormalizeAddress(string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return string.Empty;

            string trimmed = address.Trim();
            if (trimmed.StartsWith('<') && trimmed.EndsWith('>') && trimmed.Length >= 2) {
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
            }

            return trimmed.ToLowerInvariant();
        }

        #endregion

        #region static method TrySplitAddress

        /// <summary>
        /// Splits a normalized email address into local-part and domain-part while rejecting malformed syntax.
        /// </summary>
        /// <param name="normalizedAddress">A trimmed, lowercase email address.</param>
        /// <param name="localPart">When this method returns <see langword="true"/>, contains the local-part before <c>@</c>.</param>
        /// <param name="domainPart">When this method returns <see langword="true"/>, contains the domain-part after <c>@</c>.</param>
        /// <returns><see langword="true"/> if <paramref name="normalizedAddress"/> has valid <c>local@domain</c> syntax; otherwise <see langword="false"/>.</returns>
        private static bool TrySplitAddress(string normalizedAddress, out string localPart, out string domainPart)
        {
            localPart = string.Empty;
            domainPart = string.Empty;

            int atIndex = normalizedAddress.IndexOf('@');
            if (atIndex <= 0 || atIndex != normalizedAddress.LastIndexOf('@'))
                return false;

            localPart = normalizedAddress.Substring(0, atIndex);
            domainPart = normalizedAddress.Substring(atIndex + 1);

            if (localPart.Contains('*') || string.IsNullOrWhiteSpace(localPart))
                return false;

            return IsValidDomain(domainPart);
        }

        #endregion

        #region static method IsValidDomain

        /// <summary>
        /// Performs basic structural validation on a DNS domain label sequence.
        /// </summary>
        /// <param name="domain">The candidate domain string to validate.</param>
        /// <returns><see langword="true"/> if <paramref name="domain"/> is structurally valid; otherwise <see langword="false"/>.</returns>
        private static bool IsValidDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253)
                return false;
            if (domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains(".."))
                return false;
            if (domain.Any(ch => char.IsWhiteSpace(ch) || ch == '@' || ch == '*'))
                return false;

            return domain.Contains('.');
        }

        #endregion
        
    }

}
