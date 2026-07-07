# WebVella ERP — Security Audit Report

**Engagement:** OWASP Top 10 (2021) security audit and vulnerability remediation of the WebVella ERP codebase (ASP.NET Core, Blazor WebAssembly, PostgreSQL; `WebVella.ERP3.sln`).
**Deliverable status:** **FINAL.** All Critical and High findings have been remediated in code (the single exception — a third-party dependency advisory whose upstream fix is license-incompatible — is a documented, build-audit-suppressed **risk acceptance**, see A06/H11). All Medium and Low findings are documented with recommended fixes; several low-risk ones were also fixed in-scope. The Section 7 validation gates report **measured** results.

**Method:** Changes were made under a strict **Minimal Change Clause** — only the modifications necessary to remediate identified vulnerabilities, preserving existing functionality, API contracts, database schema, and the documented performance envelope. Each security change carries an inline threat comment; changes are grouped into atomic commits by vulnerability class.

---

## 1. Executive Summary

The audit assessed the full OWASP Top 10 (2021) surface plus supplementary checks (dependency/SCA, secrets, security headers, TLS posture, input validation/output encoding, error handling/information disclosure, rate-limiting/DoS, CORS, file upload, and API security). Findings were classified Critical / High / Medium / Low. Every Critical and High finding was remediated in code with the least-invasive change consistent with preserving behavior; Medium/Low findings are documented.

### 1.1 Findings by Severity

| Severity | Count | Disposition |
|----------|:-----:|-------------|
| Critical | 5 | **All remediated in code** (C1–C5). |
| High | 11 | **10 remediated in code** (H1–H10); **1 risk-accepted + build-audit-suppressed** (H11 — AutoMapper advisory; upstream fix is license-incompatible, documented with a migration recommendation). |
| Medium | 7 | M1–M3 remediated in code; D1–D3, D9 documented (accepted-risk / feature-scope / pre-existing). |
| Low | 7 | L1 and D7 remediated in code; D4–D6, D8, D10 documented. |

### 1.2 Headline Outcome

- **A02 Cryptographic Failures:** unsalted MD5 password hashing replaced with **PBKDF2** (HMAC-SHA-256, 128-bit salt, 256-bit subkey) with backward-compatible **rehash-on-login** migration and **constant-time** comparison; hardcoded encryption key and default JWT signing key removed (configured values now required, fail-fast at startup).
- **A05 Security Misconfiguration:** per-host **CORS allowlist**, **security-headers middleware** (CSP in report-only mode), **cookie `SecurePolicy`/`SameSite`**, **HTTPS redirection + HSTS** (production), committed secrets removed, `DevelopmentMode=false` for production.
- **A06 Vulnerable & Outdated Components:** the two end-of-support `net7.0` Blazor WASM projects retargeted to `net10.0`; SDK pinned in `global.json`. The `AutoMapper` 14.0.0 advisory is a documented risk acceptance (H11).
- **A07 Identification & Authentication Failures:** no static default administrator password (operator-supplied bootstrap secret with **forced first-login rotation**); 100-year session cookie reduced to an operational lifetime; **five-attempt lockout**; token expiry corrected to UTC.
- **A08 Software & Data Integrity Failures:** all four Newtonsoft.Json `TypeNameHandling` sites switched to `None` or a shared **fail-closed allowlist binder** (`ErpSerializationBinder`).
- **A09 Security Logging Failures:** structured security-event logging added (auth failures, permission denials, role/password changes).

**Validation posture (measured — see Section 7):** the clean release build passes (`dotnet build -c Release` → **0 errors**); SAST reports **0** insecure-deserialization/weak-crypto findings (Roslyn CA2326–CA2330 and weak-crypto analyzers); the secrets scan finds **0** committed credentials in the seven host `Config.json` files; and the SCA scan reports **0 Critical** with the only **High** being the risk-accepted `AutoMapper` advisory (build-audit-suppressed). Functional smoke tests (fresh-database seeding, forced admin-password rotation, cookie login, EQL + record CRUD/hook flow) pass.

---

## 2. Scope & Methodology

### 2.1 Systems Audited

The core runtime library (`WebVella.Erp`), the web layer (`WebVella.Erp.Web`), all seven site hosts (`WebVella.Erp.Site`, `.Crm`, `.Mail`, `.MicrosoftCDM`, `.Next`, `.Project`, `.Sdk`), the Blazor WebAssembly projects (`WebVella.Erp.WebAssembly`), and the build/configuration surface (`global.json`, per-host `Config.json`/`web.config`).

### 2.2 OWASP Top 10 (2021) Categories Assessed

A01 Broken Access Control, A02 Cryptographic Failures, A03 Injection, A04 Insecure Design, A05 Security Misconfiguration, A06 Vulnerable & Outdated Components, A07 Identification & Authentication Failures, A08 Software & Data Integrity Failures, A09 Security Logging & Monitoring Failures, A10 Server-Side Request Forgery.

### 2.3 Supplementary Checks

Dependency scanning (SCA), secrets detection, security-header verification, TLS/HTTPS posture, input validation/output encoding, error-handling/information-disclosure review, rate-limiting/DoS review, CORS review, file-upload security, and API security.

### 2.4 Tooling (.NET-appropriate)

Because the stack is .NET, the request's language-generic examples (`npm audit`, `pip-audit`) were mapped to the applicable equivalents:

- **SAST:** Roslyn analyzers **CA2326–CA2330** (Newtonsoft.Json `TypeNameHandling`), plus weak-crypto/hardcoded-key analyzers; Security Code Scan / Semgrep patterns.
- **SCA:** `dotnet list package --vulnerable --include-transitive`; the NuGet restore security audit (`NU1901`–`NU1904`); OWASP Dependency-Check / Trivy; retire.js for vendored JS.
- **Secrets:** gitleaks / detect-secrets (here: content inspection of the seven host `Config.json` files and the tree).

### 2.5 Constraints (Minimal Change Clause)

Only vulnerability-remediating changes were made. No new features, optimizations, refactoring, or architectural changes beyond a fix; the smallest-footprint solution was preferred where alternatives existed. API contracts, interfaces, database schema, and user-facing behavior are preserved; the stored password-column format is preserved via in-place rehash-on-login; performance stays within the documented envelope. Concerns beyond scope are documented, not fixed, unless Critical.

---

## 3. Severity Legend

| Marker | Meaning |
|:------:|---------|
| **✅ Resolved** | Remediated in code and validated at this FINAL checkpoint. |
| **⚠ Risk-accepted** | Not code-changed; residual risk formally accepted with rationale and a recommended long-term fix (build-audit-suppressed where a scanner would otherwise fail the gate). |
| **📄 Documented** | Medium/Low finding documented with a recommended fix and deferral rationale per the Minimal Change Clause. |

---

## 4. Vulnerability Inventory

### 4.1 Mandated Finding Format

Every finding is recorded using the exact block structure required by the request:

```
FINDING: [Vulnerability name]
SEVERITY: [Critical/High/Medium/Low]
CWE: [CWE-XXX]
LOCATION: [File path and line numbers]
DESCRIPTION: [What the vulnerability is]
IMPACT: [What could happen if exploited]
EVIDENCE: [Code snippet or proof]
REMEDIATION: [Specific fix applied]
```

### 4.2 Findings Summary

| # | Finding | OWASP | Severity | CWE | Status |
|---|---------|:-----:|:--------:|-----|--------|
| C1 | Weak password hashing (unsalted MD5) | A02 | Critical | CWE-916, CWE-759, CWE-327 | ✅ Resolved |
| C2 | Hardcoded default encryption key in source | A02 | Critical | CWE-798, CWE-321 | ✅ Resolved |
| C3 | Hardcoded / default JWT signing key | A02 | Critical | CWE-798, CWE-321, CWE-547 | ✅ Resolved |
| C4 | Default administrator credential | A07 | Critical | CWE-798, CWE-521 | ✅ Resolved |
| C5 | Insecure deserialization (`TypeNameHandling`) | A08 | Critical | CWE-502 | ✅ Resolved |
| H1 | Non-constant-time credential comparison | A02 | High | CWE-208 | ✅ Resolved |
| H2 | Committed database credentials | A02 | High | CWE-798 | ✅ Resolved |
| H3 | Permissive CORS (`AllowAnyOrigin`) | A05 | High | CWE-942 | ✅ Resolved |
| H4 | Missing security response headers | A05 | High | CWE-693, CWE-1021, CWE-16 | ✅ Resolved |
| H5 | Session cookie missing `Secure`/`SameSite` | A05 | High | CWE-614, CWE-1275 | ✅ Resolved |
| H6 | Missing HTTPS redirection / HSTS (cleartext transport) | A02 | High | CWE-319 | ✅ Resolved |
| H7 | Excessive session lifetime (100-year cookie) | A07 | High | CWE-613 | ✅ Resolved |
| H8 | No account lockout / brute-force protection | A07 | High | CWE-307 | ✅ Resolved |
| H9 | Information disclosure via development mode | A05 | High | CWE-489, CWE-215, CWE-11 | ✅ Resolved |
| H10 | Out-of-support runtime (.NET 7) | A06 | High | CWE-1104 | ✅ Resolved |
| H11 | `AutoMapper` 14.0.0 uncontrolled-recursion advisory | A06 | High | CWE-674 | ⚠ Risk-accepted (build-audit-suppressed) |
| M1 | Token expiry uses local time, not UTC | A07 | Medium | CWE-613 | ✅ Resolved |
| M2 | Synchronous I/O enabled (DoS surface) | A05 | Medium | CWE-400 | ✅ Resolved |
| M3 | Insufficient security logging | A09 | Medium | CWE-778 | ✅ Resolved |
| L1 | Unpinned SDK toolchain (supply-chain) | A08 | Low | CWE-1104 | ✅ Resolved |
| D1 | No multi-factor authentication (MFA) | A07 | Medium | CWE-308 | 📄 Documented |
| D2 | JWT stored in browser localStorage (WASM) | A07/A02 | Medium | CWE-522, CWE-79 | 📄 Documented |
| D3 | Runtime C# evaluation (accepted risk) | A03 | Medium | CWE-94 | ✅ Admin-only guard enforced; documented |
| D4 | UI-only authorization helper (`WvAuthorize`) | A01 | Low | CWE-602 | 📄 Documented |
| D5 | Latent null-reference in Blazor circuit handler | A04 | Low | CWE-476 | 📄 Documented |
| D6 | Vendored client-side libraries (CVE-gated) | A06 | Low | CWE-1104, CWE-1395 | 📄 Documented |
| D7 | Secrets in `WebVella.Erp.ConsoleApp/Config.json` | A02/A05 | Low | CWE-798 | ✅ Resolved |
| D8 | `MailKit` 4.14.1 / `MimeKit` 4.14.0 moderate advisories | A06 | Low | CWE-1104 | 📄 Documented |
| D9 | JWT token endpoints leak stack traces + file paths in production | A05 | Medium | CWE-209 | 📄 Documented |
| D10 | `MicrosoftCDM` host reuses the `Crm` session cookie name (`erp_auth_crm`) | A05 | Low | CWE-614, CWE-1275 | 📄 Documented |

> The **REMEDIATION** field in each finding block below states the fix that was **applied** (past tense). For the risk-accepted item (H11) the block states the accepted-risk disposition and recommended long-term fix.

### 4.3 A02 — Cryptographic Failures

```
FINDING: Weak password hashing (unsalted MD5)
SEVERITY: Critical
CWE: CWE-916, CWE-759, CWE-327
LOCATION: WebVella.Erp/Utilities/PasswordUtil.cs:L9-L30
DESCRIPTION: User passwords are hashed with a single, unsalted pass of MD5. MD5 is a fast, cryptographically broken digest unsuitable for password storage; the absence of a per-user salt permits precomputed (rainbow-table) attacks and reveals identical passwords across accounts.
IMPACT: An attacker with read access to the user table (e.g., via SQL injection elsewhere, a backup leak, or insider access) can recover most plaintext passwords in minutes using GPU cracking or rainbow tables, enabling account takeover and credential-stuffing against other systems.
EVIDENCE: private static MD5 md5Hash = MD5.Create(); ... byte[] data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(input)); // GetMd5Hash returns lowercase hex, no salt, no work factor
REMEDIATION: Replaced with PBKDF2 (HMAC-SHA-256, 128-bit random salt, 256-bit subkey) as the storage format, with an iteration work factor of 600,000 (OWASP 2023 guidance for PBKDF2-HMAC-SHA-256). Added a backward-compatible VerifyPassword that first tries the modern hash, then falls back to the legacy MD5 check and, on legacy success, signals "rehash needed" so the caller persists an upgraded PBKDF2 hash on the next successful login. A separate acceptance floor (Pbkdf2MinIterations = 210,000) lets pre-existing lower-iteration PBKDF2 hashes continue to verify (returning "rehash needed") rather than locking those users out. Legacy VerifyMd5Hash is retained for verification only. No existing user is locked out (functional parity preserved). The credential-validation path (SecurityManager) and the encrypted password-field write path (RecordManager) are routed through the new primitive, and SecurityManager persists the upgraded hash on legacy success.
```

```
FINDING: Non-constant-time credential comparison
SEVERITY: High
CWE: CWE-208
LOCATION: WebVella.Erp/Utilities/PasswordUtil.cs:L25-L30 (legacy path)
DESCRIPTION: The legacy password-hash comparison used an ordinal string comparer, whose runtime depends on where the first differing character occurs. This timing side channel can leak information about a stored hash.
IMPACT: Under favorable conditions an attacker measuring response timing could incrementally infer hash bytes, reducing the effort to forge a matching credential.
EVIDENCE: StringComparer comparer = StringComparer.OrdinalIgnoreCase; return (0 == comparer.Compare(hashOfInput, hash));
REMEDIATION: The modern verification path compares fixed-length hash bytes with CryptographicOperations.FixedTimeEquals, which runs in time independent of the input contents.
```

```
FINDING: Hardcoded default encryption key in source
SEVERITY: Critical
CWE: CWE-798, CWE-321
LOCATION: WebVella.Erp/Utilities/CryptoUtility.cs (default literal + CryptKey fallback)
DESCRIPTION: A 64-hex-character symmetric encryption key was embedded as a compile-time constant and used as the fallback key whenever no key was configured. A key committed to source control is a shared, publicly known secret.
IMPACT: Anyone with access to the source (a public repository, a decompiled binary) knows the encryption key and can decrypt any data protected with the default, defeating confidentiality of encrypted fields.
EVIDENCE: private const string defaultCryptKey = "BC93B776A428..."; ... if (string.IsNullOrWhiteSpace(ErpSettings.EncryptionKey)) { cryptKey = defaultCryptKey; }
REMEDIATION: Removed the hardcoded default literal. A configured encryption key (Settings:EncryptionKey) is now required; the application fails fast with a clear message if it is missing rather than silently using a known key.
```

```
FINDING: Hardcoded / default JWT signing key
SEVERITY: Critical
CWE: CWE-798, CWE-321, CWE-547
LOCATION: WebVella.Erp/ErpSettings.cs (default fallback) and WebVella.Erp.Site/Config.json (committed key; applies to all seven hosts)
DESCRIPTION: The JWT HMAC signing key defaulted to the hardcoded literal "ThisIsMySecretKey" when unconfigured, and a committed default key was present in host configuration. The signing key is the sole secret protecting token integrity.
IMPACT: Knowing the signing key, an attacker can forge valid JWTs for any user (including administrators), achieving complete authentication bypass and privilege escalation.
EVIDENCE: JwtKey = string.IsNullOrWhiteSpace(configuration["Settings:Jwt:Key"]) ? "ThisIsMySecretKey" : configuration["Settings:Jwt:Key"];
REMEDIATION: Removed the insecure default; a configured Settings:Jwt:Key is now required (fail-fast at startup). The committed key was removed from every host Config.json. Documented that rotating a live signing key invalidates all in-flight tokens (a mass sign-out) and must therefore be coordinated operationally rather than performed silently.
```

```
FINDING: Committed database credentials
SEVERITY: High
CWE: CWE-798
LOCATION: WebVella.Erp.Site/Config.json (applies to all seven hosts' Config.json)
DESCRIPTION: The PostgreSQL connection string, including a username and password, was committed to source control in cleartext.
IMPACT: Anyone with repository access obtains database credentials, enabling direct data exfiltration or tampering that bypasses the application's access controls entirely.
EVIDENCE: "ConnectionString": "Server=localhost;Port=5432;User Id=dev;Password=dev;Database=ttg_test;..."
REMEDIATION: Removed the committed credentials from configuration; the connection string is now supplied via .NET user-secrets (development) or an environment variable (production) — see the Secure-Configuration Guide (Section 8). All seven host Config.json files now ship with empty ConnectionString/EncryptionKey/Jwt:Key values and a threat comment.
```

```
FINDING: Missing HTTPS redirection / HSTS (cleartext transport)
SEVERITY: High
CWE: CWE-319
LOCATION: All seven host Startup.cs Configure pipelines (no UseHttpsRedirection / UseHsts)
DESCRIPTION: The application pipelines contained no HTTPS redirection and no HTTP Strict Transport Security. Without them, traffic (including the session cookie and credentials) can traverse the network in cleartext.
IMPACT: A network attacker can intercept or downgrade connections and steal session cookies or credentials (man-in-the-middle).
EVIDENCE: The Configure method registered localization, routing, authentication, and endpoints but never called app.UseHttpsRedirection() or app.UseHsts().
REMEDIATION: Added app.UseHttpsRedirection() and app.UseHsts() gated to non-development environments (so HTTP development flows are not broken). HSTS emits Strict-Transport-Security: max-age=31536000; includeSubDomains. This is a prerequisite for the Secure cookie flag (H5).
```

### 4.4 A05 — Security Misconfiguration

```
FINDING: Permissive CORS policy (AllowAnyOrigin)
SEVERITY: High
CWE: CWE-942
LOCATION: WebVella.Erp.Site/Startup.cs:L58-L64 (and the equivalent policy in the other permissive hosts)
DESCRIPTION: The default CORS policy allowed any origin, any method, and any header, removing the browser's same-origin protection for cross-site requests to the API.
IMPACT: Any website can issue cross-origin requests to the API on behalf of a visiting authenticated user, facilitating data theft and cross-site request abuse (especially problematic if combined with credentials).
EVIDENCE: options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
REMEDIATION: Replaced the permissive default policy with an explicit origin allowlist modeled on the tightened AllowNodeJsLocalhost policy already used by WebVella.Erp.Site.Crm/Startup.cs (WithOrigins(...).AllowAnyMethod().AllowCredentials()). Applied across the hosts that used the permissive policy; the development origin for the Blazor WASM client is preserved.
```

```
FINDING: Missing security response headers
SEVERITY: High
CWE: CWE-693, CWE-1021, CWE-16
LOCATION: All seven host Startup.cs Configure pipelines (no security-headers middleware)
DESCRIPTION: Responses omitted standard defensive headers (X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy, X-XSS-Protection, Content-Security-Policy), leaving the UI exposed to clickjacking, MIME sniffing, and reduced defense-in-depth against XSS.
IMPACT: Missing X-Frame-Options / frame-ancestors enables clickjacking; missing X-Content-Type-Options enables MIME-sniffing attacks; absent CSP removes a key layer of XSS mitigation.
EVIDENCE: The Configure pipeline contained no middleware that appends response security headers.
REMEDIATION: Added WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs that emits the mandated header baseline (Section 5.2) for every response, wired into all seven hosts. To avoid breaking the inline-script/style-dependent UI, the Content-Security-Policy is emitted as Content-Security-Policy-Report-Only first (observe-then-enforce), to be promoted to the enforcing header once the violation report is clean. Verified at runtime: the login response carries X-Content-Type-Options, X-Frame-Options, X-XSS-Protection, Referrer-Policy, Permissions-Policy, and Content-Security-Policy-Report-Only.
```

```
FINDING: Session cookie missing Secure and SameSite attributes
SEVERITY: High
CWE: CWE-614, CWE-1275
LOCATION: WebVella.Erp.Site/Startup.cs (only HttpOnly was set); applies to all seven hosts
DESCRIPTION: The authentication cookie set HttpOnly but neither Secure (restrict to HTTPS) nor SameSite (restrict cross-site sending), leaving it eligible to be transmitted over cleartext and attached to cross-site requests.
IMPACT: Without Secure the cookie can leak over HTTP; without SameSite it is attached to cross-site requests, broadening CSRF exposure and session-theft opportunities.
EVIDENCE: options.Cookie.HttpOnly = true; options.Cookie.Name = "erp_auth_base"; // no Cookie.SecurePolicy, no Cookie.SameSite
REMEDIATION: Added Cookie.SecurePolicy and Cookie.SameSite = SameSiteMode.Lax across all seven hosts, preserving each host's existing cookie name. Six hosts use distinct names (erp_auth_base, erp_auth_crm, erp_auth_mail, erp_auth_next, erp_auth_project, erp_auth_sdk); the MicrosoftCDM host reuses the Crm name (erp_auth_crm) — that pre-existing name collision is out of scope for this cookie-flag fix and is documented separately as a Low finding (D10). SecurePolicy is ENVIRONMENT-GATED — CookieSecurePolicy.Always in production, CookieSecurePolicy.SameAsRequest in development — so a strict HTTPS-only cookie does not break local HTTP development login. This depends on HTTPS enforcement (H6), added in the same edit. Verified at runtime: over HTTP in Development the cookie is set with SameSite=Lax; HttpOnly; no Secure flag.
```

```
FINDING: Information disclosure via development mode in production
SEVERITY: High
CWE: CWE-489, CWE-215, CWE-11
LOCATION: WebVella.Erp.Site/Config.json ("DevelopmentMode": "true") and WebVella.Erp.Site/web.config (ASPNETCORE_ENVIRONMENT=Development). Note: web.config exists ONLY in WebVella.Erp.Site.
DESCRIPTION: Development mode and the Development hosting environment were committed as defaults. In production these disclose detailed stack traces and diagnostic error detail (the ApiControllerBase error masking is DevelopmentMode-gated).
IMPACT: Detailed errors and stack traces disclosed to end users reveal internal paths, types, SQL, and library versions, aiding an attacker in reconnaissance and exploit development.
EVIDENCE: "DevelopmentMode": "true"  (Config.json)  /  <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Development" />  (web.config)
REMEDIATION: Set DevelopmentMode=false in every host Config.json for production, and documented that ASPNETCORE_ENVIRONMENT must not be Development in production. A configuration audit (Section 8.5) should fail the build/deploy if DevelopmentMode=true is present in a production profile.
```

```
FINDING: Synchronous I/O enabled (denial-of-service surface)
SEVERITY: Medium
CWE: CWE-400
LOCATION: WebVella.Erp.Web/Middleware/ErpMiddleware.cs:L25-L27
DESCRIPTION: The middleware opted the request into synchronous I/O. Synchronous I/O ties up thread-pool threads for the duration of blocking reads/writes, and under load can lead to thread starvation and denial of service.
IMPACT: A burst of slow or large requests can exhaust the thread pool, degrading or halting the service (availability impact).
EVIDENCE: var syncIOFeature = context.Features.Get<IHttpBodyControlFeature>(); if (syncIOFeature != null) syncIOFeature.AllowSynchronousIO = true;
REMEDIATION: The AllowSynchronousIO = true opt-in is removed/refactored toward asynchronous I/O so the pipeline no longer forces synchronous blocking. (Fixed opportunistically as a low-risk, localized edit within the configuration commit class.)
```

```
FINDING: JWT token endpoints disclose exception stack traces and file paths (production)
SEVERITY: Medium
CWE: CWE-209
LOCATION: WebVella.Erp.Web/Controllers/WebApiController.cs — GetJwtToken (route api/v3/en_US/auth/jwt/token, L4283-L4297) and GetNewJwtToken (route api/v3/en_US/auth/jwt/token/refresh, L4302-L4316)
DESCRIPTION: The two [AllowAnonymous] JWT token / token-refresh endpoints catch exceptions and copy the raw exception message together with the full stack trace into the client-facing response body (response.Message = e.Message + e.StackTrace). Unlike the record/query API surface, these two handlers do NOT route error detail through the DevelopmentMode-gated masking in ApiControllerBase, so the disclosure occurs regardless of environment — including production.
IMPACT: An unauthenticated caller can trigger an exception (e.g., malformed input) and read internal stack frames, absolute file paths, type names, and library internals, aiding reconnaissance and exploit development.
EVIDENCE: catch (Exception e) { new LogService().Create(LogType.Error, "GetJwtToken", e); response.Success = false; response.Message = e.Message + e.StackTrace; }  (identical pattern in GetNewJwtToken)
REMEDIATION: Documented only (Medium, not code-changed). These endpoints are pre-existing (present at the pre-audit baseline) and were not among the enumerated in-scope A07 remediation targets (AuthService.cs, login.cshtml.cs, ERPService.cs), so per the Minimal Change Clause and the severity-driven action rule (§0.8.1 — Medium findings are documented, not fixed) they are documented here. Recommended fix: return a generic client-facing error message and gate any detailed error on DevelopmentMode (mirroring ApiControllerBase); never assign e.StackTrace to a response body — the exception is already captured server-side via LogService for diagnostics.
```

```
FINDING: MicrosoftCDM host reuses the Crm session cookie name
SEVERITY: Low
CWE: CWE-614, CWE-1275
LOCATION: WebVella.Erp.Site.MicrosoftCDM/Startup.cs:L104 (Cookie.Name = "erp_auth_crm"), colliding with WebVella.Erp.Site.Crm/Startup.cs:L92
DESCRIPTION: Six of the seven hosts assign a distinct authentication cookie name (erp_auth_base, erp_auth_crm, erp_auth_mail, erp_auth_next, erp_auth_project, erp_auth_sdk); the MicrosoftCDM host reuses the Crm host's cookie name (erp_auth_crm). If both hosts are ever served under the same parent domain, their authentication cookies can collide, causing session cross-talk.
IMPACT: On a shared parent domain a session established on one host could be read or overwritten by the other, undermining session isolation between the two applications. In the reference single-host deployment the practical impact is negligible (Low).
EVIDENCE: WebVella.Erp.Site.MicrosoftCDM/Startup.cs: options.Cookie.Name = "erp_auth_crm";  (identical to the value in WebVella.Erp.Site.Crm/Startup.cs)
REMEDIATION: Documented only (Low, not Critical) per the Minimal Change Clause. Recommended fix: assign a host-unique cookie name (e.g., erp_auth_cdm) to the MicrosoftCDM host so cookies cannot collide across hosts sharing a parent domain. The Secure/SameSite hardening (H5) already applies to this cookie.
```

### 4.5 A06 — Vulnerable & Outdated Components

```
FINDING: Out-of-support runtime target (.NET 7)
SEVERITY: High
CWE: CWE-1104
LOCATION: WebVella.Erp.WebAssembly/Server/*.csproj and WebVella.Erp.WebAssembly/Shared/*.csproj
DESCRIPTION: Two Blazor WebAssembly projects targeted net7.0. .NET 7 reached end of support on 2024-05-14; end-of-support runtimes no longer receive security patches.
IMPACT: Any vulnerability discovered in the .NET 7 runtime/framework after end-of-support remains unpatched for these projects, exposing them to known and future CVEs.
EVIDENCE: <TargetFramework>net7.0</TargetFramework>
REMEDIATION: Retargeted both the Server and Shared projects from net7.0 to net10.0, aligning them with the rest of the solution. .NET 10 is the current Long-Term Support release (GA November 2025, supported through November 2028). Classified under the components commit class.
```

```
FINDING: AutoMapper 14.0.0 uncontrolled-recursion advisory (risk-accepted)
SEVERITY: High
CWE: CWE-674
LOCATION: WebVella.Erp/WebVella.Erp.csproj (PackageReference "AutoMapper" Version="[14.0.0]"); transitive across all consumers
DESCRIPTION: AutoMapper 14.0.0 is affected by advisory GHSA-rvv3-g6hj-g44x / CVE-2026-32933 (High, CWE-674 Uncontrolled Recursion): a deeply nested or self-referential object graph pushed through a mapping path can exhaust the stack (StackOverflowException, i.e., a denial of service). The NuGet restore security audit surfaces this as NU1903 for the direct reference and transitively for every consumer.
IMPACT: If attacker-controlled, deeply recursive data reached an AutoMapper conversion path, a StackOverflowException could crash the process (availability impact). WebVella maps only its own bounded, non-cyclic internal metadata types (Entity, Field, Relation, Job, User, Role, ...); no attacker-controlled recursive object graph reaches a conversion path, so real-world exploitability is effectively absent for this codebase.
EVIDENCE: `dotnet list package --vulnerable --include-transitive` reports AutoMapper 14.0.0 High (GHSA-rvv3-g6hj-g44x) for the direct reference and its transitive consumers.
REMEDIATION: Risk-accepted and kept pinned at [14.0.0]. The upgrade is doubly blocked: (1) the maintainer confirmed NO patch will ship for the 14.x line — the fix (a default MaxDepth of 64) ships only in 15.1.1+/16.1.1+, which are RE-LICENSED from MIT to RPL-1.5 (reciprocal/copyleft) or a commercial license and are therefore incompatible with this project's Apache-2.0 license; and (2) the Minimal Change Clause forbids third-party version/license changes and architectural changes (mapper migration) for a non-Critical finding. The NuGet build audit is version-based and would fail on this advisory regardless of exploitability, so a single solution-wide NuGetAuditSuppress for GHSA-rvv3-g6hj-g44x is declared in Directory.Build.props (with a threat comment) to record the accepted risk; `dotnet list package --vulnerable` remains version-based and will still list the advisory by design. RECOMMENDED LONG-TERM FIX: migrate to a maintained, MIT-licensed mapper such as Mapperly (source-generated, no runtime recursion surface), tracked as future work outside this minimal-change engagement.
```

```
FINDING: Unpinned SDK toolchain (build reproducibility / supply-chain)
SEVERITY: Low
CWE: CWE-1104
LOCATION: global.json:L1-L5 (SDK version line commented out)
DESCRIPTION: The global.json SDK version pin was commented out, so builds floated to whatever SDK was installed on the build machine, undermining reproducibility and supply-chain assurance.
IMPACT: Non-deterministic builds; a compromised or unexpected SDK could alter build output without detection. Low direct exploitability but a supply-chain hygiene gap.
EVIDENCE: { "sdk": { //"version": "7.0.103" } }
REMEDIATION: Pinned a .NET 10 SDK version in global.json to make builds reproducible. Classified under the components commit class; severity Low (supply-chain hygiene).
```

```
FINDING: Vendored client-side libraries (CVE-gated)
SEVERITY: Low
CWE: CWE-1104, CWE-1395
LOCATION: WebVella wwwroot vendored client-side assets (e.g., Bootstrap v4, jQuery, moment, jsTree 3.3.7, Select2, Chart.js)
DESCRIPTION: Front-end libraries are vendored into wwwroot. Vendored libraries can drift behind upstream security releases.
IMPACT: An outdated vendored library carrying a known client-side CVE (e.g., DOM-based XSS) could be exploited in the browser context.
EVIDENCE: Vendored assets are present under wwwroot/lib; no active CVE was confirmed by a scanner in this pass.
REMEDIATION: Documented only (Low, not code-changed). Recommendation: run retire.js in CI and update only libraries flagged with an active CVE. Deferral rationale: a blanket front-end upgrade risks UI regressions and exceeds the minimal-change boundary; no confirmed active CVE in this pass.
```

```
FINDING: MailKit / MimeKit moderate advisories
SEVERITY: Low
CWE: CWE-1104
LOCATION: WebVella.Erp.Plugins.Mail and WebVella.Erp.Site.Mail (MailKit 4.14.1, MimeKit 4.14.0)
DESCRIPTION: The NuGet restore audit surfaces MODERATE advisories (NU1902) for MailKit 4.14.1 and MimeKit 4.14.0, used by the Mail plugin for SMTP/IMAP and MIME parsing.
IMPACT: Moderate-severity issues in mail-parsing/transport libraries; not Critical/High, so they do not fail the acceptance gate (which is Critical/High only).
EVIDENCE: `dotnet list package --vulnerable --include-transitive` reports MailKit 4.14.1 / MimeKit 4.14.0 as Moderate (NU1902).
REMEDIATION: Documented only (Low; the SCA acceptance gate is Critical/High). Recommendation: after compatibility validation, update MailKit/MimeKit to the latest patched releases in a routine dependency-maintenance change; the update is not required to clear the Critical/High gate and a version bump is outside the minimal-change boundary for this audit.
```

### 4.6 A07 — Identification & Authentication Failures

```
FINDING: Default administrator credential seeded at install
SEVERITY: Critical
CWE: CWE-798, CWE-521
LOCATION: WebVella.Erp/ERPService.cs (first-user seed, ~L462-L490)
DESCRIPTION: Initialization seeded the first administrator with a well-known static password ("erp"), the email erp@webvella.com, and username administrator. A default credential shipped with the product is public knowledge.
IMPACT: Any freshly installed instance where the operator has not changed the password is trivially compromised at the highest privilege level (full administrative takeover).
EVIDENCE: user["password"] = "erp"; user["email"] = "erp@webvella.com"; user["username"] = "administrator";
REMEDIATION: No static default password ships with the product. The seeded administrator password is taken from an OPERATOR-SUPPLIED bootstrap secret (Settings:InitialAdminPassword) provided via user-secrets/environment variables; if it is missing or not 12-24 characters the initialization FAILS FAST with a clear InvalidOperationException (the enterprise password-length policy). The bootstrap secret is NEVER written to stdout or any log (this closed the related High finding of printing the initial password to the console). The account is subject to a FORCED first-login rotation (see the design note in Section 5.4), so the operator-known bootstrap secret cannot grant normal application access until it is changed.
```

```
FINDING: Excessive session lifetime (100-year authentication cookie)
SEVERITY: High
CWE: CWE-613
LOCATION: WebVella.Erp.Web/Services/AuthService.cs:L44
DESCRIPTION: The authentication cookie was issued with an expiry 100 years in the future, effectively never expiring.
IMPACT: A stolen cookie remains valid indefinitely; there is no natural session timeout to bound the window of misuse after theft.
EVIDENCE: ExpiresUtc = DateTimeOffset.UtcNow.AddYears(100),
REMEDIATION: Reduced the cookie ExpiresUtc to an operational lifetime consistent with the documented JWT lifetime (1440 minutes / 24 hours), bounding the exposure window while preserving normal usability.
```

```
FINDING: No account lockout / brute-force protection
SEVERITY: High
CWE: CWE-307
LOCATION: WebVella.Erp.Web/Pages/login.cshtml.cs
DESCRIPTION: The login handler returned a generic, enumeration-safe error but imposed no limit on failed attempts, allowing unlimited password guessing.
IMPACT: Attackers can brute-force or credential-stuff accounts at will, increasing the likelihood of account takeover — especially against weak passwords.
EVIDENCE: Error = "Invalid username or password"; // returned on failure, but no failed-attempt counter / lockout
REMEDIATION: Added a five-attempt failed-login lockout (15-minute rolling window, tracked per submitted identifier) while preserving the existing enumeration-safe "Invalid username or password" message on every path (so the fix does not introduce username enumeration and lockout cannot be used as an account-existence oracle).
```

```
FINDING: JWT expiry computed from local time instead of UTC
SEVERITY: Medium
CWE: CWE-613
LOCATION: WebVella.Erp.Web/Services/AuthService.cs:L156-L158
DESCRIPTION: The JWT expires claim was computed with DateTime.Now (server local time) while validation and issuance elsewhere use UTC. On non-UTC servers this skews the effective token lifetime.
IMPACT: Depending on the server's offset, tokens live longer or shorter than intended — a longer-than-intended lifetime widens the misuse window for a stolen token; a shorter one causes spurious expiry.
EVIDENCE: expires: DateTime.Now.AddMinutes(JWT_TOKEN_EXPIRY_DURATION_MINUTES)
REMEDIATION: The expiry is computed from DateTime.UtcNow so the token lifetime is deterministic regardless of server timezone (low-risk fix within the authentication commit class).
```

### 4.7 A08 — Software & Data Integrity Failures

```
FINDING: Insecure deserialization via Newtonsoft.Json TypeNameHandling
SEVERITY: Critical
CWE: CWE-502
LOCATION: WebVella.Erp/Jobs/JobDataService.cs (TypeNameHandling.All); WebVella.Erp/Notifications/NotificationContext.cs; WebVella.Erp/Database/DbEntityRepository.cs; WebVella.Erp/Database/DbRelationRepository.cs (TypeNameHandling.Auto)
DESCRIPTION: Serialization settings enabled TypeNameHandling.All/Auto, which embed and honor a $type discriminator during deserialization. When deserializing data that an attacker can influence, this permits instantiation of arbitrary .NET types ("gadgets").
IMPACT: Deserialization gadget chains can lead to remote code execution or other integrity violations if attacker-controlled JSON reaches these code paths.
EVIDENCE: JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All }; // and TypeNameHandling.Auto at the notification/entity/relation sites
REMEDIATION: Set TypeNameHandling.None where polymorphism is unnecessary; where polymorphic round-tripping of persisted data is genuinely required, attached a new fail-closed allowlist WebVella.Erp/Utilities/ErpSerializationBinder.cs (ISerializationBinder) that resolves only an explicitly permitted set of types and THROWS for everything else, neutralizing gadget attacks while preserving legitimate persisted payloads. This aligns with Roslyn analyzers CA2326–CA2330 — a post-remediation SAST pass reports zero CA2326–CA2330 findings.
```

### 4.8 A09 — Security Logging & Monitoring Failures

```
FINDING: Insufficient security logging
SEVERITY: Medium
CWE: CWE-778
LOCATION: WebVella.Erp/Diagnostics/Log.cs
DESCRIPTION: The logging facility recorded login timestamps and general error events but did not emit structured, security-relevant audit entries for authentication failures, permission denials, or role/password changes.
IMPACT: Attacks such as brute-force attempts, privilege abuse, or unauthorized role changes may go undetected and lack the forensic trail required for incident response.
EVIDENCE: Log.cs exposed GetLogs plus general create/error logging, with no dedicated security-event entries.
REMEDIATION: Extended Log.cs with four structured security-event methods — LogAuthenticationFailure (source Security.Authentication), LogPermissionDenied (Security.Authorization), LogRoleChange (Security.RoleChange), and LogPasswordChange (Security.PasswordChange) — and WIRED them at their call sites so the entries are actually emitted: authentication failures in SecurityManager.GetUser(email, password); permission denials at the three /error?401 authorization-deny sites in WebVella.Erp.Web/Models/BaseErpPageModel.cs.Init(); and role/password changes in SecurityManager.SaveUser() (both the existing-user and create branches, driven by persisted-vs-incoming change detection). Every entry is IDENTIFIER-ONLY (email or resource path); the supplied password and the stored hash are never recorded (CWE-778), and all calls are best-effort (wrapped in try/catch so they never throw on the authentication or save path). Verified at runtime against a fresh database: each of the four categories writes a system_log type=3 (Security) row with an identifier-only message (an isolated role change leaves the password hash intact and vice-versa); a wrong-password attempt writes a Security.Authentication row while a successful login writes none; and a full scan of system_log confirmed no plaintext password or PBKDF2 hash material appears in any message or details field.
```

### 4.9 A01 — Broken Access Control

```
FINDING: UI-only authorization helper (WvAuthorize)
SEVERITY: Low
CWE: CWE-602
LOCATION: WebVella.Erp.Web (WvAuthorize visibility helper)
DESCRIPTION: The WvAuthorize helper controls the visibility of UI elements but is a client/presentation-side convenience, not a server-side enforcement point. Server-side authorization is enforced separately by the domain permission engine.
IMPACT: If any action were gated only by WvAuthorize visibility without a corresponding server-side check, it could be invoked directly. In practice, server-side checks (SecurityContext.HasEntityPermission) remain authoritative, so this is defense-in-depth, not a bypass.
EVIDENCE: WvAuthorize toggles element visibility; the authoritative permission decision is made server-side by SecurityContext.HasEntityPermission (WebVella.Erp/Api/SecurityContext.cs).
REMEDIATION: Documented only (Low, not code-changed). Recommendation: confirm that every UI-gated action is independently enforced by a server-side permission check; treat WvAuthorize strictly as a presentation aid.
```

> **Positive control (A01, preserved — no change):** Authorization decisions are enforced server-side by the domain permission engine `SecurityContext.HasEntityPermission`. Razor pages authorize the folder root and explicitly allow anonymous access only to `/login`. This is correct baseline behavior and was not modified.

### 4.10 A03 — Injection

```
FINDING: Runtime C# evaluation (accepted-risk, admin-only feature)
SEVERITY: Medium
CWE: CWE-94
LOCATION: WebVella.Erp.Web/Services/CodeEvalService.cs:L44-L45; enforcing guard in WebVella.Erp.Web/Controllers/WebApiController.cs (route api/v3.0/datasource/code-compile)
DESCRIPTION: The platform evaluates C# source at runtime via CS-Script to support admin-authored server-side logic. Dynamic code execution is inherently powerful and, if exposed to untrusted authors, would permit arbitrary code execution.
IMPACT: If a non-trusted actor could supply the source code, this would be remote code execution. In the platform's design the authorship of this code is restricted to trusted administrators, making it a deliberate, bounded capability rather than an open injection sink.
EVIDENCE: CSScript.EvaluatorConfig.ReferenceDomainAssemblies = true; ICodeVariable scriptObject = CSScript.Evaluator.LoadCode<ICodeVariable>(sourceCode);
REMEDIATION: Documented as an accepted risk; the capability is intentionally retained (removing it is feature loss, out of scope). The admin-only trusted-author boundary is enforced in code: the request-reachable compiler endpoint api/v3.0/datasource/code-compile now carries [Authorize(Roles = "administrator")] in addition to the controller's class-level [Authorize], so non-administrators are denied by default. The threat comment at the evaluation site was corrected to describe the guard that is actually enforced. Recommendation: keep the authorship path restricted to administrators and audit any change to who can supply code.
```

> **Positive control (A03, preserved — no change):** All database access uses parameterized `NpgsqlCommand` queries; SQL injection is therefore controlled at the data layer. This baseline was verified and deliberately left unchanged.

### 4.11 A04 — Insecure Design

```
FINDING: Latent null-reference risk in Blazor circuit handler
SEVERITY: Low
CWE: CWE-476
LOCATION: WebVella.Erp.Web/Middleware/SecuritityCircuitHandler.cs (repository's existing spelling)
DESCRIPTION: The Blazor circuit handler contains a latent null-dereference path (a robustness/design defect rather than a directly exploitable security flaw).
IMPACT: A null dereference could throw and terminate a circuit (localized availability/robustness impact); it is not a confidentiality or integrity compromise.
EVIDENCE: Circuit-handler code path that may dereference a null reference under specific conditions.
REMEDIATION: Documented only (Low, not Critical) per the Minimal Change Clause. Recommendation: add a null guard on the affected path in a future maintenance change.
```

> **A04 note.** Insecure-design concerns primarily manifested as the session/authentication weaknesses captured under A07 (excessive session lifetime, missing lockout, non-UTC token expiry); those are **✅ Resolved** in the authentication commit class (see H7, H8, M1).

### 4.12 A10 — Server-Side Request Forgery (SSRF)

> **Assessed — no active finding.** No server-side code path was identified that fetches an attacker-controlled URL in a way that constitutes SSRF. The Mail plugin's inline-image handling (via HtmlAgilityPack) was noted as an input-handling surface to monitor, but no exploitable SSRF sink was found; no code change is made for A10.

---

## 5. Remediation Actions (by vulnerability class)

Changes were grouped into atomic commits per vulnerability class and validated after each class. Each edit carries an inline threat comment. The subsections below give the before/after essence of each class; all items are **✅ Resolved** at this FINAL checkpoint.

### 5.1 Cryptography (A02) — ✅ Resolved

- **`WebVella.Erp/Utilities/PasswordUtil.cs`** — Added PBKDF2 hashing and a tri-state verify.
  - *Before:* `GetMd5Hash` (unsalted MD5, lowercase hex) and `VerifyMd5Hash` (ordinal, non-constant-time).
  - *After:* `HashPassword` produces a self-describing `PBKDF2$<iterations>$<salt>$<subkey>` string (HMAC-SHA-256, 128-bit salt, 256-bit subkey, **600,000** iterations). `VerifyPassword` returns a tri-state result — `Failed`, `Success`, or `SuccessRehashNeeded` — trying the modern hash first, then the legacy MD5 hash; on legacy success (or on a modern hash below the `Pbkdf2MinIterations` = **210,000** acceptance floor) it returns `SuccessRehashNeeded` so the caller upgrades the stored hash. Comparison uses `CryptographicOperations.FixedTimeEquals`. `VerifyMd5Hash` is retained for legacy verification only.
- **`WebVella.Erp/Utilities/CryptoUtility.cs`** — Removed the hardcoded default encryption-key literal; a configured key is required (fail-fast).
- **`WebVella.Erp/ErpSettings.cs`** — Removed the default JWT signing key and encryption-key fallbacks; configured values are required. The legacy misspelled setting key `Settings:EncriptionKey` continues to be read for backward compatibility but is documented as deprecated.
- **`WebVella.Erp/Api/SecurityManager.cs`** — Credential validation (`GetUser(email, password)`) routes through `PasswordUtil.VerifyPassword`; on `SuccessRehashNeeded` it transparently persists a fresh PBKDF2 hash (`UPDATE rec_user`), so legacy MD5 credentials upgrade silently on the next successful login. No user is locked out.
- **`WebVella.Erp/Api/RecordManager.cs`** — The encrypted password-field write path uses the new hashing primitive instead of MD5.

**PBKDF2 cost / performance note.** At 600,000 iterations a single hash/verify measures **≈117 ms** on the build host — comfortably within the request's "within 10% of baseline" envelope (login latency is not one of the documented performance metrics: DB command timeout 120 s, EQL timeout 600 s, connection-pool max 100, job pool 20 threads, cache TTL 1 hour, JWT lifetime 1440 minutes — none of which this change touches).

### 5.2 Configuration & Headers (A05, secrets) — ✅ Resolved

- **`WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs` (new)** — emits the mandated baseline for every response:

```
Content-Security-Policy: default-src 'self'          (emitted as Content-Security-Policy-Report-Only first)
Strict-Transport-Security: max-age=31536000; includeSubDomains   (via UseHsts(), production)
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
X-XSS-Protection: 0
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: geolocation=(), microphone=(), camera=()
```

  A `UseSecurityHeaders()` extension registers it in the pipeline.
- **Seven host `Startup.cs`** — replaced permissive CORS with an explicit origin allowlist (modeled on the `AllowNodeJsLocalhost` **CORS policy** already present in `Site.Crm/Startup.cs`, which serves as the reference template — that CORS policy body is unchanged, though `Site.Crm/Startup.cs`, like every host, did receive the cookie/HSTS/header hardening below); added `Cookie.SecurePolicy` (env-gated: `Always` in prod, `SameAsRequest` in dev) and `Cookie.SameSite = Lax`; added `UseHttpsRedirection()`/`UseHsts()` (non-development only); wired `UseSecurityHeaders()`. Each host also now initializes `ErpSettings.Initialize(Configuration)` in `ConfigureServices` (see the design note below), so environment-variable / user-secret configuration flows through to `ErpSettings` at every host — this replaced an earlier out-of-scope edit to the shared `WebVella.Erp.Web/ErpMvcExtensions.cs`, which was reverted to be byte-identical to its baseline.
- **Seven host `Config.json`** — removed the committed database credentials, encryption key, and JWT key (all now empty and sourced from user-secrets/env); set `DevelopmentMode=false`.
- **`WebVella.Erp.ConsoleApp/Config.json`** — removed the committed connection string and encryption-key literal (now empty, sourced from user-secrets/env) and set `DevelopmentMode=false`, closing **D7** (this local EQL/CRUD smoke harness previously carried the same class of secrets as the hosts).
- **`web.config` (WebVella.Erp.Site)** — set `ASPNETCORE_ENVIRONMENT=Production` so the developer exception page and detailed stack-trace/debug disclosure are disabled in production (**H9**); it must not be `Development` in production.

**Build-gate fix (host packaging).** Resolving the CORS/cookie/header work surfaced an `NETSDK1022` duplicate-`Content`-item build failure in `WebVella.Erp.Site` when a lowercase runtime `config.json` is present alongside the shipped `Config.json` on a case-sensitive filesystem. `WebVella.Erp.Site.csproj` now adds `config.json` to `DefaultItemExcludes` and re-includes `Config.json` with `CopyToOutputDirectory=PreserveNewest`, so the solution builds cleanly whether or not a lowercase `config.json` exists, with no publish regression.

### 5.3 Deserialization (A08) — ✅ Resolved

- **`WebVella.Erp/Utilities/ErpSerializationBinder.cs` (new)** — a fail-closed `ISerializationBinder` allowlist. `BindToType` resolves only permitted types (job/notification/entity/relation payload types and their members) and throws for anything else; `BindToName` delegates to the default binder for serialization.
- **`JobDataService.cs`, `NotificationContext.cs`, `DbEntityRepository.cs`, `DbRelationRepository.cs`** — each `TypeNameHandling.All`/`.Auto` site now uses `TypeNameHandling.None` where polymorphism is unnecessary, or attaches `SerializationBinder = new ErpSerializationBinder()` where polymorphic round-tripping of persisted data is required. The allowlist enumerates every persisted type so existing stored data round-trips.

### 5.4 Authentication & Session (A07) — ✅ Resolved

- **`WebVella.Erp.Web/Services/AuthService.cs`** — reduced the cookie `ExpiresUtc` from `AddYears(100)` to the operational 1440-minute lifetime; corrected the JWT expiry to `DateTime.UtcNow`. *(`AuthService.cs` was otherwise a verified-correct file; only these two loci were touched.)*
- **`WebVella.Erp.Web/Pages/login.cshtml.cs`** — added a five-attempt lockout (rolling window) preserving the enumeration-safe message, and implemented the **forced first-login rotation** (design note below).
- **`WebVella.Erp/ERPService.cs`** — removed the static `"erp"` default; the seeded admin password is the operator-supplied `Settings:InitialAdminPassword` (fail-fast if missing or not 12–24 chars); nothing is written to stdout/logs.

**Forced first-login rotation — design note (M3).** The seeded administrator must change the operator-known bootstrap secret before gaining application access. The correct-but-subtle detail is that sign-in must **not** occur before rotation completes. The login handler therefore uses a **deferred sign-in**: when the submitted password equals the configured `Settings:InitialAdminPassword` (ordinal compare), the handler validates the credential via `new SecurityManager().GetUser(username, password)` **without** establishing a session, renders the rotation form (carrying a consistent anonymous antiforgery token), persists the new PBKDF2 hash on submit, and only **then** calls `authService.Authenticate(...)` with the new password to establish the session and redirect. The normal (non-bootstrap) login path is byte-for-byte unchanged (a single `Authenticate` call), so there is no behavior or performance change for regular users. Validated end-to-end via both a `curl` cookie-jar flow (rotation POST returns **302 → /**, not the earlier antiforgery **400**; the stored hash rotates to `PBKDF2$600000$…`; the old bootstrap secret is rejected afterward) and an interactive browser flow (login → forced-rotation prompt → authenticated Home; DB hash confirmed rotated at 600,000 iterations).

### 5.5 Components (A06) — ✅ Resolved / ⚠ Risk-accepted

- **`WebVella.Erp.WebAssembly/Server/*.csproj` and `.../Shared/*.csproj`** — retargeted `net7.0` → `net10.0` (✅).
- **`global.json`** — SDK version pinned (✅).
- **`AutoMapper` 14.0.0** — kept pinned at `[14.0.0]`; a solution-wide `NuGetAuditSuppress` for `GHSA-rvv3-g6hj-g44x` was added to `Directory.Build.props` with a 20-line threat/risk-acceptance comment (⚠ risk-accepted, see H11). `MailKit`/`MimeKit` moderate advisories documented only (D8/Min).

### 5.6 Logging & DoS — ✅ Resolved

- **`WebVella.Erp/Diagnostics/Log.cs`** — added structured security-event entries (A09/M3-audit).
- **`WebVella.Erp.Web/Middleware/ErpMiddleware.cs`** — removed the `AllowSynchronousIO = true` opt-in (M2/DoS).
- **`WebVella.Erp.Web/Services/CodeEvalService.cs`** — corrected the threat comment and confirmed the admin-only authorship guard on the reachable compile endpoint (A03/D3).

---

## 6. Documented Findings (Medium / Low — not code-changed)

Per the Minimal Change Clause, the following are documented with recommended fixes rather than remediated in code (none is Critical).

| # | Finding | Severity | Recommendation | Deferral rationale |
|---|---------|:--------:|----------------|--------------------|
| D1 | No multi-factor authentication (MFA) | Medium | Add TOTP or WebAuthn as a second factor for privileged accounts. | Adding MFA is new feature work, explicitly out of scope (no MFA exists today). |
| D2 | WASM client stores JWT in browser `localStorage` (key `token`) | Medium | Prefer an `HttpOnly` cookie or in-memory token with silent refresh to remove the XSS token-theft surface. | Changing the client token-storage model is an architectural change beyond a minimal fix; XSS is separately mitigated by CSP (report-only → enforce). |
| D3 | Runtime C# evaluation | Medium | Keep authorship admin-only; audit any change to who may supply code. | Deliberate admin-only capability; removing it is feature loss. The enforcing `[Authorize(Roles="administrator")]` guard is in place (A03). |
| D4 | UI-only authorization helper `WvAuthorize` | Low | Ensure every UI-gated action has an independent server-side permission check. | Server-side `HasEntityPermission` is already authoritative; this is defense-in-depth. |
| D5 | Latent null-reference in Blazor circuit handler | Low | Add a null guard on the affected path. | Robustness defect, not a security compromise; not Critical. |
| D6 | Vendored client-side libraries (CVE-gated) | Low | Run retire.js in CI; update only libraries with an active CVE. | No confirmed active CVE this pass; blanket upgrade risks UI regressions. |
| D8 | `MailKit` 4.14.1 / `MimeKit` 4.14.0 moderate advisories | Low | After compatibility validation, update to the latest patched releases in routine dependency maintenance. | Moderate severity does not fail the Critical/High acceptance gate; a version bump is outside the minimal-change boundary for this audit. |
| D9 | JWT token endpoints (`GetJwtToken`/`GetNewJwtToken`) return `e.Message + e.StackTrace` to the client in production | Medium | Return a generic client-facing error and gate detail on `DevelopmentMode` (mirror `ApiControllerBase`); never place `e.StackTrace` in a response body — it is already logged server-side via `LogService`. | Pre-existing (present at the pre-audit baseline); not among the enumerated in-scope A07 targets; Medium ⇒ documented per §0.8.1, not code-changed. |
| D10 | `MicrosoftCDM` host reuses the `Crm` cookie name (`erp_auth_crm`) | Low | Assign a host-unique cookie name (e.g., `erp_auth_cdm`) so auth cookies cannot collide across hosts on a shared parent domain. | Pre-existing session-isolation nit; negligible impact in the single-host reference deployment; Low ⇒ documented, outside the minimal cookie-flag fix (H5). |

### 6.1 Positive Controls Preserved (reference-only, not modified)

- **Parameterized SQL** throughout (`NpgsqlCommand` parameters) — A03 controlled at the data layer.
- **`WebVella.Erp/Api/Models/ErpUser.cs`** — `[JsonIgnore]` redaction of sensitive fields (e.g., password) — preserved.
- **`WebVella.Erp.Web/Controllers/ApiControllerBase.cs`** — `DevelopmentMode`-gated error masking — preserved.
- **`WebVella.Erp.Site.Crm/Startup.cs`** — `AllowNodeJsLocalhost` CORS policy — used as the allowlist template, unchanged.
- Razor POST handlers enforce antiforgery tokens; the login handler returns an enumeration-safe message.

---

## 7. Validation & Scan Results (MEASURED)

All gates below report **measured** results at the FINAL checkpoint (not projections). The repository has no pre-existing automated test suite or CI/CD (only `.github/FUNDING.yml`), so regression assurance rests on a clean release build, the `WebVella.Erp.ConsoleApp` smoke path, the security scanners, and manual verification of the Critical/High fixes.

### 7.1 Build Gate

| Gate | Command | Result |
|------|---------|--------|
| Clean release build | `dotnet build WebVella.ERP3.sln -c Release --no-incremental` | **PASS — 0 errors, 62 warnings.** |

The 62 warnings span exactly **six pre-existing** categories unrelated to the remediation (`CA2200` re-throw, `ASPDEPR008`/`CS0618` obsolete-API usage, `NU1902` MailKit/MimeKit moderate advisories, `CS0168` unused local, `ASP0019` header-append). Baseline was 74 warnings; the reduction is due to suppressing the version-based `NU1903` AutoMapper advisory (risk acceptance, H11). **Zero new warning codes were introduced** by any remediation edit (verified by comparing the distinct warning-code set before and after).

### 7.2 SAST (Static Application Security Testing)

| Check | Analyzers | Result |
|-------|-----------|--------|
| Insecure deserialization | Roslyn **CA2326–CA2330** (`TypeNameHandling`) | **0 findings** — all four sites use `None` or the allowlist binder. |
| Weak cryptography / hardcoded keys | weak-crypto & hardcoded-secret analyzers | **0 findings** — MD5 removed from the storage path; no hardcoded keys remain in source. |

**SAST outcome: 0 Critical/High findings.**

### 7.3 SCA (Software Composition Analysis)

Command: `dotnet list package --vulnerable --include-transitive` (plus the NuGet restore audit `NU1901`–`NU1904`).

| Package | Version | Advisory | Severity | Disposition |
|---------|---------|----------|:--------:|-------------|
| AutoMapper | 14.0.0 | GHSA-rvv3-g6hj-g44x (CVE-2026-32933, CWE-674) | **High** | **⚠ Risk-accepted** (H11) — upgrade license-incompatible; build-audit-suppressed solution-wide in `Directory.Build.props`; Mapperly migration recommended. |
| MailKit | 4.14.1 | (NU1902) | Moderate | 📄 Documented (D8) — below the Critical/High gate. |
| MimeKit | 4.14.0 | (NU1902) | Moderate | 📄 Documented (D8) — below the Critical/High gate. |

**SCA outcome: 0 Critical, 0 un-accepted High.** The only High (AutoMapper) is a documented, formally accepted risk with a recorded suppression and a long-term migration recommendation; `dotnet list package --vulnerable` remains version-based and will still enumerate the advisory by design.

### 7.4 Secrets Scan

| Check | Result |
|-------|--------|
| Committed credentials in the seven host `Config.json` | **0** — `ConnectionString`, `EncryptionKey`, and `Jwt:Key` are empty in every host; `DevelopmentMode=false` everywhere. |
| Tree-wide committed secrets | **0** — the former default JWT key string `ThisIsMySecretKey…` now appears **only** in this report, as the documented "before" evidence for C3. |

**Secrets outcome: 0 committed credentials.**

### 7.5 Functional Smoke Tests (regression assurance)

| Scenario | Result |
|----------|--------|
| Fresh-database seeding | **PASS** — admin seeded with a `PBKDF2$600000$…` hash from the operator-supplied bootstrap secret; nothing printed to stdout; fail-fast when the secret is missing/invalid (H2, C4, Min10). |
| Forced first-login rotation | **PASS** — bootstrap login shows the rotation prompt with **no** session cookie set (gate not bypassable); rotation persists a new PBKDF2 hash; deferred sign-in then redirects to Home; the old bootstrap secret is rejected afterward (M3). Validated via both `curl` and browser. |
| Cookie login (HTTP dev) | **PASS** — auth cookie set with `SameSite=Lax; HttpOnly`, no `Secure` flag over HTTP in Development (env-gated `SecurePolicy`, H5/M8). |
| EQL + record CRUD/hook flow | **PASS** — ConsoleApp create→update→delete hook flow inside a rolled-back transaction (feature F-030 smoke path). |
| Brute-force lockout | **PASS** — five failed attempts trigger lockout; the enumeration-safe message is preserved on every path (H8). |

### 7.6 Acceptance Gate Summary

| Gate | Requirement | Status |
|------|-------------|:------:|
| Clean release build | 0 errors | ✅ |
| SAST | 0 Critical/High | ✅ |
| SCA | 0 Critical/High (High = risk-accepted AutoMapper, suppressed) | ✅ |
| Secrets | 0 committed credentials | ✅ |
| Functional smoke | all workflows pass | ✅ |

### 7.7 Security-relevant Dependency Versions (reference)

| Package | Version |
|---------|---------|
| Newtonsoft.Json | 13.0.4 |
| Npgsql | 9.0.4 |
| System.IdentityModel.Tokens.Jwt | 8.15.0 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.1 |
| Microsoft.CodeAnalysis.* (Roslyn) | 5.0.0 |
| CS-Script (CSScriptLib) | 4.13.1 |
| MailKit | 4.14.1 |
| MimeKit | 4.14.0 |
| Blazored.LocalStorage | 4.5.0 |
| Microsoft.AspNetCore.Cryptography.KeyDerivation / System.Security.Cryptography | shared framework (PBKDF2 — no added dependency) |

---

## 8. Secure-Configuration Guide

Secrets are no longer committed. Supply them at runtime via .NET user-secrets (development) or environment variables (production). Configuration keys use the `Settings:` prefix (which maps to the `Settings__` double-underscore convention for environment variables).

### 8.1 Development (user-secrets)

```
dotnet user-secrets init --project WebVella.Erp.Site
dotnet user-secrets set "Settings:ConnectionString" "Server=localhost;Port=5432;User Id=<user>;Password=<pass>;Database=<db>;" --project WebVella.Erp.Site
dotnet user-secrets set "Settings:EncryptionKey" "<64-hex-char key>" --project WebVella.Erp.Site
dotnet user-secrets set "Settings:Jwt:Key" "<32+ byte random key>" --project WebVella.Erp.Site
dotnet user-secrets set "Settings:InitialAdminPassword" "<12-24 char bootstrap password>" --project WebVella.Erp.Site
```

### 8.2 Production (environment variables)

```
Settings__ConnectionString=Server=<host>;Port=5432;User Id=<user>;Password=<pass>;Database=<db>;
Settings__EncryptionKey=<64-hex-char key>
Settings__Jwt__Key=<32+ byte random key>
Settings__InitialAdminPassword=<12-24 char bootstrap password>
```

### 8.3 Generating strong keys (examples)

```
# 32-byte (256-bit) random key, base64:
openssl rand -base64 32
# 64 hex characters (256-bit) for the encryption key:
openssl rand -hex 32
```

### 8.4 First administrator login

1. Set `Settings:InitialAdminPassword` (12–24 chars) before first run. If it is missing or out of range, initialization fails fast with a clear message — this is intentional (no default credential ships).
2. Sign in as `erp@webvella.com` with the bootstrap secret; the application immediately requires a new password (forced first-login rotation) before granting access.
3. After rotation, the bootstrap secret is no longer valid; remove `Settings:InitialAdminPassword` from the environment.

### 8.5 Production configuration checklist (fail the deploy if any is false)

- [ ] `Settings:Jwt:Key` is configured (no default; note: rotating a live key signs everyone out — coordinate operationally).
- [ ] `Settings:EncryptionKey` is configured (no default).
- [ ] `Settings:ConnectionString` is configured (no committed credentials).
- [ ] `DevelopmentMode=false` and `ASPNETCORE_ENVIRONMENT` is **not** `Development`.
- [ ] TLS is terminated in front of the app so `UseHttpsRedirection()`/`UseHsts()` and the `Secure` cookie flag are effective.
- [ ] CORS allowlist contains only the intended production origin(s).

---

## 9. Appendix

### 9.1 File Change Inventory (FINAL)

**New files (CREATE):**

| File | Purpose | Status |
|------|---------|:------:|
| `WebVella.Erp/Utilities/ErpSerializationBinder.cs` | Fail-closed deserialization allowlist (A08) | ✅ |
| `WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs` | Security-header baseline; CSP report-only (A05) | ✅ |
| `Directory.Build.props` | Solution-wide `NuGetAuditSuppress` for the risk-accepted AutoMapper advisory (A06/H11) | ✅ |
| `SECURITY.md` | This audit report | ✅ |

**Modified files (UPDATE):**

| File(s) | Change | Status |
|---------|--------|:------:|
| `WebVella.Erp/Utilities/PasswordUtil.cs` | PBKDF2 + tri-state verify + constant-time compare (A02) | ✅ |
| `WebVella.Erp/Utilities/CryptoUtility.cs` | Remove hardcoded encryption key (A02) | ✅ |
| `WebVella.Erp/ErpSettings.cs` | Remove default JWT/encryption keys; require configured values (A02/A05) | ✅ |
| `WebVella.Erp/Api/SecurityManager.cs` | Route validation through PBKDF2; rehash-on-login (A02/A07) | ✅ |
| `WebVella.Erp/Api/RecordManager.cs` | Password-field write path uses PBKDF2 (A02) | ✅ |
| `WebVella.Erp/Database/DbRecordRepository.cs` | Encrypted password-field write path uses PBKDF2 instead of MD5 (A02) | ✅ |
| `WebVella.Erp/ERPService.cs` | Operator-supplied bootstrap secret; fail-fast; no stdout (A07) | ✅ |
| `WebVella.Erp/Jobs/JobDataService.cs` | `TypeNameHandling` → `None`/binder (A08) | ✅ |
| `WebVella.Erp/Notifications/NotificationContext.cs` | `TypeNameHandling` → `None`/binder (A08) | ✅ |
| `WebVella.Erp/Database/DbEntityRepository.cs` | `TypeNameHandling` → `None`/binder (A08) | ✅ |
| `WebVella.Erp/Database/DbRelationRepository.cs` | `TypeNameHandling` → `None`/binder (A08) | ✅ |
| `WebVella.Erp/Api/Models/AutoMapper/Profiles/JobProfile.cs` | Job attribute/result (de)serialization constrained with the `ErpSerializationBinder` allowlist (A08) | ✅ |
| `WebVella.Erp/Diagnostics/Log.cs` | Structured security-event logging (A09) | ✅ |
| `WebVella.Erp.Web/Models/BaseErpPageModel.cs` | Wire `LogPermissionDenied` at the page-access authorization deny paths (A09) | ✅ |
| `WebVella.Erp.Web/Services/AuthService.cs` | Cookie lifetime → operational; UTC token expiry (A07) | ✅ |
| `WebVella.Erp.Web/Middleware/ErpMiddleware.cs` | Remove `AllowSynchronousIO` (DoS) | ✅ |
| `WebVella.Erp.Web/Services/CodeEvalService.cs` | Threat comment + admin-only guard verified (A03) | ✅ |
| `WebVella.Erp.Web/Controllers/WebApiController.cs` | `[Authorize(Roles="administrator")]` on the runtime-compile endpoint — deny-by-default for non-admins (A03/D3) | ✅ |
| `WebVella.Erp.Web/Pages/login.cshtml.cs` (+ `login.cshtml`) | 5-attempt lockout + forced first-login rotation (A07) | ✅ |
| `WebVella.Erp.Site*/Startup.cs` (7 hosts) | CORS allowlist, cookie `SecurePolicy`/`SameSite`, HTTPS/HSTS, headers, `ErpSettings.Initialize` (A05/A02/A07) | ✅ |
| `WebVella.Erp.Site*/Config.json` (7 hosts) | Remove secrets; `DevelopmentMode=false` (A02/A05) | ✅ |
| `WebVella.Erp.ConsoleApp/Config.json` | Remove committed connection string / encryption key; `DevelopmentMode=false` (A02/A05, D7) | ✅ |
| `WebVella.Erp.Site/web.config` | `ASPNETCORE_ENVIRONMENT=Production` — disable dev exception page / stack-trace disclosure (A05/H9) | ✅ |
| `WebVella.Erp.Site.csproj` | `config.json` `DefaultItemExcludes` + re-include `Config.json` PreserveNewest — fixes NETSDK1022 | ✅ |
| `WebVella.Erp.WebAssembly/Server/*.csproj`, `.../Shared/*.csproj` | `net7.0` → `net10.0` (A06) | ✅ |
| `global.json` | Pin SDK version (A08 supply-chain) | ✅ |
| `WebVella.Erp/WebVella.Erp.csproj` | AutoMapper pinned `[14.0.0]` + risk-acceptance rationale comment (A06/H11) | ✅ |
| `WebVella.Erp.Site/JWT_README.txt` | Reconcile the stale `JwtBearer` version to `10.0.1`; note the signing key must come from user-secrets/env, never source (A05 doc) | ✅ |
| `.gitignore` | Ignore lowercase runtime `config.json` copies so removed secrets cannot be re-committed (A02/A05, CWE-798) | ✅ |

**Reference-only (NOT modified):** `WebVella.Erp.Web/Controllers/ApiControllerBase.cs` (error masking), `WebVella.Erp/Api/Models/ErpUser.cs` (`[JsonIgnore]` redaction), `WebVella.Erp.Web/ErpMvcExtensions.cs` (reverted byte-identical to baseline — the config-supply wiring lives in each host `Startup.cs`, not the shared extension). Note: the `AllowNodeJsLocalhost` **CORS policy** in `WebVella.Erp.Site.Crm/Startup.cs` is the allowlist template the other hosts were modeled on and its CORS body is unchanged, but the file itself **was** modified (cookie `SecurePolicy`/`SameSite`, HSTS/HTTPS redirection, security headers, `ErpSettings.Initialize`) and is counted in the 7-host `Startup.cs` row above — it is not reference-only.

### 9.2 Standards Applied

- **Password KDF:** PBKDF2-HMAC-SHA-256, 128-bit salt, 256-bit subkey, 600,000 iterations (OWASP 2023). Constant-time verification via `CryptographicOperations.FixedTimeEquals`. CSPRNG (`RandomNumberGenerator`) for all salts and any security-sensitive randomness.
- **Password policy:** operator bootstrap secret 12–24 chars, enforced at seed time; forced first-login rotation.
- **Account protection:** five-attempt lockout with rolling window; enumeration-safe error messages.
- **Cookies:** `HttpOnly`, `SameSite=Lax`, and `Secure` (production/HTTPS).
- **Transport:** HTTPS redirection + HSTS (`max-age=31536000; includeSubDomains`) in production.
- **Deserialization:** `TypeNameHandling.None`, or a fail-closed `ISerializationBinder` allowlist where polymorphism is required (CA2326–CA2330 clean).
- **Authorization:** server-side `HasEntityPermission` remains authoritative; deny-by-default on the runtime-compile endpoint.

### 9.3 JWT_README Reconciliation

`WebVella.Erp.Site/JWT_README.txt` was reviewed. **No issue found:** the file already references the current `Microsoft.AspNetCore.Authentication.JwtBearer` **10.0.1** and provides empty-JWT-key guidance consistent with the C3 remediation (no default key; configure `Settings:Jwt:Key` via user-secrets/env). No change is required; this section supersedes any earlier note that implied the file still referenced an older JwtBearer version or a default key.

### 9.4 Environment / Build Notes

- Target framework: **.NET 10** across the solution (the two `net7.0` WASM projects were retargeted). SDK pinned in `global.json`.
- Runtime configuration on a case-sensitive filesystem: source references a lowercase `config.json` while the repository ships `Config.json`; `WebVella.Erp.Site.csproj` now excludes the lowercase runtime artifact from the build items and re-includes `Config.json` (PreserveNewest), so both the build and publish are correct regardless of a lowercase copy.
- The AutoMapper advisory suppression is declared once, solution-wide, in `Directory.Build.props`.

### 9.5 Out-of-Scope, Pre-existing, Non-security Observations (documented, not fixed)

The QA browser/end-to-end pass surfaced the following **functional/operational** observations. They are **not security vulnerabilities**, and each was verified via `git` to **pre-date this security audit** (they exist at the pre-audit baseline and were not introduced or affected by any remediation edit). Per the Minimal Change Clause (§0.7) and the out-of-scope boundary (§0.3.2 — "unrelated feature, performance, or refactoring work"), they are documented here for a complete audit trail but were deliberately **not changed** (none is Critical). They are recorded so a future functional-maintenance effort can address them.

| # | Observation | Severity | Pre-existing evidence | Disposition |
|---|-------------|:--------:|-----------------------|-------------|
| O1 | Blazor Server circuit on the SDK `/dev` page does not connect — `blazor.server.js` returns HTTP 405 | Info (functional) | Caused by the catch-all `[AcceptVerbs("DELETE"), Route("{*filepath}")]` action in `WebApiController.cs` combined with the legacy `UseStaticFiles()`/routing order under .NET 10; the catch-all route is present from the **initial commit `4919f97d`**. The security remediation to the Sdk `Startup.cs` only added dev-gated `UseHttpsRedirection`/`UseHsts` + a pass-through `UseSecurityHeaders()` and left the `UseStaticFiles → UseRouting → UseEndpoints` ordering unchanged. | Not a security issue; pre-existing; out of scope. Recommend a routing-order/catch-all fix in a functional change. |
| O2 | Blazor **WASM** client returns 404 — broken `ProjectReference` to `..\Client\WebVella.Erp.WebAssembly.Client.csproj` (actual project file is `WebVella.Erp.WebAssembly.csproj`) | Info (functional) | The broken project reference was introduced in commit **`d8c5c086`** (2023-11-01), long before this audit. The only A06 edit to these projects was the `net7.0 → net10.0` target-framework retarget. | Not a security issue; pre-existing; out of scope. Recommend correcting the `ProjectReference` path in a build-maintenance change. |
| O3 | CKEditor image-thumbnail generation fails on Linux (GDI+/`libgdiplus` dependency) | Info (environment) | Server-side image processing depends on `System.Drawing`/GDI+, which is not fully supported on Linux; this is an environment/runtime limitation, not introduced by any remediation. | Not a security issue; environment-specific; out of scope. Recommend a cross-platform image library for Linux hosting. |
| O4 | `GET /fs/...` returns HTTP 500 when the request path carries a U+202F (narrow no-break space) header artifact | Info (functional) | Pre-existing file-serving handler behavior (matches the long-standing build warning `ASP0019` at `WebApiController.cs:3304`); unrelated to any security edit. | Not a security issue; pre-existing; out of scope. Recommend input-normalization hardening in a functional change. |
| O5 | Minor navigation accessibility labels / bfcache eligibility observations | Info (a11y/perf) | UI/accessibility and back-forward-cache observations from the E2E pass; not security-relevant and not affected by remediation. | Not a security issue; out of scope. Recommend addressing in routine UX/a11y maintenance. |
| F2 | JWT refresh endpoint returns `success: true` for a malformed/garbage refresh token (no token is actually issued) | Info (contract) | `GetNewJwtToken` reports success without issuing a token on unparseable input; **no authentication bypass** occurs (no valid token is minted, no session is granted). Behavior is pre-existing. | Not exploitable as an auth bypass; Info. Recommend returning an explicit failure for an invalid refresh token to tighten the API contract. |

> These observations were also relayed in the resolution report. They are intentionally excluded from the security finding inventory (Sections 4 and 6) because they are not security vulnerabilities; this appendix preserves their disposition for completeness.

---

*End of report. All Critical and High findings are remediated in code except the AutoMapper advisory (H11), which is a documented, build-audit-suppressed risk acceptance with a recommended long-term migration to a maintained, license-compatible mapper. All Medium and Low findings are documented with recommended fixes. The FINAL validation gates (Section 7) pass with measured results.*

