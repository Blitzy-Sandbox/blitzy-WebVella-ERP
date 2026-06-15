<!--{"sort_order":1, "name": "owasp-top10-audit", "label": "OWASP Top 10 Security Audit"}-->
# WebVella ERP — OWASP Top 10 (2021) Security Audit & Remediation Log

This document is the security finding log for the OWASP Top 10 (2021) hardening of the WebVella
ERP codebase. It records every vulnerability class that was audited, its severity, CWE, location,
the remediation applied, and the controls that were already correct and preserved. The audit was a
behavior-preserving **security hardening** pass: only changes necessary to remediate identified
vulnerabilities were made, with no feature additions and no refactoring beyond security.

## Severity Classification

| Severity | Definition |
|----------|------------|
| Critical | Remote code execution, authentication bypass, sensitive-data exposure, privilege escalation |
| High     | SQL injection, stored XSS, insecure direct object reference (IDOR), session hijacking |
| Medium   | Reflected XSS, CSRF, information disclosure, weak cryptographic configuration |
| Low      | Missing security headers, verbose errors, minor misconfiguration |

**Completion bar:** zero Critical and zero High findings remaining; all Medium findings documented
and addressed where low-cost; the dependency (SCA) scan passing with zero High advisories; all
existing functionality preserved.

## Result Summary

| Class | Count | Status |
|-------|-------|--------|
| Critical | 4 classes (unsalted MD5, hardcoded symmetric key, default JWT key, insecure deserialization) | Remediated |
| High | 5 (file path traversal/upload, no lockout/rate limiting, 100-year cookie, default admin, .NET 7 EOL) | Remediated |
| Medium | 6 (cookie Secure flag, upload content-type, CORS, security headers, sync I/O DoS, weak crypto config) | Remediated / documented |
| Low / Info | 2 (JWT issuer==audience, SSRF review surface) | Documented / addressed |

---

## A01 — Broken Access Control

```
FINDING:     File path traversal / unrestricted upload on file endpoints
SEVERITY:    High
CWE:         CWE-22 (Path Traversal) / CWE-434 (Unrestricted Upload) / CWE-639 (IDOR)
LOCATION:    WebVella.Erp.Web/Controllers/WebApiController.cs (single-file and multi-file upload endpoints)
DESCRIPTION: Upload endpoints accepted client-supplied filenames and content without canonicalization,
             name/extension allowlisting, or active-content rejection. The multi-file endpoints
             (UploadUserFileMultiple, UploadFileMultiple) bypassed the controls applied to the
             single-file paths.
IMPACT:      A crafted filename could traverse directories or persist active/executable content.
REMEDIATION: All upload paths (single-file and both multi-file endpoints) now route the filename
             through SanitizeUploadFileName + IsAllowedUploadFileName + IsAllowedUploadContent before
             persistence. Invalid files fail closed (the multi-file endpoints throw, which rolls back
             the surrounding transaction so nothing unsafe is stored). Endpoint contracts are unchanged.
```

## A02 — Cryptographic Failures

```
FINDING:     Unsalted MD5 password hashing
SEVERITY:    Critical
CWE:         CWE-327 (Broken Crypto) / CWE-916 (Weak Password Hash)
LOCATION:    WebVella.Erp/Utilities/PasswordUtil.cs; WebVella.Erp/Api/SecurityManager.cs;
             WebVella.Erp/Api/RecordManager.cs; WebVella.Erp/Database/DbRecordRepository.cs
DESCRIPTION: Passwords were hashed with unsalted MD5 and matched inside the SQL WHERE clause.
IMPACT:      Offline brute-force / rainbow-table recovery of credentials on database disclosure.
REMEDIATION: Introduced IPasswordHasher / ErpPasswordHasher using salted PBKDF2-HMAC-SHA256 (600,000
             iterations, CSPRNG per-password salt, 32-byte derived key, fixed-time comparison, versioned
             self-describing hash). Every password-field storage path (RecordManager and both
             DbRecordRepository sites) now hashes via ErpPasswordHasher. Legacy MD5 hashes still verify
             and are transparently re-hashed on the next successful login (needsUpgrade), preserving
             authentication continuity. The MD5 helpers are retained verify-only for that migration.
```

```
FINDING:     Hardcoded symmetric encryption key committed to source/config
SEVERITY:    Critical
CWE:         CWE-321 (Hardcoded Crypto Key) / CWE-798 (Hardcoded Credentials)
LOCATION:    WebVella.Erp/Utilities/CryptoUtility.cs; WebVella.Erp.Site*/Config.json; ConsoleApp/Config.json
DESCRIPTION: A default symmetric key literal was hardcoded; encryption/JWT key material was committed.
IMPACT:      Disclosure of the key enables decryption of protected data / token forgery.
REMEDIATION: Removed the hardcoded defaultCryptKey literal. CryptoUtility.CryptKey now requires
             Settings:EncryptionKey from configuration and throws if absent. Config.json secret values
             (EncryptionKey, Jwt:Key, and DB connection-string credentials) are externalized to
             environment variables / a secret store while preserving identical configuration keys.
             NOTE: the legacy Settings:EncriptionKey typo fallback is intentionally preserved as a
             compatibility shim. The symmetric EncryptText/DecryptText/EncryptData/DecryptData helpers
             have no live callers; a documented minimal-change exception explains why AES-256-GCM is not
             retrofitted onto that dead code (it would be an unused feature addition); if reactivated they
             must migrate to AES-256-GCM with random nonces and a backward-compatible decrypt path.
```

```
FINDING:     Authentication cookie missing the Secure flag
SEVERITY:    Medium
CWE:         CWE-614 (Sensitive Cookie Without Secure Flag)
LOCATION:    WebVella.Erp.Site*/Startup.cs cookie configuration
REMEDIATION: Cookies set HttpOnly + SecurePolicy.Always + SameSite across all seven hosts.
```

## A03 — Injection

```
FINDING:     Upload filename / content-type not validated
SEVERITY:    High
CWE:         CWE-22 / CWE-434
LOCATION:    WebVella.Erp.Web/Controllers/WebApiController.cs
REMEDIATION: See A01 — content-type/extension allowlisting and filename sanitization on all upload paths.
```

```
FINDING:     Unsandboxed runtime C# evaluation (trusted-author boundary)
SEVERITY:    Medium (documented, accepted)
CWE:         CWE-94 (Code Injection) / CWE-95 (Eval Injection)
LOCATION:    WebVella.Erp.Web/Services/CodeEvalService.cs
DESCRIPTION: CSScriptLib compiles/executes C# snippets at runtime.
IMPACT:      Code execution if untrusted input reached the evaluator.
REMEDIATION: This is a DELIBERATELY TRUSTED-AUTHOR boundary: the evaluated source originates exclusively
             from authenticated administrators/developers authoring server-side snippets and page logic,
             never from end-user/request input. The boundary is documented in code (CodeEvalService.cs).
             No code change beyond documentation; any future change that would accept untrusted input here
             MUST be escalated and sandboxed first.
```

SQL injection is mitigated by the existing parameterized NpgsqlCommand / EqlParameter queries, which
were preserved. The credential lookup was additionally changed from a regex predicate to an exact,
parameterized, case-normalized `lower(email) = lower(@email)` match (see A04/A07).

## A04 — Insecure Design

```
FINDING:     No account lockout / rate limiting on authentication
SEVERITY:    High
CWE:         CWE-307 (Improper Restriction of Excessive Auth Attempts) / CWE-799
LOCATION:    WebVella.Erp.Web/Pages/login.cshtml.cs; WebVella.Erp.Site*/Startup.cs
REMEDIATION: Per-account lockout after 5 failed attempts (generic messages) at the login hook, paired
             with the ASP.NET Core rate limiter. Every host enforces login throttling: hosts with a
             GlobalLimiter throttle POST /login at 5 requests/minute per client IP; others apply a global
             per-IP limiter. NOTE: the in-process lockout is per-instance; for multi-instance deployments
             back it with distributed storage.
```

```
FINDING:     100-year authentication cookie lifetime
SEVERITY:    High
CWE:         CWE-613 (Insufficient Session Expiration)
LOCATION:    WebVella.Erp.Web/Services/AuthService.cs
REMEDIATION: Cookie ExpiresUtc bounded to a 480-minute operational lifetime; IsPersistent and the JWT
             path preserved.
```

```
FINDING:     Built-in default administrator credential (erp@webvella.com / erp)
SEVERITY:    High
CWE:         CWE-1392 (Default Credentials) / CWE-521 (Weak Password Requirements)
LOCATION:    WebVella.Erp/ERPService.cs
REMEDIATION: The default admin seed password is randomized via a CSPRNG (force-reset), keeping the email
             identity. The system user.password field minimum length was raised from 6 to 12 characters
             to satisfy the authentication-hardening standard.
```

## A05 — Security Misconfiguration

```
FINDING:     Default JWT signing key shipped / empty key not operationalized
SEVERITY:    Critical
CWE:         CWE-1188 (Insecure Default Initialization) / CWE-798
LOCATION:    WebVella.Erp/ErpSettings.cs; WebVella.Erp.Web/ErpMvcExtensions.cs;
             WebVella.Erp.Site*/Config.json; WebVella.Erp.Site/Startup.cs
DESCRIPTION: A default JWT key was shipped, and empty-key configuration could not be supplied at runtime
             because environment variables were not loaded.
REMEDIATION: ErpSettings fail-fast now throws when Settings:Jwt:Key is empty or a known default — but ONLY
             for hosts that actually configure a Settings:Jwt section (JWT hosts). Cookie-only hosts and
             the console app, which have no Jwt section, are not forced to require JWT. Environment-variable
             loading (.AddEnvironmentVariables()) was added to the central composition root
             (ErpMvcExtensions.UseErp), the default host Startup, and the console Program so that
             Settings__Jwt__Key and other secrets operationalize at runtime.
```

```
FINDING:     Permissive CORS (AllowAnyOrigin) / missing AllowAnyHeader on explicit-origin policies
SEVERITY:    Medium
CWE:         CWE-942 (Overly Permissive CORS)
LOCATION:    WebVella.Erp.Site*/Startup.cs
REMEDIATION: CORS is an explicit per-host WithOrigins allowlist (no AllowAnyOrigin). Explicit-origin
             policies include AllowAnyHeader + AllowAnyMethod + AllowCredentials, preserving cookie/origin
             names.
```

```
FINDING:     Missing security response headers
SEVERITY:    Medium
CWE:         CWE-693 (Protection Mechanism Failure) / CWE-1021 (Improper UI Restriction)
LOCATION:    WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs (registered in UseErp for all hosts)
REMEDIATION: A central SecurityHeadersMiddleware emits, on every response:
               Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'
               Strict-Transport-Security: max-age=31536000; includeSubDomains   (force-set)
               X-Content-Type-Options: nosniff
               X-Frame-Options: DENY
               X-XSS-Protection: 0
               Referrer-Policy: strict-origin-when-cross-origin
               Permissions-Policy: geolocation=(), microphone=(), camera=()
             CSP Report-Only mode is the DEFAULT (functional parity): the response carries
             Content-Security-Policy-Report-Only with the exact policy above, so violations are reported
             (not enforced) and existing inline Razor/Stencil scripts/styles and vendored client libraries
             keep working on first deployment. Operators tighten to enforce mode (header
             Content-Security-Policy, same value) by setting the Settings:SecurityHeaders:ContentSecurityPolicyReportOnly
             toggle to false once any inline scripts/styles have been tuned — no code change required. All
             hosts also call services.AddHsts with MaxAge = 365 days and IncludeSubDomains so the framework
             default does not override the exact value.
```

```
FINDING:     AllowSynchronousIO = true (denial-of-service)
SEVERITY:    Medium
CWE:         CWE-400 (Uncontrolled Resource Consumption)
LOCATION:    WebVella.Erp.Web/Middleware/ErpMiddleware.cs
REMEDIATION: Removed AllowSynchronousIO; the pipeline relies on asynchronous I/O. No occurrences remain.
```

```
FINDING:     JWT issuer equal to audience
SEVERITY:    Low
CWE:         CWE-1188
LOCATION:    WebVella.Erp/ErpSettings.cs
REMEDIATION: Distinct default issuer/audience values; deployments should set environment-specific values.
```

## A06 — Vulnerable & Outdated Components

```
FINDING:     .NET 7 (End-of-Support) runtime on WebAssembly Server/Shared projects
SEVERITY:    High
CWE:         CWE-1104 (Use of Unmaintained Third-Party Components)
LOCATION:    WebVella.Erp.WebAssembly/Server/*.csproj; WebVella.Erp.WebAssembly/Shared/*.csproj
REMEDIATION: TargetFramework upgraded net7.0 -> net10.0 (matching the rest of the solution);
             Microsoft.AspNetCore.Components.WebAssembly.Server resolves at 10.0.x.
```

```
FINDING:     Vulnerable NuGet packages flagged by SCA
SEVERITY:    High (AutoMapper) / Moderate (MailKit, MimeKit)
CWE:         CWE-1395 (Dependency on Vulnerable Component)
LOCATION:    WebVella.Erp/WebVella.Erp.csproj; WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj
DESCRIPTION: AutoMapper 14.0.0 (GHSA-rvv3-g6hj-g44x, High); MailKit 4.14.1 (GHSA-9j88-vvj5-vhgr, Moderate);
             transitive MimeKit 4.14.0 (GHSA-g7hc-96xr-gvvx, Moderate).
REMEDIATION: AutoMapper upgraded 14.0.0 -> 16.1.1 (the single mechanical breaking change — the
             MapperConfiguration constructor now requires an ILoggerFactory — was addressed by passing
             NullLoggerFactory.Instance, preserving behavior). MailKit upgraded 4.14.1 -> 4.16.0; transitive
             MimeKit resolves to 4.16.0. `dotnet list package --vulnerable` reports no vulnerable packages.
```

## A07 — Identification & Authentication Failures

Composite of the A02 and A04 remediations: salted PBKDF2 password storage with transparent legacy
migration, bounded (480-minute) cookie lifetime, randomized default admin credential, 12-character
minimum password length, account lockout + per-host login rate limiting, and cookies set
HttpOnly + Secure + SameSite. The credential lookup uses an exact, parameterized, case-normalized
`lower(email) = lower(@email)` query (eliminating the previous regex-expansion DoS) and performs a
constant-cost dummy PBKDF2 verification for non-existent emails to remove the account-enumeration timing
side channel. JWT ValidateIssuer/ValidateAudience/ValidateLifetime/ValidateIssuerSigningKey flags are
preserved.

## A08 — Software & Data Integrity Failures

```
FINDING:     Insecure deserialization via Newtonsoft.Json TypeNameHandling
SEVERITY:    Critical
CWE:         CWE-502 (Deserialization of Untrusted Data)
LOCATION:    WebVella.Erp/Jobs/JobDataService.cs; WebVella.Erp/Notifications/NotificationContext.cs;
             WebVella.Erp/Database/DbEntityRepository.cs; WebVella.Erp/Database/DbRelationRepository.cs
DESCRIPTION: TypeNameHandling was used without a SerializationBinder, exposing the $type gadget RCE vector.
REMEDIATION: A custom allowlist ISerializationBinder (ErpSerializationBinder) is attached at every
             TypeNameHandling site. BindToType allowlists the expected WebVella payload types and delegates
             to DefaultSerializationBinder for known types, while BindToName preserves the on-wire $type
             format (no format change). The same hardening is applied to the AutoMapper JobProfile job-data
             settings and the code-generation service's real serializer settings.
```

## A09 — Security Logging & Monitoring Failures

```
FINDING:     Authentication failures and permission denials are not audit-logged
SEVERITY:    Medium (enhancement)
CWE:         CWE-778 (Insufficient Logging)
LOCATION:    system_log facility (WebVella.Erp/Diagnostics/Log.cs, WebVella.Erp.Web/Services/LogService.cs)
STATUS:      system_log PRESERVED; audit logging documented as an optional, low-cost enhancement.
```

The platform already provides a structured `system_log` facility used throughout the application:
`LogService.Create(LogType type, string source, string message, string details, ...)` persists records
that are reviewable in **SDK Application > Server > Log**. `LogType` includes Error / Warning / Info
categories, and overloads accept an `Exception` and the current `HttpRequest`.

**Recommended optional enhancement (no breaking change):** record security-relevant authentication and
authorization events to `system_log` so they are centrally auditable:

- **Failed login** — at the login hook / `SecurityManager.GetUser(email, password)` failure path, write a
  `LogType.Warning` entry with source `Security:Authentication` containing the attempted email (never the
  password), source IP, and timestamp. The lockout counter increment is the natural call site.
- **Lockout triggered** — write a `LogType.Warning` entry when the 5-attempt threshold is reached.
- **Permission denied** — where `HasEntityPermission` / `[Authorize]` / `AuthorizeFolder` denies access,
  write a `LogType.Warning` entry with source `Security:Authorization`, the user id, and the requested
  resource. Use generic, non-sensitive messages.

These are additive (call the existing `LogService.Create`) and require no schema change. They are
classified Medium and are deferred as an operational enhancement rather than a code change in this pass,
to honor the minimal-change clause; the design above is the implementation guide when they are scheduled.

## A10 — Server-Side Request Forgery (SSRF)

```
FINDING:     Outbound-integration SSRF review surface
SEVERITY:    Low / Informational
CWE:         CWE-918 (Server-Side Request Forgery)
LOCATION:    MailKit SMTP (WebVella.Erp.Plugins.Mail/Api/SmtpService.cs,
             WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs, WebVella.Erp.Web/Services/MailService.cs);
             Storage.Net blob storage; Microsoft CDM plugin.
STATUS:      Documented review surface. Out of scope for code change unless a concrete user-supplied-URL
             sink is confirmed.
```

The application performs outbound network operations through three integration points: SMTP delivery
(MailKit), file/blob storage (Storage.Net), and the Microsoft CDM plugin. None of these currently take a
raw, end-user-supplied URL as a fetch target in the audited paths — SMTP host/port and storage endpoints
are operator-configured. **Guidance:** if any of these integrations is later extended to fetch from a
user-supplied URL, that input MUST be validated against an allowlist of permitted hosts/schemes, must
reject internal/link-local/loopback address ranges, and should resolve-then-pin the destination to defeat
DNS-rebinding. This surface is recorded here as a standing review item.

---

## Controls Already Correct — Preserved (not duplicated)

- Parameterized `NpgsqlCommand` / `EqlParameter` queries (A03 SQLi control).
- Antiforgery on Razor POST handlers.
- `[Authorize]` / `AuthorizeFolder` + `HasEntityPermission` role-based access control.
- `[JsonIgnore]` redaction on sensitive `ErpUser` fields.
- `DevelopmentMode`-gated error masking (ApiControllerBase).
- JWT `ValidateIssuer` / `ValidateAudience` / `ValidateLifetime` / `ValidateIssuerSigningKey` flags.
- The legacy `Settings:EncriptionKey` typo fallback (intentional compatibility shim).
- The `system_log` facility.

## Scope Note — Serialization Binder Coverage (F-MV-001)

`WebVella.Erp/Api/Models/AutoMapper/Profiles/JobProfile.cs` (4 `TypeNameHandling.All` sites) and
`WebVella.Erp.Plugins.SDK/Services/CodeGenService.cs` (2 real serializer-settings sites) attach
`ErpSerializationBinder.Instance`, fulfilling the A08 requirement to allowlist deserialization at every
`TypeNameHandling` site. These are correct, in-scope A08 hardening and are intentionally retained.
The `TypeNameHandling.All` strings inside CodeGenService's generated-code string literals are
serialization-only code-generation templates (not live deserialization sinks) and therefore do not
require a binder; they are noted as a standing review item should the generated code later be executed.
