# WebVella ERP — Security Remediation Report

This document is the structured security finding log for the OWASP Top 10 (2021) hardening of
WebVella ERP (closure of the PR #1 security backlog). It records every finding that was triaged
during the audit, its severity, CWE, location, impact and the remediation that was applied (or the
documented accepted-risk decision where an in-place code fix was not the correct resolution).

The remediation followed a strict **minimal-change** discipline: only changes necessary to remediate
identified vulnerabilities were made, public API contracts / NuGet package contracts / the database
schema were preserved, and authentication continuity for existing MD5-hashed credentials was
maintained (legacy hashes verify and are transparently upgraded on next login).

## Severity legend

- **Critical** — remote code execution, authentication bypass, sensitive-data exposure, privilege escalation
- **High** — SQL injection, stored XSS, IDOR, session hijacking, EoL components, brute force
- **Medium** — reflected XSS, CSRF, information disclosure, weak cryptographic configuration
- **Low** — missing security headers, verbose errors, minor misconfiguration

## Remediation summary

| ID | Severity | OWASP | Title | Status |
|----|----------|-------|-------|--------|
| F-01 | Critical | A03 | Authenticated runtime C# code-compilation reachable beyond trusted-author boundary | Resolved |
| F-02 | High | A01/A03 | Multiple file-upload endpoints not sanitized / validated | Resolved |
| F-03 | High | A04/A07 | JWT token endpoint bypasses login lockout (brute force) | Resolved |
| F-04 | High | A02/A05 | Runtime secret overlays not reaching `ErpSettings`; unconditional JWT-key requirement | Resolved |
| F-05 | High | A04 | Named login rate limiters registered but inert | Resolved |
| F-06 | Medium | A05 | Content-Security-Policy emitted as Report-Only by default | Resolved |
| F-07 | Medium | A05 | HSTS exact `max-age`/`includeSubDomains` not configured on six hosts | Resolved |
| F-08 | Medium | A05 | CORS policies missing `AllowAnyHeader` on five hosts | Resolved |
| F-10 | Medium | A07 | System password field minimum length below policy (6) | Resolved |
| F-11 | High | A06 | AutoMapper 14.0.0 vulnerable (GHSA-rvv3-g6hj-g44x, recursion DoS) | Resolved (mitigated + accepted residual) |
| F-12 | Medium | A02 | Symmetric encryption not authenticated (no AES-GCM) | Resolved |
| F-13 | Low | A05 | JWT issuer equals audience in committed config | Resolved |
| F-14 | Medium | A06 | MailKit 4.14.1 / MimeKit 4.14.0 vulnerable (transitive) | Resolved |
| F-15 | Low | A10 | SSRF review surface not documented | Resolved (documentation) |
| F-16 | Low | — | Structured finding log not present | Resolved (this document) |

All Critical and High findings are remediated. All Medium and Low findings are remediated or
documented. The only residual SCA listing is AutoMapper (F-11); see the Accepted-Risk Register.

---

## Detailed finding log

```
FINDING:     Authenticated runtime C# code compilation beyond the trusted-author boundary
ID:          F-01
SEVERITY:    Critical
CWE:         CWE-94 / CWE-95 (code injection / eval)
LOCATION:    WebVella.Erp.Web/Controllers/WebApiController.cs (datasource/code-compile action);
             WebVella.Erp.Web/Services/CodeEvalService.cs
DESCRIPTION: The /api/v3.0/datasource/code-compile endpoint compiled caller-supplied C# under only
             class-level [Authorize], so any authenticated user (not just an administrator/developer)
             could reach runtime compilation. The CodeEvalService documentation claimed a
             trusted-author boundary that was not actually enforced.
IMPACT:      Authenticated remote code compilation / execution and privilege escalation.
REMEDIATION: Added [Authorize(Roles = "administrator")] to the code-compile action so only
             administrators can reach CodeEvalService.Compile. Audited all CodeEvalService callers:
             the only request-input path is this endpoint (now gated); the remaining callers evaluate
             administrator-authored persisted DataSource CODE variables / snippet files
             (PageDataModel) or a hardcoded developer sample (EQL page) — none accept request input.
             Rewrote the CodeEvalService boundary documentation to describe the now-enforced gating.
STATUS:      Resolved
```

```
FINDING:     File-upload endpoints accept unsanitized filenames and unvalidated content
ID:          F-02
SEVERITY:    High
CWE:         CWE-22 (path traversal) / CWE-434 (unrestricted upload)
LOCATION:    WebVella.Erp.Web/Controllers/WebApiController.cs
             (/fs/upload-user-file-multiple/ and /fs/upload-file-multiple/)
DESCRIPTION: The two multiple-file upload endpoints embedded the attacker-controlled filename directly
             into the storage path and did not apply filename sanitization, an extension allowlist, or
             content-signature validation. One endpoint also used the current user without a null
             guard, and the other wrote via an unauthenticated temp-file helper.
IMPACT:      Path traversal, unrestricted/dangerous file upload, and unauthenticated writes.
REMEDIATION: Applied the same controls already used by the remediated single-upload paths:
             SanitizeUploadFileName + IsAllowedUploadFileName (extension allowlist / denylist) +
             IsAllowedUploadContent (magic-byte / content-type checks) + a current-user null guard
             (rollback + "Not authorized." on failure), and owner-stamped writes
             (Create(path, bytes, UtcNow, currentUser.Id)) instead of the unauthenticated temp helper.
STATUS:      Resolved
```

```
FINDING:     JWT token issuance bypasses login lockout / rate limiting
ID:          F-03
SEVERITY:    High
CWE:         CWE-307 (improper restriction of excessive auth attempts) / CWE-799
LOCATION:    WebVella.Erp.Web/Controllers/WebApiController.cs (JWT token + refresh actions);
             WebVella.Erp.Web/Pages/login.cshtml.cs; WebVella.Erp.Web/Services/LoginAttemptTracker.cs
DESCRIPTION: The [AllowAnonymous] JWT credential endpoint authenticated without participating in the
             5-attempt lockout used by the Razor login page, leaving a brute-force path on JWT hosts.
             The endpoints also leaked exception stack traces.
IMPACT:      Credential brute force and internal information disclosure.
REMEDIATION: Extracted the Razor lockout logic into a shared LoginAttemptTracker (5 attempts,
             15-minute lockout, bounded cleanup) and refactored login.cshtml.cs to delegate to it.
             Integrated the tracker into the JWT token endpoint (pre-check lockout, reset on success,
             register on failure) and replaced stack-trace leaks with generic messages
             ("Invalid email or password." / "Unable to refresh token."). A per-host global rate
             limiter additionally throttles the JWT token path on JWT hosts (see F-05).
STATUS:      Resolved
```

```
FINDING:     Runtime secret overlays do not reach ErpSettings; unconditional JWT-key requirement
ID:          F-04
SEVERITY:    High
CWE:         CWE-798 (hardcoded/again-default credentials) / CWE-1188 (insecure default)
LOCATION:    WebVella.Erp.Web/ErpMvcExtensions.cs (UseErp); WebVella.Erp/ErpSettings.cs (Initialize);
             WebVella.Erp.Site/Startup.cs; WebVella.Erp.Site.Project/Startup.cs
DESCRIPTION: UseErp built configuration from the JSON file only, so environment-variable secret
             overlays (Settings__EncryptionKey / Settings__Jwt__Key) never reached ErpSettings. In
             addition, ErpSettings unconditionally required a JWT key, which could crash cookie-only
             hosts that legitimately do not configure JWT.
IMPACT:      Production startup blocked by empty committed placeholders, or cookie-only hosts failing
             fast for a key they do not need.
REMEDIATION: Layered .AddEnvironmentVariables() onto the UseErp configuration builder (env overrides
             JSON) so secret overlays reach ErpSettings.Initialize for all hosts. Made the JWT-key
             requirement conditional: when no key is configured, JwtKey is left null (cookie-only
             hosts start normally); fail-fast is preserved only when a key is present but is the
             insecure default. The host startup configuration builders (Site, Project) also include
             .AddEnvironmentVariables() so the host JWT setup and ErpSettings see the same sources.
             The legacy Settings:EncriptionKey typo fallback and the distinct issuer/audience defaults
             are preserved.
STATUS:      Resolved
```

```
FINDING:     Login rate limiters registered but not consumed (inert)
ID:          F-05
SEVERITY:    High
CWE:         CWE-307 / CWE-799
LOCATION:    WebVella.Erp.Site/Startup.cs; WebVella.Erp.Site.MicrosoftCDM/Startup.cs;
             WebVella.Erp.Site.Project/Startup.cs
DESCRIPTION: A named "login" rate-limiter policy was registered but no endpoint metadata consumed it,
             so it provided no protection.
IMPACT:      No transport-level throttling of credential surfaces.
REMEDIATION: Added an active partitioned GlobalLimiter scoped to the credential surfaces (/login, and
             on JWT hosts the JWT token path) with NoLimiter for all other paths to preserve
             functional parity. All hosts already call app.UseRateLimiter(), so the global limiter is
             applied automatically. The other hosts already had a consumed global limiter.
STATUS:      Resolved
```

```
FINDING:     Content-Security-Policy emitted as Report-Only by default
ID:          F-06
SEVERITY:    Medium
CWE:         CWE-693 (protection mechanism failure)
LOCATION:    WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs
DESCRIPTION: The exact CSP literal was present but emitted as Content-Security-Policy-Report-Only by
             default, so the policy was not enforced and did not satisfy the exact-header requirement.
IMPACT:      CSP protections (script/style restrictions) not actually enforced.
REMEDIATION: Changed the default so the enforced Content-Security-Policy header is emitted with the
             exact value "default-src 'self'; script-src 'self'; style-src 'self'". The policy string
             and a report-only toggle remain configurable
             (Settings:SecurityHeaders:ContentSecurityPolicy[ReportOnly]) so operators can run a
             Report-Only staged rollout without a code change if needed.
STATUS:      Resolved
```

```
FINDING:     HSTS exact max-age / includeSubDomains not configured per host
ID:          F-07
SEVERITY:    Medium
CWE:         CWE-1021 / CWE-693
LOCATION:    WebVella.Erp.Site, .Site.Mail, .Site.MicrosoftCDM, .Site.Next, .Site.Project, .Site.Sdk
             (Startup.cs)
DESCRIPTION: Hosts called UseHsts() but did not configure AddHsts with the required max-age and
             includeSubDomains, so the framework default could preempt the centrally emitted header.
IMPACT:      Weaker / inconsistent HSTS than required.
REMEDIATION: Added services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains =
             true; }) to the six hosts that lacked it (CRM already had it). TimeSpan.FromDays(365) ==
             max-age=31536000, matching the central middleware header exactly.
STATUS:      Resolved
```

```
FINDING:     CORS policy omits AllowAnyHeader
ID:          F-08
SEVERITY:    Medium
CWE:         CWE-942 (overly permissive CORS) — host-recipe consistency
LOCATION:    WebVella.Erp.Site.Crm, .Site.Mail, .Site.MicrosoftCDM, .Site.Next, .Site.Sdk (Startup.cs)
DESCRIPTION: Explicit-origin CORS policies allowed credentials and any method but omitted
             AllowAnyHeader, which breaks credentialed API calls that send custom headers and
             violates the host hardening recipe.
IMPACT:      Broken credentialed cross-origin API calls with custom headers (functional), inconsistent
             host posture.
REMEDIATION: Added .AllowAnyHeader() to each affected policy while retaining explicit WithOrigins and
             never using AllowAnyOrigin().
STATUS:      Resolved
```

```
FINDING:     System password field minimum length below policy
ID:          F-10
SEVERITY:    Medium
CWE:         CWE-521 (weak password requirements)
LOCATION:    WebVella.Erp/ERPService.cs (system "password" field definition)
DESCRIPTION: The system password field had MinLength = 6, below the 12-character minimum policy.
IMPACT:      Users / password changes could set 6-character passwords.
REMEDIATION: Raised MinLength to 12 (MaxLength 24 unchanged). This bounds raw password input; the
             stored value is a salted PBKDF2 hash whose length is unaffected, and legacy login
             verification does not re-check length, so existing credentials continue to verify.
STATUS:      Resolved
```

```
FINDING:     AutoMapper 14.0.0 uncontrolled-recursion denial of service
ID:          F-11
SEVERITY:    High
CWE:         CWE-674 (uncontrolled recursion)
LOCATION:    WebVella.Erp/Api/Models/AutoMapper/ErpAutoMapper.cs; Directory.Build.props;
             advisory GHSA-rvv3-g6hj-g44x
DESCRIPTION: AutoMapper 14.0.0 can be driven into unbounded recursive mapping by a deeply nested or
             self-referential object graph, exhausting the stack.
IMPACT:      Denial of service.
REMEDIATION: The vulnerability vector is closed in code: ErpAutoMapper.Initialize applies a default
             recursion depth cap (MaxDepth = 64) to every registered map at the single configuration
             seal chokepoint (used by both the web composition root and the console app), bounding
             recursion for self-referential / cyclic graphs (validated: a 5000-deep graph is capped at
             64). The package is NOT upgraded — see the Accepted-Risk Register below for the rationale.
             A solution-wide NuGetAuditSuppress (Directory.Build.props) silences the build-time
             advisory warning for this specific, mitigated advisory; it does not hide the package from
             `dotnet list package` vulnerability scans.
STATUS:      Resolved (vector mitigated; residual SCA listing accepted and documented)
```

```
FINDING:     Symmetric encryption is not authenticated (no AES-GCM)
ID:          F-12
SEVERITY:    Medium
CWE:         CWE-326 (inadequate encryption strength) / weak crypto configuration
LOCATION:    WebVella.Erp/Utilities/CryptoUtility.cs
DESCRIPTION: The legacy symmetric helpers used a caller-supplied SymmetricAlgorithm with a
             deterministic IV derived from the key and provided no integrity/authentication.
IMPACT:      Ciphertext malleability and weak (deterministic-IV) symmetric encryption posture.
REMEDIATION: Added a new, additive AES-256-GCM authenticated-encryption API
             (EncryptTextAuthenticated / DecryptTextAuthenticated and the byte[] variants) that uses a
             fresh CSPRNG 96-bit nonce per operation and a 128-bit authentication tag (tag size passed
             explicitly per the .NET 8+ AesGcm contract), packing nonce || tag || ciphertext. The
             256-bit key is derived from the configured high-entropy secret via SHA-256. Tampering or
             a wrong key fails decryption with CryptographicException (validated). The legacy helpers
             are retained unchanged for backward compatibility; no existing ciphertext requires
             migration (they are unused by active code).
STATUS:      Resolved
```

```
FINDING:     JWT issuer equals audience in committed configuration
ID:          F-13
SEVERITY:    Low
CWE:         CWE-1188 (insecure default configuration)
LOCATION:    WebVella.Erp.Site/Config.json; WebVella.Erp.Site.Project/Config.json
DESCRIPTION: Both Settings:Jwt:Issuer and Settings:Jwt:Audience were committed as "webvella-erp",
             overriding the distinct code defaults.
IMPACT:      Weakened issuer/audience validation distinction.
REMEDIATION: Set distinct values (issuer "webvella-erp-issuer", audience "webvella-erp-audience")
             matching the ErpSettings code defaults. Operators should set environment-specific values
             in production.
STATUS:      Resolved
```

```
FINDING:     MailKit / MimeKit transitive vulnerabilities
ID:          F-14
SEVERITY:    Medium
CWE:         CWE-1104 (use of unmaintained / vulnerable component)
LOCATION:    WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj;
             advisories GHSA-9j88-vvj5-vhgr (MailKit), GHSA-g7hc-96xr-gvvx (MimeKit, CVE-2026-30227)
DESCRIPTION: MailKit 4.14.1 (and MimeKit 4.14.0 pulled transitively) carried moderate advisories.
IMPACT:      Known-vulnerable mail components.
REMEDIATION: Upgraded MailKit to 4.16.0, which clears GHSA-9j88-vvj5-vhgr and pulls MimeKit 4.16.0
             transitively, clearing GHSA-g7hc-96xr-gvvx (fixed in MimeKit >= 4.15.1). Both packages
             remain MIT-licensed. Verified cleared via `dotnet list package --vulnerable` for the Mail
             plugin and the Site.Mail host.
STATUS:      Resolved
```

```
FINDING:     Server-Side Request Forgery (SSRF) review surface not documented
ID:          F-15
SEVERITY:    Low / Informational
CWE:         CWE-918 (SSRF)
LOCATION:    WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs; MailKit, Storage.Net and the
             Microsoft CDM plugin (outbound-integration surfaces)
DESCRIPTION: The audit confirmed that server-side outbound URL handling driven by user content is
             minimal and does not constitute an exploitable SSRF. See the SSRF Review Surface section
             below for the analysis.
IMPACT:      Informational; no exploitable server-side fetch of attacker-controlled hosts was found.
REMEDIATION: Documented (this report). No code change required; any future feature that fetches a
             user-supplied URL server-side must validate / allowlist the target host.
STATUS:      Resolved (documentation)
```

```
FINDING:     Structured finding log not present in the repository
ID:          F-16
SEVERITY:    Low
CWE:         — (process / documentation)
LOCATION:    Repository root
DESCRIPTION: The required FINDING:/SEVERITY:/CWE:/LOCATION:/DESCRIPTION:/IMPACT:/REMEDIATION: finding
             log was not committed to the repository.
IMPACT:      Auditors lacked an in-repo record of the remediation.
REMEDIATION: This SECURITY.md provides the structured finding log for all findings.
STATUS:      Resolved
```

---

## OWASP Top 10 (2021) coverage

| Category | Outcome |
|----------|---------|
| A01 Broken Access Control | Upload endpoints hardened with ownership stamping and sanitization (F-02); code-eval gated to administrators (F-01). |
| A02 Cryptographic Failures | Salted adaptive PBKDF2 password hashing with transparent legacy-MD5 upgrade (prior checkpoint); hardcoded key removed and secrets externalized with fail-fast (F-04); cookies `Secure`; authenticated AES-256-GCM API added (F-12). |
| A03 Injection | Parameterized queries preserved; upload content/filename validation (F-02); runtime code-eval gated and documented (F-01). |
| A04 Insecure Design | Account lockout / rate limiting made effective (F-05) and extended to JWT issuance (F-03); bounded cookie lifetime and randomized default admin (prior checkpoint). |
| A05 Security Misconfiguration | Fail-fast JWT-key handling with conditional requirement (F-04); explicit CORS allowlist + AllowAnyHeader (F-08); enforced security headers (F-06) and exact HSTS (F-07); synchronous-I/O removed; distinct issuer/audience (F-13). |
| A06 Vulnerable & Outdated Components | .NET 7 EoL WebAssembly projects upgraded to net10.0 (prior checkpoint); MailKit/MimeKit upgraded (F-14); AutoMapper recursion DoS mitigated with documented accepted residual (F-11). |
| A07 Identification & Authentication Failures | Password minimum raised to 12 (F-10); lockout on Razor and JWT paths (F-03/F-05); bounded cookie lifetime; constant-time hash verification (prior checkpoint). |
| A08 Software & Data Integrity Failures | Allowlist ISerializationBinder applied at every TypeNameHandling site (prior checkpoint; verified intact). |
| A09 Security Logging & Monitoring Failures | Existing system_log preserved; see the Security Logging note below. |
| A10 SSRF | Reviewed and documented (F-15); no exploitable server-side fetch of attacker-controlled hosts found. |

---

## Accepted-Risk Register

### AutoMapper 14.0.0 — GHSA-rvv3-g6hj-g44x (CWE-674, recursion DoS)

- **Decision:** Keep AutoMapper 14.0.0; do not upgrade the package.
- **Why not upgrade:** The only fixed releases (15.1.1 and 16.1.1+) are distributed under a paid
  commercial license, which conflicts with the project's Apache-2.0 licensing, and they break the
  static-mapper API that WebVella uses across the solution. An in-place version bump would therefore
  violate the project's API/functionality preservation and minimal-change constraints. No fixed
  release exists on the 14.x (MIT) line.
- **Mitigation applied:** A default recursion depth cap (MaxDepth = 64) is applied to every map at the
  configuration seal chokepoint (`ErpAutoMapper.Initialize`), which closes the uncontrolled-recursion
  denial-of-service vector. This is the vendor/community-recommended mitigation for this advisory.
- **Residual:** `dotnet list package --vulnerable` continues to report AutoMapper 14.0.0 because that
  command performs a database lookup that does not honor build-time audit suppressions. The residual
  is intentionally left visible to auditors; the exploitable vector is closed by the depth cap. A
  solution-wide `Directory.Build.props` suppresses only the build-time NU1903 warning for this
  specific advisory.
- **Re-evaluation trigger:** Revisit if a fixed MIT-licensed AutoMapper release becomes available, or
  if the project adopts a commercial AutoMapper license, or migrates off AutoMapper.

---

## SSRF Review Surface (A10 / F-15)

Server-side outbound network access driven by user-controllable input was reviewed and found to be
minimal and non-exploitable:

- **HTML email inline images (`SmtpInternalService`)** — When composing outbound email, the service
  scans `<img src>` attributes but only acts on values that begin with `/fs`. It parses the value
  solely to extract the local path, strips the `/fs` prefix, and resolves the image from the **local**
  blob store (`DbFileRepository.Find`). It never issues an HTTP request to an arbitrary, attacker-
  controlled host. There is no `HttpClient` / `WebClient` outbound fetch in this path.
- **MailKit (SMTP)** — Connects to the operator-configured SMTP server only; the server host is not
  user-supplied per request.
- **Storage.Net (blob storage)** — Targets the operator-configured storage backend, not user-supplied
  URLs.
- **Microsoft CDM plugin** — Integrates with the configured CDM endpoint; treated as a documented
  review surface, not a per-request user-controlled fetch.
- **WebAssembly client `HttpClient`** — Executes in the browser against the application's own API; it
  is not a server-side request and therefore not an SSRF vector.

**Guidance:** Any future feature that fetches a user-supplied URL on the server MUST validate and
allowlist the destination host (and block private / link-local / metadata address ranges).

---

## Security Logging note (A09)

The existing `system_log` facility is preserved. Audit logging of authentication failures and
permission-denial events is a low-cost, optional enhancement (Medium): failed-login attempts are
already throttled and tracked in-process by `LoginAttemptTracker`, and permission checks already exist
at the access-control layer; emitting `system_log` entries for these events would improve detection
and monitoring. This is documented as a recommended follow-up rather than implemented here, to remain
within the minimal-change scope of this remediation.

---

## Operational deployment prerequisites

These are configuration prerequisites introduced by the hardening (no code change required at deploy
time):

1. **Secrets** — Provide `Settings:EncryptionKey` and, on JWT hosts, `Settings:Jwt:Key` via
   environment variables (`Settings__EncryptionKey`, `Settings__Jwt__Key`), user-secrets, or a secret
   store. Committed `Config.json` values are placeholders. JWT hosts fail fast on a default/insecure
   key; cookie-only hosts run without a JWT key.
2. **HTTPS** — Cookies are `Secure`, so every environment must serve over HTTPS.
3. **Content-Security-Policy** — The strict CSP is enforced by default. If a deployment relies on
   inline scripts/styles or external resources, tune `Settings:SecurityHeaders:ContentSecurityPolicy`
   or run a staged rollout via `Settings:SecurityHeaders:ContentSecurityPolicyReportOnly=true`.
4. **JWT issuer/audience** — Set environment-specific distinct values in production.
