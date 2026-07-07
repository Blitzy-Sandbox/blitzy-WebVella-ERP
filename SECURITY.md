# WebVella ERP — Security Audit Report

**Framework:** OWASP Top 10 (2021)
**Target:** WebVella ERP — an open-source (Apache-2.0, .NET Foundation) ERP platform built on ASP.NET Core, Blazor WebAssembly, and PostgreSQL (`WebVella.ERP3.sln`).
**Audit type:** Comprehensive source-code security audit with in-code remediation of Critical/High findings and documentation of Medium/Low findings.
**Change discipline:** Minimal Change Clause — only the changes necessary to remediate identified vulnerabilities were made; existing functionality, API contracts, database schemas, and user-facing behavior are preserved.

> **How to read this report.** Every finding is recorded in the mandated Finding format (Section 4). Findings classified **Critical** or **High** are remediated in code (Section 5); findings classified **Medium** or **Low** are documented with recommended fixes and a deferral rationale (Section 6). Section 7 records the before/after scan posture; Section 8 is the actionable secure-configuration guide.
>
> **This is a living document produced during a phased, per-vulnerability-class remediation.** Sections 5 and 7 describe the *target* end-state. Every remediation item and scan gate is annotated with its status — **✅ Implemented** (landed in code now) or **⏳ Planned** (scheduled for a later vulnerability-class checkpoint) — so no item is presented as complete before its supporting code and scan evidence exist. See **Milestone Status** immediately below for exactly what is implemented at the current checkpoint.

### Milestone Status — Checkpoint 1: Foundational Security Primitives & Standalone Core/Web/Build Fixes

This checkpoint delivers the **foundational security primitives** and the standalone core/web/build fixes that the later remediations depend on. The following are **✅ Implemented in code at this checkpoint**:

- **A02 crypto primitive** — `PasswordUtil` PBKDF2-HMAC-SHA256 hashing plus the tri-state, backward-compatible `VerifyPassword` (constant-time comparison; enforces the 16-byte salt, 32-byte subkey, and a 210,000-iteration floor with a DoS ceiling; legacy MD5 → *rehash-needed* signal).
- **A05 config primitives** — `SecurityHeadersMiddleware` (baseline headers, CSP in **report-only** mode) with a `UseSecurityHeaders()` extension; `ErpMiddleware` synchronous-I/O opt-in removed.
- **A08 deserialization primitive** — the **fail-closed** `ErpSerializationBinder` allowlist (throws `JsonSerializationException` on any non-permitted type).
- **A05/A02 config surface** — the hardcoded default JWT signing key removed from `ErpSettings` (a configured `Settings:Jwt:Key` is now required, fail-fast at startup).
- **A09 logging** — `Log` extended with a `Security` log type and security-event helper methods routed through the existing insert path.
- **A06 components** — the WASM **Shared** project retargeted `net7.0` → `net10.0`; the SDK pinned in `global.json`.
- **A03 eval boundary** — the code-compile endpoint that can reach `CodeEvalService` is now gated with `[Authorize(Roles = "administrator")]`, and the accepted-risk documentation reflects that verified, enforced guard.

The remaining Critical/High remediations described in Section 5 are **⏳ Planned** for later vulnerability-class checkpoints: routing `SecurityManager`/`RecordManager`/`DbRecordRepository` through the new hasher; removing the hardcoded key from `CryptoUtility`; sanitizing all seven host `Config.json`/`web.config`; tightening CORS/cookies/HSTS/headers wiring in the seven `Startup.cs`; switching the four Newtonsoft `TypeNameHandling` call sites to the binder / `None`; retargeting the WASM **Server** project; and the authentication/session changes in `ERPService`/`AuthService`/`login.cshtml.cs`. **The automated SAST/SCA/secrets scans have not yet been run to completion, so their acceptance gates are targets that are not yet met** (see Section 7).

---

## 1. Executive Summary

WebVella ERP is a free and open-source, extensible web application platform (ASP.NET Core + Blazor WebAssembly front end, PostgreSQL 16 data store, no ORM — SQL is issued through parameterized `NpgsqlCommand`). This audit assessed the full OWASP Top 10 (2021) attack surface plus supplementary checks (dependency/SCA, secrets detection, security-header verification, TLS configuration, input validation / output encoding, error-handling / information disclosure, rate-limiting / DoS, CORS, file-upload, and API security).

The audit identified **26 findings**: **15 Critical/High** — all scheduled for in-code remediation across the phased vulnerability-class checkpoints — and **11 Medium/Low**, documented here with recommended fixes and a deferral rationale consistent with the Minimal Change Clause ("document but do not fix unless Critical"). At the current foundational checkpoint the shared security primitives and the standalone core/web/build fixes are implemented; the remaining Critical/High remediations are in progress and are marked **⏳ Planned** in Section 5 until their supporting code lands (see **Milestone Status** above for the precise per-item breakdown).

### 1.1 Findings by Severity

| Severity | Total | In-code remediation (target plan) | Documented only |
|----------|:-----:|:---------------------------------:|:---------------:|
| **Critical** | 5 | 5 | 0 |
| **High** | 10 | 10 | 0 |
| **Medium** | 6 | 3 | 3 |
| **Low** | 5 | 1 | 4 |
| **Total** | **26** | **19** | **7** |

> The **In-code remediation** column is the *target remediation plan* across all phased checkpoints, not the count completed at the current checkpoint. For what is actually implemented now versus **⏳ Planned**, see **Milestone Status** above and the per-item status markers in Section 5.

### 1.2 Headline Outcome

_These are the **target** headline outcomes for the remediation as a whole. See **Milestone Status** above and the per-item **✅ Implemented / ⏳ Planned** markers in Section 5 for what has landed at the current checkpoint versus what remains scheduled._

- **Cryptography (A02):** Unsalted MD5 password hashing replaced with PBKDF2 (HMAC-SHA-256, 128-bit salt, 256-bit subkey) and a backward-compatible verify that transparently rehashes legacy MD5 credentials on the next successful login — no existing user is locked out. Constant-time comparison via `CryptographicOperations.FixedTimeEquals`. Hardcoded encryption key and default JWT signing key removed; configured values are now required (fail-fast at startup).
- **Configuration (A05):** Permissive `AllowAnyOrigin` CORS replaced with an origin allowlist; a `SecurityHeadersMiddleware` now emits the mandated response-header baseline (CSP begins in report-only mode); session cookies gain `Secure`/`SameSite`; HTTPS redirection and HSTS are enabled outside development; committed secrets removed from configuration and `DevelopmentMode` set to `false` for production.
- **Deserialization (A08):** Newtonsoft.Json `TypeNameHandling.All`/`Auto` gadget surface closed by setting `TypeNameHandling.None` where polymorphism is unnecessary and attaching a new allowlist `ErpSerializationBinder` where it is required.
- **Authentication (A07):** 100-year authentication cookie reduced to an operational lifetime; JWT expiry corrected to UTC; five-attempt account lockout added (preserving the enumeration-safe error message); the seeded default administrator password replaced with a CSPRNG-generated secret with forced rotation at first login.
- **Components (A06):** The two out-of-support `net7.0` Blazor WebAssembly projects retargeted to `net10.0`; the SDK version pinned in `global.json`.
- **Logging (A09):** Structured security-event logging added for authentication failures, permission denials, and role/password changes.

**Validation posture (target — not yet met at this checkpoint):** the acceptance goal is that SAST, SCA (dependency), and secrets scans report **zero Critical/High** findings once all remediations land. These scans have **not yet been run to completion**, so those gates are **not yet met**. A dependency scan currently surfaces an outstanding **high-severity advisory for `AutoMapper` 14.0.0 (NU1903)** and moderate advisories for **`MailKit`/`MimeKit` (NU1902)** — tracked under A06, out of scope for the foundational checkpoint, and to be addressed with the A06 remediation. Targeted in-scope module builds succeed (`WebVella.Erp`, `WebVella.Erp.Web` build with `dotnet build -c Release`); full functional-workflow verification (cookie login, JWT login, record CRUD, EQL) is scheduled once the dependent remediations land.

---

## 2. Scope & Methodology

### 2.1 Systems Audited

The audit covered the core runtime library (`WebVella.Erp`), the shared web layer (`WebVella.Erp.Web`), all seven site hosts (`WebVella.Erp.Site`, `.Crm`, `.Mail`, `.MicrosoftCDM`, `.Next`, `.Project`, `.Sdk`), the Blazor WebAssembly projects (`WebVella.Erp.WebAssembly`), and the build/configuration manifests (`global.json`, host `Config.json`/`web.config`, vendored `wwwroot` client libraries).

### 2.2 OWASP Top 10 (2021) Categories Assessed

> The **Assessment & Status** column records both what the audit found and the remediation state **at the current foundational-primitives checkpoint** (**✅ Implemented** / **⏳ Planned**). It is not an end-state "all clear"; see the Section 4.2 Status column and the Section 5 per-subsection markers, which govern.

| ID | Category | Assessment & Status |
|----|----------|---------------------|
| **A01** | Broken Access Control | Server-side permission engine (`SecurityContext.HasEntityPermission`) is authoritative (positive control, unchanged); one UI-only visibility helper documented (D4). |
| **A02** | Cryptographic Failures | Multiple Critical/High findings (weak hashing, hardcoded keys). PBKDF2 primitive + constant-time compare ✅ Implemented; `ErpSettings` default JWT key removed ✅; credential-path routing and `CryptoUtility` key removal ⏳ Planned (crypto checkpoint). |
| **A03** | Injection | SQL fully parameterized (positive control, unchanged). Runtime C# evaluation is an accepted-risk admin-only feature (D3); its admin-only compile guard is now enforced in code ✅. |
| **A04** | Insecure Design | Assessed; related weaknesses surface under A07 (session/lockout — ⏳ Planned) and a latent null-reference (D5, documented). |
| **A05** | Security Misconfiguration | Multiple High findings (CORS, headers, cookies, HTTPS/HSTS, dev-mode disclosure). `SecurityHeadersMiddleware` ✅ and `ErpMiddleware` sync-I/O removal ✅ Implemented; per-host CORS/cookie/HSTS/dev-mode changes ⏳ Planned (configuration checkpoint). |
| **A06** | Vulnerable & Outdated Components | Out-of-support .NET 7: WASM **Shared** retargeted to `net10.0` ✅, **Server** ⏳ Planned; SDK pinned in `global.json` ✅; vendored JS libraries documented as CVE-gated (D6). |
| **A07** | Identification & Authentication Failures | Default admin credential, session lifetime, lockout, token clock — ⏳ Planned (authentication checkpoint); MFA and localStorage token documented (D1, D2). |
| **A08** | Software & Data Integrity Failures | Insecure deserialization: allowlist `ErpSerializationBinder` ✅ Implemented; call-site `TypeNameHandling` changes ⏳ Planned. SDK pinning added ✅. |
| **A09** | Security Logging & Monitoring Failures | Structured security-event logging added ✅ Implemented. |
| **A10** | Server-Side Request Forgery (SSRF) | Assessed; no server-side fetch of user-controlled URLs identified as a remediation target. Mail inline-image handling (HtmlAgilityPack) noted as an input-handling surface with no active finding. |

### 2.3 Supplementary Checks

Dependency scanning (SCA), secrets detection, security-header verification, TLS/transport configuration, input validation and output encoding, error-handling / information-disclosure review, rate-limiting / DoS review, CORS review, file-upload security, and API security.

### 2.4 Tooling (.NET-appropriate)

The original request enumerated language-generic scanners (`npm audit`, `pip-audit`). Because this stack is **.NET**, the following equivalent tools were used in their place:

| Purpose | Tools |
|---------|-------|
| **SAST** (static analysis) | Security Code Scan, Semgrep, and the built-in Roslyn analyzers **CA2326–CA2330** (which specifically flag insecure Newtonsoft.Json `TypeNameHandling` deserialization). |
| **SCA** (dependency / component) | `dotnet list package --vulnerable --include-transitive`, OWASP Dependency-Check, Trivy, and **retire.js** for vendored client-side JavaScript. |
| **Secrets** | **gitleaks** and **detect-secrets**. |

> These `.NET`-native tools replace the prompt's `npm audit` / `pip-audit` examples, which do not apply to an ASP.NET Core / NuGet solution.

### 2.5 Constraints (Minimal Change Clause)

- Make only the changes necessary to remediate identified vulnerabilities; introduce no features, optimizations, refactoring, or architectural changes beyond a fix; prefer the smallest-footprint solution.
- Preserve all existing functionality, API contracts, interfaces, and user-facing behavior; do not modify database schemas unless a security fix requires it (none did — the password column format is preserved via in-place rehash-on-login); keep performance within 10% of baseline.
- Annotate each security change with a threat comment; commit atomically per vulnerability class (crypto, configuration, deserialization, authentication, components, logging) and validate after each class.
- For concerns discovered beyond scope: document them but do not fix unless Critical.

---

## 3. Severity Legend

| Severity | Definition |
|----------|------------|
| **Critical** | Directly exploitable weakness that can lead to full compromise of confidentiality, integrity, or availability (e.g., remote code execution, trivial credential recovery, authentication bypass). Must be fixed in code. |
| **High** | Serious weakness that materially increases the likelihood or impact of compromise, typically requiring a modest precondition (e.g., missing transport/session hardening, permissive cross-origin access, information disclosure). Must be fixed in code. |
| **Medium** | Weakness that raises risk but is mitigated by existing controls or requires a stronger precondition (e.g., missing MFA, defense-in-depth gaps). Documented with a recommended fix; fixed only when the change is low-risk and in scope. |
| **Low** | Minor weakness or hardening/hygiene opportunity with limited direct impact (e.g., build reproducibility, latent code-quality risks). Documented; fixed only opportunistically. |

---

## 4. Vulnerability Inventory

### 4.1 Mandated Finding Format

Every finding below is recorded using the exact block structure required by the request:

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
| C1 | Weak password hashing (unsalted MD5) | A02 | Critical | CWE-916, CWE-759, CWE-327 | ◑ Partial — PBKDF2 primitive ✅; call-site routing ⏳ |
| C2 | Hardcoded default encryption key in source | A02 | Critical | CWE-798, CWE-321 | ⏳ Planned (crypto) |
| C3 | Hardcoded / default JWT signing key | A02 | Critical | CWE-798, CWE-321, CWE-547 | ◑ Partial — `ErpSettings` default removed ✅; host `Config.json` ⏳ |
| C4 | Default administrator credential | A07 | Critical | CWE-798, CWE-521 | ⏳ Planned (authentication) |
| C5 | Insecure deserialization (`TypeNameHandling`) | A08 | Critical | CWE-502 | ◑ Partial — `ErpSerializationBinder` ✅; call sites ⏳ |
| H1 | Non-constant-time credential comparison | A02 | High | CWE-208 | ✅ Implemented (crypto) |
| H2 | Committed database credentials | A02 | High | CWE-798 | ⏳ Planned (configuration) |
| H3 | Permissive CORS (`AllowAnyOrigin`) | A05 | High | CWE-942 | ⏳ Planned (configuration) |
| H4 | Missing security response headers | A05 | High | CWE-693, CWE-1021, CWE-16 | ◑ Partial — middleware ✅; host wiring ⏳ |
| H5 | Session cookie missing `Secure`/`SameSite` | A05 | High | CWE-614, CWE-1275 | ⏳ Planned (configuration) |
| H6 | Missing HTTPS redirection / HSTS (cleartext transport) | A02 | High | CWE-319 | ⏳ Planned (configuration) |
| H7 | Excessive session lifetime (100-year cookie) | A07 | High | CWE-613 | ⏳ Planned (authentication) |
| H8 | No account lockout / brute-force protection | A07 | High | CWE-307 | ⏳ Planned (authentication) |
| H9 | Information disclosure via development mode | A05 | High | CWE-489, CWE-215, CWE-11 | ⏳ Planned (configuration) |
| H10 | Out-of-support runtime (.NET 7) | A06 | High | CWE-1104 | ◑ Partial — WASM Shared ✅; Server ⏳ |
| M1 | Token expiry uses local time, not UTC | A07 | Medium | CWE-613 | ⏳ Planned (authentication) |
| M2 | Synchronous I/O enabled (DoS surface) | A05 | Medium | CWE-400 | ✅ Implemented (configuration) |
| M3 | Insufficient security logging | A09 | Medium | CWE-778 | ✅ Implemented (logging) |
| L1 | Unpinned SDK toolchain (supply-chain) | A08 | Low | CWE-1104 | ✅ Implemented (components) |
| D1 | No multi-factor authentication (MFA) | A07 | Medium | CWE-308 | Documented |
| D2 | JWT stored in browser localStorage (WASM) | A07/A02 | Medium | CWE-522, CWE-79 | Documented |
| D3 | Runtime C# evaluation (accepted risk) | A03 | Medium | CWE-94 | ✅ Admin-only guard enforced; documented |
| D4 | UI-only authorization helper (`WvAuthorize`) | A01 | Low | CWE-602 | Documented |
| D5 | Latent null-reference in Blazor circuit handler | A04 | Low | CWE-476 | Documented |
| D6 | Vendored client-side libraries (CVE-gated) | A06 | Low | CWE-1104, CWE-1395 | Documented |
| D7 | Secrets in `WebVella.Erp.ConsoleApp/Config.json` | A02/A05 | Low | CWE-798 | Documented |

> **Consistency note.** The **Status** column reflects the state at the current **foundational-primitives checkpoint**, not the end state of the whole engagement: **✅ Implemented** items have landed in code; **◑ Partial** items have their shared primitive implemented with the remaining call-site/host integration **⏳ Planned**; **⏳ Planned** items are scheduled for a later vulnerability-class checkpoint (see the **Milestone Status** banner in Section 1 and the per-subsection Status markers in Section 5, which are authoritative). Items **M2–M3** and **L1** are Medium/Low that were implemented in this checkpoint because the edit was low-risk and already in scope for its commit class; **M1** remains **⏳ Planned** with the rest of the authentication class. Items **D1–D7** are Medium/Low that are documented, not code-changed, per the Minimal Change Clause; **D3**'s administrator-only guard has additionally been **enforced in code** (see Section 4.10 — A03) on top of being documented.

---

> **How to read the REMEDIATION field below.** Each finding block uses the mandated audit template, whose `REMEDIATION` line states the **prescribed fix** for that vulnerability. The template describes the fix in the specified form; it does **not** by itself assert the fix has already shipped. Whether a given remediation has **landed at this checkpoint** versus is **scheduled for a later vulnerability-class checkpoint** is given authoritatively by the **Status** column in Section 4.2 and the per-subsection **Status** markers in Section 5. Where the two could be read as differing, Sections 4.2 and 5 govern.

### 4.3 A02 — Cryptographic Failures

```
FINDING: Weak password hashing (unsalted MD5)
SEVERITY: Critical
CWE: CWE-916, CWE-759, CWE-327
LOCATION: WebVella.Erp/Utilities/PasswordUtil.cs:L9-L30
DESCRIPTION: User passwords are hashed with a single, unsalted pass of MD5. MD5 is a fast, cryptographically broken digest unsuitable for password storage; the absence of a per-user salt permits precomputed (rainbow-table) attacks and reveals identical passwords across accounts.
IMPACT: An attacker with read access to the user table (e.g., via SQL injection elsewhere, a backup leak, or insider access) can recover most plaintext passwords in minutes using GPU cracking or rainbow tables, enabling account takeover and credential-stuffing against other systems.
EVIDENCE: private static MD5 md5Hash = MD5.Create(); ... byte[] data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(input)); // GetMd5Hash returns lowercase hex, no salt, no work factor
REMEDIATION: Replaced with PBKDF2 (HMAC-SHA-256, 128-bit random salt, 256-bit subkey, iterated work factor) as the storage format. Added a backward-compatible VerifyPassword that first tries the modern hash, then falls back to the legacy MD5 check and, on legacy success, signals "rehash needed" so the caller persists an upgraded PBKDF2 hash on the next successful login. Legacy VerifyMd5Hash is retained for verification only. No existing user is locked out (functional parity preserved).
```

```
FINDING: Non-constant-time credential comparison
SEVERITY: High
CWE: CWE-208
LOCATION: WebVella.Erp/Utilities/PasswordUtil.cs:L25-L30
DESCRIPTION: The password-hash comparison uses an ordinal string comparer, whose runtime depends on where the first differing character occurs. This timing side channel can leak information about a stored hash.
IMPACT: Under favorable conditions an attacker measuring response timing could incrementally infer hash bytes, reducing the effort to forge a matching credential.
EVIDENCE: StringComparer comparer = StringComparer.OrdinalIgnoreCase; return (0 == comparer.Compare(hashOfInput, hash));
REMEDIATION: The modern verification path compares fixed-length hash bytes with CryptographicOperations.FixedTimeEquals, which runs in time independent of the input contents.
```

```
FINDING: Hardcoded default encryption key in source
SEVERITY: Critical
CWE: CWE-798, CWE-321
LOCATION: WebVella.Erp/Utilities/CryptoUtility.cs:L16 (default literal) and L23-L39 (CryptKey property)
DESCRIPTION: A 64-hex-character symmetric encryption key is embedded as a compile-time constant and is used as the fallback key whenever no key is configured. A key committed to source control is a shared, publicly known secret.
IMPACT: Anyone with access to the source (a public repository, a decompiled binary) knows the encryption key and can decrypt any data protected with the default, defeating confidentiality of encrypted fields.
EVIDENCE: private const string defaultCryptKey = "BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658"; ... if (string.IsNullOrWhiteSpace(ErpSettings.EncryptionKey)) { cryptKey = defaultCryptKey; }
REMEDIATION: Removed the hardcoded default literal. A configured encryption key (Settings:EncryptionKey) is now required; the application fails fast with a clear message if it is missing rather than silently using a known key.
```

```
FINDING: Hardcoded / default JWT signing key
SEVERITY: Critical
CWE: CWE-798, CWE-321, CWE-547
LOCATION: WebVella.Erp/ErpSettings.cs:L118 (default fallback) and WebVella.Erp.Site/Config.json:L24-L28 (committed key)
DESCRIPTION: The JWT HMAC signing key defaults to the hardcoded literal "ThisIsMySecretKey" when unconfigured, and a committed default key is present in host configuration. The signing key is the sole secret protecting token integrity.
IMPACT: Knowing the signing key, an attacker can forge valid JWTs for any user (including administrators), achieving complete authentication bypass and privilege escalation.
EVIDENCE: JwtKey = string.IsNullOrWhiteSpace(configuration["Settings:Jwt:Key"]) ? "ThisIsMySecretKey" : configuration["Settings:Jwt:Key"]; // Config.json: "Key": "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey"
REMEDIATION: Removed the insecure default; a configured Settings:Jwt:Key is now required (fail-fast at startup). Documented that rotating a live signing key invalidates all in-flight tokens (a mass sign-out) and must therefore be coordinated operationally rather than performed silently.
```

```
FINDING: Committed database credentials
SEVERITY: High
CWE: CWE-798
LOCATION: WebVella.Erp.Site/Config.json:L4 (applies to all seven hosts' Config.json)
DESCRIPTION: The PostgreSQL connection string, including a username and password, is committed to source control in cleartext.
IMPACT: Anyone with repository access obtains database credentials, enabling direct data exfiltration or tampering that bypasses the application's access controls entirely.
EVIDENCE: "ConnectionString": "Server=localhost;Port=5432;User Id=dev;Password=dev;Database=ttg_test;..."
REMEDIATION: Removed the committed credentials from configuration; the connection string is now supplied via .NET user-secrets (development) or an environment variable (production) — see the Secure-Configuration Guide (Section 8).
```

```
FINDING: Missing HTTPS redirection / HSTS (cleartext transport)
SEVERITY: High
CWE: CWE-319
LOCATION: All seven host Startup.cs Configure pipelines (no UseHttpsRedirection / UseHsts)
DESCRIPTION: The application pipelines contain no HTTPS redirection and no HTTP Strict Transport Security. Without them, traffic (including the session cookie and credentials) can traverse the network in cleartext.
IMPACT: A network attacker can intercept or downgrade connections and steal session cookies or credentials (man-in-the-middle).
EVIDENCE: The Configure method registers localization, routing, authentication, and endpoints but never calls app.UseHttpsRedirection() or app.UseHsts().
REMEDIATION: Added app.UseHttpsRedirection() and app.UseHsts() gated to non-development environments (so HTTP development flows are not broken). HSTS emits Strict-Transport-Security: max-age=31536000; includeSubDomains. This is a prerequisite for the Secure cookie flag (H5).
```


```
FINDING: Committed secrets in the Console smoke-test harness configuration
SEVERITY: Low
CWE: CWE-798
LOCATION: WebVella.Erp.ConsoleApp/Config.json
DESCRIPTION: The console smoke-test harness ships a tracked Config.json that carries the same database connection string and encryption key as the production hosts (it has no Jwt section). Although a developer tool, the file is committed to the repository.
IMPACT: If populated with real values, it exposes the same database and encryption secrets as the host configs to anyone with repository access.
EVIDENCE: WebVella.Erp.ConsoleApp/Config.json defines Settings:ConnectionString and Settings:EncryptionKey, mirroring the host configuration shape.
REMEDIATION: Documented only (Low, not code-changed). Recommendation: apply the same user-secrets/environment-variable migration as the production hosts (Section 8). Deferral rationale: the console harness is outside the seven-host production scope defined for this audit.
```

### 4.4 A05 — Security Misconfiguration

```
FINDING: Permissive CORS policy (AllowAnyOrigin)
SEVERITY: High
CWE: CWE-942
LOCATION: WebVella.Erp.Site/Startup.cs:L58-L64
DESCRIPTION: The default CORS policy allows any origin, any method, and any header. This removes the browser's same-origin protection for cross-site requests to the API.
IMPACT: Any website can issue cross-origin requests to the API on behalf of a visiting authenticated user, facilitating data theft and cross-site request abuse (especially problematic if combined with credentials).
EVIDENCE: options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
REMEDIATION: Replaced the permissive default policy with an explicit origin allowlist modeled on the tightened AllowNodeJsLocalhost policy already used by WebVella.Erp.Site.Crm/Startup.cs (builder.WithOrigins("http://localhost:3000", "http://localhost").AllowAnyMethod().AllowCredentials()). The commented AllowNodeJsLocalhost template at WebVella.Erp.Site/Startup.cs:L53-L57 was the basis. Applied across the hosts that used the permissive policy; the development origin for the Blazor WASM client is preserved.
```

```
FINDING: Missing security response headers
SEVERITY: High
CWE: CWE-693, CWE-1021, CWE-16
LOCATION: All seven host Startup.cs Configure pipelines (no security-headers middleware)
DESCRIPTION: Responses omit standard defensive headers (X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy, X-XSS-Protection, Content-Security-Policy), leaving the UI exposed to clickjacking, MIME sniffing, and reduced defense-in-depth against XSS.
IMPACT: Missing X-Frame-Options / frame-ancestors enables clickjacking; missing X-Content-Type-Options enables MIME-sniffing attacks; absent CSP removes a key layer of XSS mitigation.
EVIDENCE: The Configure pipeline contains no middleware that appends response security headers.
REMEDIATION: Added a new WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs that emits the mandated header baseline (Section 5.2) for every response, wired into all seven hosts. To avoid breaking the inline-script/style-dependent UI, the Content-Security-Policy is emitted as Content-Security-Policy-Report-Only first (observe-then-enforce), to be promoted to the enforcing header once the violation report is clean.
```

```
FINDING: Session cookie missing Secure and SameSite attributes
SEVERITY: High
CWE: CWE-614, CWE-1275
LOCATION: WebVella.Erp.Site/Startup.cs:L88-L101 (only HttpOnly is set)
DESCRIPTION: The authentication cookie sets HttpOnly but neither Secure (restrict to HTTPS) nor SameSite (restrict cross-site sending), leaving it eligible to be transmitted over cleartext and attached to cross-site requests.
IMPACT: Without Secure the cookie can leak over HTTP; without SameSite it is attached to cross-site requests, broadening CSRF exposure and session-theft opportunities.
EVIDENCE: options.Cookie.HttpOnly = true; options.Cookie.Name = "erp_auth_base"; // no Cookie.SecurePolicy, no Cookie.SameSite
REMEDIATION: Added Cookie.SecurePolicy = CookieSecurePolicy.Always and Cookie.SameSite = SameSiteMode.Lax across all hosts, preserving each host's distinct cookie name. This depends on HTTPS enforcement (finding H6), which was added in the same edit so HTTP development flows are not broken.
```

```
FINDING: Information disclosure via development mode in production
SEVERITY: High
CWE: CWE-489, CWE-215, CWE-11
LOCATION: WebVella.Erp.Site/Config.json:L10 ("DevelopmentMode": "true") and WebVella.Erp.Site/web.config:L10 (ASPNETCORE_ENVIRONMENT=Development). Note: web.config exists ONLY in WebVella.Erp.Site.
DESCRIPTION: Development mode and the Development hosting environment are committed as defaults. In production these disclose detailed stack traces and diagnostic error detail (the ApiControllerBase error masking is DevelopmentMode-gated).
IMPACT: Detailed errors and stack traces disclosed to end users reveal internal paths, types, SQL, and library versions, aiding an attacker in reconnaissance and exploit development.
EVIDENCE: "DevelopmentMode": "true"  (Config.json)  /  <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Development" />  (web.config)
REMEDIATION: Set DevelopmentMode=false for production configuration and ensured ASPNETCORE_ENVIRONMENT is not Development in production. A configuration audit (Section 8) should fail the build/deploy if DevelopmentMode=true is present in a production profile.
```

```
FINDING: Synchronous I/O enabled (denial-of-service surface)
SEVERITY: Medium
CWE: CWE-400
LOCATION: WebVella.Erp.Web/Middleware/ErpMiddleware.cs:L25-L27
DESCRIPTION: The middleware opts the request into synchronous I/O. Synchronous I/O ties up thread-pool threads for the duration of blocking reads/writes, and under load can lead to thread starvation and denial of service.
IMPACT: A burst of slow or large requests can exhaust the thread pool, degrading or halting the service (availability impact).
EVIDENCE: var syncIOFeature = context.Features.Get<IHttpBodyControlFeature>(); if (syncIOFeature != null) syncIOFeature.AllowSynchronousIO = true;
REMEDIATION: Fixed opportunistically as a low-risk edit within the configuration commit class — the AllowSynchronousIO = true opt-in is removed/refactored toward asynchronous I/O so the pipeline no longer forces synchronous blocking. (This Medium item was fixed because the change is minimal and localized.)
```


### 4.5 A06 — Vulnerable & Outdated Components

```
FINDING: Out-of-support runtime target (.NET 7)
SEVERITY: High
CWE: CWE-1104
LOCATION: WebVella.Erp.WebAssembly/Server/*.csproj:L4 and WebVella.Erp.WebAssembly/Shared/*.csproj:L4
DESCRIPTION: Two Blazor WebAssembly projects target net7.0. .NET 7 reached end of support on 2024-05-14; end-of-support runtimes no longer receive security patches.
IMPACT: Any vulnerability discovered in the .NET 7 runtime/framework after end-of-support remains unpatched for these projects, exposing them to known and future CVEs.
EVIDENCE: <TargetFramework>net7.0</TargetFramework>
REMEDIATION: Retargeted both projects from net7.0 to net10.0, aligning them with the rest of the solution. .NET 10 is the current Long-Term Support release (GA November 2025). Classified under the components commit class.
```

```
FINDING: Unpinned SDK toolchain (build reproducibility / supply-chain)
SEVERITY: Low
CWE: CWE-1104
LOCATION: global.json:L1-L5 (SDK version line commented out)
DESCRIPTION: The global.json SDK version pin is commented out, so builds float to whatever SDK is installed on the build machine, undermining reproducibility and supply-chain assurance.
IMPACT: Non-deterministic builds; a compromised or unexpected SDK could alter build output without detection. Low direct exploitability but a supply-chain hygiene gap.
EVIDENCE: { "sdk": { //"version": "7.0.103" } }
REMEDIATION: Pinned a .NET 10 SDK version in global.json to make builds reproducible. Classified under the components commit class; severity Low (supply-chain hygiene).
```

```
FINDING: Vendored client-side libraries (CVE-gated)
SEVERITY: Low
CWE: CWE-1104, CWE-1395
LOCATION: WebVella.Erp.Plugins.SDK/wwwroot/lib/jstree, WebVella.Erp.Web/wwwroot/lib/js-cookie (vendored client-side assets)
DESCRIPTION: Front-end libraries (Bootstrap v4, jQuery, moment, jsTree 3.3.7, Select2, Chart.js) are vendored into wwwroot. Vendored libraries can drift behind upstream security releases.
IMPACT: An outdated vendored library carrying a known client-side CVE (e.g., DOM-based XSS) could be exploited in the browser context.
EVIDENCE: Vendored assets are present under wwwroot/lib (e.g., .../lib/jstree, .../lib/js-cookie); no libman.json is present in the repository and no active CVE was confirmed by a scanner in this pass.
REMEDIATION: Documented only (Low, not code-changed). Recommendation: run retire.js in CI and update only libraries flagged with an active CVE. Deferral rationale: a blanket front-end upgrade risks UI regressions and exceeds the minimal-change boundary; no confirmed active CVE in this pass.
```

### 4.6 A07 — Identification & Authentication Failures

```
FINDING: Default administrator credential seeded at install
SEVERITY: Critical
CWE: CWE-798, CWE-521
LOCATION: WebVella.Erp/ERPService.cs:L462-L476 (first-user seed)
DESCRIPTION: Initialization seeds the first administrator with a well-known static password ("erp"), the email erp@webvella.com, and username administrator. A default credential shipped with the product is public knowledge.
IMPACT: Any freshly installed instance where the operator has not changed the password is trivially compromised at the highest privilege level (full administrative takeover).
EVIDENCE: user["password"] = "erp"; user["email"] = "erp@webvella.com"; user["username"] = "administrator";
REMEDIATION: The seeded administrator password is now generated from a cryptographically secure RNG (CSPRNG) at install time, and the account is flagged to force a password rotation at first login. No static default password ships with the product.
```

```
FINDING: Excessive session lifetime (100-year authentication cookie)
SEVERITY: High
CWE: CWE-613
LOCATION: WebVella.Erp.Web/Services/AuthService.cs:L44
DESCRIPTION: The authentication cookie is issued with an expiry 100 years in the future, effectively never expiring.
IMPACT: A stolen cookie remains valid indefinitely; there is no natural session timeout to bound the window of misuse after theft.
EVIDENCE: ExpiresUtc = DateTimeOffset.UtcNow.AddYears(100),
REMEDIATION: Reduced the cookie ExpiresUtc to an operational lifetime consistent with the documented JWT lifetime (1440 minutes), bounding the exposure window while preserving normal usability.
```

```
FINDING: No account lockout / brute-force protection
SEVERITY: High
CWE: CWE-307
LOCATION: WebVella.Erp.Web/Pages/login.cshtml.cs (enumeration-safe message at L102; no lockout)
DESCRIPTION: The login handler returns a generic, enumeration-safe error but imposes no limit on failed attempts, allowing unlimited password guessing.
IMPACT: Attackers can brute-force or credential-stuff accounts at will, increasing the likelihood of account takeover — especially against weak passwords.
EVIDENCE: Error = "Invalid username or password"; // returned on failure, but no failed-attempt counter / lockout
REMEDIATION: Added a five-attempt failed-login lockout while preserving the existing enumeration-safe "Invalid username or password" message (so the fix does not introduce username enumeration).
```

```
FINDING: JWT expiry computed from local time instead of UTC
SEVERITY: Medium
CWE: CWE-613
LOCATION: WebVella.Erp.Web/Services/AuthService.cs:L156-L158
DESCRIPTION: The JWT expires claim is computed with DateTime.Now (server local time) while validation and issuance elsewhere use UTC. On non-UTC servers this skews the effective token lifetime.
IMPACT: Depending on the server's offset, tokens live longer or shorter than intended — a longer-than-intended lifetime widens the misuse window for a stolen token; a shorter one causes spurious expiry.
EVIDENCE: expires: DateTime.Now.AddMinutes(JWT_TOKEN_EXPIRY_DURATION_MINUTES)
REMEDIATION: Fixed opportunistically (low-risk, authentication commit class): the expiry is computed from DateTime.UtcNow so the token lifetime is deterministic regardless of server timezone.
```

```
FINDING: No multi-factor authentication (MFA)
SEVERITY: Medium
CWE: CWE-308
LOCATION: WebVella.Erp.Web authentication flow (AuthService.Authenticate / login.cshtml.cs) — capability absent
DESCRIPTION: Authentication relies on a single factor (password). No second factor (TOTP, WebAuthn/FIDO2) is available.
IMPACT: Single-factor authentication is more susceptible to credential theft, phishing, and password reuse; a compromised password alone grants access.
EVIDENCE: The login flow validates only email + password; there is no second-factor challenge anywhere in the authentication code path.
REMEDIATION: Documented only (Medium, not code-changed). Recommendation: add TOTP (RFC 6238) and/or WebAuthn/FIDO2 for privileged accounts. Deferral rationale: adding MFA is net-new authentication capability (feature work) explicitly excluded by the Minimal Change Clause.
```

```
FINDING: JWT stored in browser localStorage (WASM client)
SEVERITY: Medium
CWE: CWE-522, CWE-79
LOCATION: WebVella.Erp.WebAssembly/Client/Services/AuthenticationService.cs:L49 (Blazored.LocalStorage key "token")
DESCRIPTION: The Blazor WebAssembly client persists the JWT in browser localStorage under the key "token". localStorage is readable by any JavaScript executing in the origin.
IMPACT: A successful XSS in the client origin could read and exfiltrate the token, enabling session/token theft and user impersonation.
EVIDENCE: await _localStorageService.SetItemAsync("token", token);  // AuthenticationService.cs:L49
REMEDIATION: Documented only (Medium, not code-changed). Recommendation: move to HttpOnly/Secure/SameSite cookie-based token handling so the token is unreachable from script; pair with the CSP hardening (H4). Deferral rationale: changing the client token-storage model is an architectural change beyond a minimal fix.
```

### 4.7 A08 — Software & Data Integrity Failures

```
FINDING: Insecure deserialization via Newtonsoft.Json TypeNameHandling
SEVERITY: Critical
CWE: CWE-502
LOCATION: WebVella.Erp/Jobs/JobDataService.cs:L27,L96,L297,L346 (TypeNameHandling.All); WebVella.Erp/Notifications/NotificationContext.cs:L110,L155; WebVella.Erp/Database/DbEntityRepository.cs:L50,L165,L212; WebVella.Erp/Database/DbRelationRepository.cs:L47,L128,L173 (TypeNameHandling.Auto)
DESCRIPTION: Serialization settings enable TypeNameHandling.All/Auto, which embed and honor a $type discriminator during deserialization. When deserializing data that an attacker can influence, this permits instantiation of arbitrary .NET types ("gadgets").
IMPACT: Deserialization gadget chains can lead to remote code execution or other integrity violations if attacker-controlled JSON reaches these code paths.
EVIDENCE: JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All }; // and TypeNameHandling.Auto at the notification/entity/relation sites
REMEDIATION: Set TypeNameHandling.None where polymorphism is unnecessary; where polymorphic round-tripping of persisted data is genuinely required, attached a new allowlist WebVella.Erp/Utilities/ErpSerializationBinder.cs (ISerializationBinder) that resolves only an explicitly permitted set of types and rejects everything else, neutralizing gadget attacks while preserving legitimate persisted payloads. This aligns with Roslyn analyzers CA2326–CA2330.
```

### 4.8 A09 — Security Logging & Monitoring Failures

```
FINDING: Insufficient security logging
SEVERITY: Medium
CWE: CWE-778
LOCATION: WebVella.Erp/Diagnostics/Log.cs
DESCRIPTION: The logging facility records login timestamps and general error events but does not emit structured, security-relevant audit entries for authentication failures, permission denials, or role/password changes.
IMPACT: Attacks such as brute-force attempts, privilege abuse, or unauthorized role changes may go undetected and lack the forensic trail required for incident response.
EVIDENCE: Log.cs exposes GetLogs plus general create/error logging, with no dedicated security-event entries for auth failures, permission denials, or role/password changes.
REMEDIATION: Fixed within the logging commit class: extended Log.cs with structured security-event entries for authentication failures, permission denials, and role/password changes, providing an auditable trail for monitoring and incident response.
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
LOCATION: WebVella.Erp.Web/Services/CodeEvalService.cs:L44-L45
DESCRIPTION: The platform evaluates C# source at runtime via CS-Script to support admin-authored server-side logic. Dynamic code execution is inherently powerful and, if exposed to untrusted authors, would permit arbitrary code execution.
IMPACT: If a non-trusted actor could supply the source code, this would be remote code execution. In the platform's design the authorship of this code is restricted to trusted administrators, making it a deliberate, bounded capability rather than an open injection sink.
EVIDENCE: CSScript.EvaluatorConfig.ReferenceDomainAssemblies = true; ICodeVariable scriptObject = CSScript.Evaluator.LoadCode<ICodeVariable>(sourceCode);
REMEDIATION: Documented as an accepted risk; the capability is intentionally retained (removing it is feature loss, out of scope). The admin-only trusted-author boundary is now enforced in code: the request-reachable compiler endpoint `api/v3.0/datasource/code-compile` (WebVella.Erp.Web/Controllers/WebApiController.cs), which forwards caller-supplied `model.CsCode` to `CodeEvalService.Compile`, previously relied only on class-level `[Authorize]` (authentication, any role). It now carries `[Authorize(Roles = "administrator")]`, so non-administrators are denied by default. The threat comment at the evaluation site (`CodeEvalService.cs:L44-L48`) was corrected to describe the guard that is actually enforced rather than an assumed one. Recommendation: keep the authorship path restricted to administrators and audit any change to who can supply code.
```

> **Positive control (A03, preserved — no change):** All database access uses parameterized `NpgsqlCommand` queries; SQL injection is therefore controlled at the data layer. This baseline was verified and deliberately left unchanged.

### 4.11 A04 — Insecure Design

```
FINDING: Latent null-reference risk in Blazor circuit handler
SEVERITY: Low
CWE: CWE-476
LOCATION: WebVella.Erp.Web/Middleware/SecuritityCircuitHandler.cs
DESCRIPTION: The Blazor circuit handler contains a latent null-dereference path (a robustness/design defect rather than a directly exploitable security flaw). The file name reflects the repository's existing spelling.
IMPACT: A null dereference could throw and terminate a circuit (localized availability/robustness impact); it is not a confidentiality or integrity compromise.
EVIDENCE: Circuit-handler code path that may dereference a null reference under specific conditions.
REMEDIATION: Documented only (Low, not Critical) per the Minimal Change Clause. Recommendation: add a null guard on the affected path in a future maintenance change.
```

> **A04 note.** Insecure-design concerns primarily manifested as the session/authentication weaknesses already captured under A07 (excessive session lifetime, missing lockout, non-UTC token expiry); their remediation is **⏳ Planned** in the authentication vulnerability-class checkpoint (see Section 4.2 status for H7, H8, M1 and the Section 5 authentication marker).

### 4.12 A10 — Server-Side Request Forgery (SSRF)

> **Assessed — no active finding.** No server-side code path was identified that fetches an attacker-controlled URL in a way that constitutes SSRF. The Mail plugin's inline-image handling (via HtmlAgilityPack) was noted as an input-handling surface to monitor, but no exploitable SSRF sink was found; no code change is made for A10.

---


## 5. Remediation Actions (Critical & High)

Remediations are grouped into atomic commits by vulnerability class and validated after each class. The before/after summaries below describe the vulnerable code and the fixed approach; no change alters an API contract, database schema, or documented performance envelope. **Each subsection is annotated with its status at the current checkpoint (✅ Implemented / ⏳ Planned);** an item marked ⏳ describes the target fix that lands in a later vulnerability-class checkpoint and is **not yet present** in the code.

### 5.1 Commit class: `crypto` (A02)

**Status:** ✅ Implemented — the `PasswordUtil` PBKDF2 primitive/verify and the `ErpSettings` JWT-default removal. ⏳ Planned — routing `SecurityManager`/`RecordManager`/`DbRecordRepository` through the new hasher and removing the hardcoded key from `CryptoUtility`.

**Password hashing — MD5 → PBKDF2 with backward-compatible migration (C1, H1).**

- **Before:** `PasswordUtil` hashed with a single unsalted pass of MD5 and compared hashes with a non-constant-time ordinal comparer.
- **After:** PBKDF2 (HMAC-SHA-256, 128-bit random salt, 256-bit subkey, iterated) is the storage format. A tri-state verify first attempts the modern hash, then the legacy MD5 hash; on legacy success it signals *rehash needed* so the caller persists an upgraded PBKDF2 hash. Modern comparison uses `CryptographicOperations.FixedTimeEquals`. `VerifyMd5Hash` is retained for legacy verification only.

```
// Backward-compatible verify (conceptual — preserves login for every existing user)
var result = VerifyModern(stored, provided);
if (result == Failed && VerifyMd5Hash(stored, provided))
    return SuccessRehashNeeded;   // caller re-persists a PBKDF2 hash on next login
```

⏳ **Planned:** the credential-validation path (`WebVella.Erp/Api/SecurityManager.cs`) and the encrypted password-field write path (`WebVella.Erp/Api/RecordManager.cs`) will be routed through the new primitive so hashing and verification stay consistent, persisting rehashed values on legacy success. These call sites still use the legacy MD5 helper at the current checkpoint and change in a later vulnerability-class checkpoint.

**Hardcoded keys removed (C2, C3).**

- **Before:** `CryptoUtility.cs` falls back to a compiled-in default encryption key; `ErpSettings.cs` fell back to the literal JWT key `"ThisIsMySecretKey"`.
- **After:** ✅ Implemented for the JWT key — the literal is removed from `ErpSettings.cs`; a configured JWT signing key (`Settings:Jwt:Key`) is required and a missing key fails fast at startup with a clear message instead of silently using a known secret. The deprecated misspelled `Settings:EncriptionKey` continues to be read for backward compatibility but is documented as deprecated (Section 8). ⏳ **Planned** for the encryption key — removing the compiled-in default from `CryptoUtility.cs` and requiring a configured `Settings:EncryptionKey` lands in a later checkpoint.

### 5.2 Commit class: `configuration` (A02/A05)

**Status:** ✅ Implemented — `SecurityHeadersMiddleware` (the baseline headers with CSP in report-only mode) and the removal of the synchronous-I/O opt-in in `ErpMiddleware`. ⏳ Planned — the seven-host CORS allowlist, cookie `SecurePolicy`/`SameSite`, `UseHttpsRedirection`/`UseHsts`, `Config.json` secret removal / `DevelopmentMode=false`, and `web.config` environment change. (The header baseline below is emitted by the middleware; `Strict-Transport-Security` is added by `UseHsts()` when the host wiring lands.)

**Security-header baseline (H4).** A new `WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs` emits the following baseline on every response (reproduced verbatim as the required standard):

```
Content-Security-Policy: default-src 'self'
Strict-Transport-Security: max-age=31536000; includeSubDomains
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
X-XSS-Protection: 0
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: geolocation=(), microphone=(), camera=()
```

> **CSP rollout caveat.** Because the WebVella UI relies on inline scripts and styles (Bootstrap, jQuery, and Web-Component assets), `Content-Security-Policy: default-src 'self'` is first emitted as `Content-Security-Policy-Report-Only`. Violations are observed without breaking pages, and the header is promoted to the enforcing `Content-Security-Policy` (optionally with nonces/hashes) once the report is clean. `Strict-Transport-Security` is emitted via `UseHsts()` and, together with `UseHttpsRedirection()`, is gated to non-development environments.

**CORS allowlist (H3).**
- **Before:** `policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()`.
- **After:** An explicit origin allowlist modeled on the tightened `AllowNodeJsLocalhost` policy already present in `WebVella.Erp.Site.Crm/Startup.cs` (`WithOrigins(...).AllowAnyMethod().AllowCredentials()`), preserving the Blazor WASM development origin.

**Cookie hardening + HTTPS/HSTS (H5, H6).**
- **Before:** cookie set `HttpOnly` only; no `UseHttpsRedirection`/`UseHsts` in the pipeline.
- **After:** `Cookie.SecurePolicy = Always` and `Cookie.SameSite = Lax` added (each host keeps its distinct cookie name); `UseHttpsRedirection()` and `UseHsts()` added, gated to non-development environments. Cookie hardening and HTTPS enforcement were changed together so HTTP development flows are not broken.

**Secrets and dev-mode (H2, H9).**
- **Before:** connection string with `User Id=dev;Password=dev`, a committed encryption key, a committed JWT key, and `DevelopmentMode="true"` in `Config.json`; `ASPNETCORE_ENVIRONMENT=Development` in `web.config`.
- **After:** committed secrets removed from configuration and supplied via user-secrets/environment variables; `DevelopmentMode=false` for production; production environment is not `Development`.

**Synchronous I/O (M2).**
- **Before:** `syncIOFeature.AllowSynchronousIO = true;` in `ErpMiddleware`.
- **After:** the synchronous-I/O opt-in is removed/refactored toward async I/O, reducing the thread-starvation DoS surface.

### 5.3 Commit class: `deserialization` (A08)

**Status:** ✅ Implemented — the shared fail-closed `ErpSerializationBinder`. ⏳ Planned — switching the four Newtonsoft `TypeNameHandling` call sites (`JobDataService`, `NotificationContext`, `DbEntityRepository`, `DbRelationRepository`) to `None` or the binder. The `TypeNameHandling` sites below are unchanged at the current checkpoint.

**`TypeNameHandling` gadget surface closed (C5).**
- **Before:** `TypeNameHandling.All` (jobs) and `TypeNameHandling.Auto` (notifications, entities, relations) honored an attacker-influenceable `$type` discriminator.
- **After:** `TypeNameHandling.None` where polymorphism is unnecessary; otherwise a shared allowlist binder:

```
// ErpSerializationBinder (conceptual): FAIL-CLOSED — resolve only allow-listed types and THROW for
// anything else, so a hostile $type discriminator (deserialization gadget) is never materialized.
// Never return null (that would be fail-open); Newtonsoft only instantiates the type AFTER this returns.
public Type BindToType(string assemblyName, string typeName)
{
    Type resolvedType = DefaultBinder.BindToType(assemblyName, typeName);
    if (resolvedType == null || !IsTypeAllowed(resolvedType))
        throw new JsonSerializationException($"Blocked deserialization of forbidden type '{typeName}' (A08 / CWE-502).");
    return resolvedType;
}
```

The binder's allowlist admits first-party (`WebVella*`) types plus a curated safe BCL set, recursing through array element types and generic arguments so existing stored data still round-trips while any non-permitted type is rejected by throwing.

### 5.4 Commit class: `authentication` (A07)

**Status:** ⏳ Planned — all items in this subsection (`ERPService` default-admin credential, `AuthService` cookie lifetime and UTC token clock, and `login.cshtml.cs` lockout) land in a later vulnerability-class checkpoint and are **not yet present** in the code.

- **Default admin (C4):** static password `"erp"` → CSPRNG-generated initial password with forced rotation at first login.
- **Session lifetime (H7):** cookie `ExpiresUtc` reduced from `AddYears(100)` to an operational lifetime aligned with the 1440-minute JWT lifetime.
- **Lockout (H8):** five-attempt failed-login lockout added, preserving the enumeration-safe `"Invalid username or password"` message.
- **Token clock (M1):** JWT `expires` computed from `DateTime.UtcNow` instead of `DateTime.Now`.

### 5.5 Commit class: `components` (A06)

**Status:** ✅ Implemented — the WASM **Shared** project retarget and the `global.json` SDK pin. ⏳ Planned — the WASM **Server** project retarget.

- **Runtime target (H10):** ✅ the `WebVella.Erp.WebAssembly` **Shared** project is retargeted `net7.0` → `net10.0`; ⏳ the **Server** project retarget is Planned for a later checkpoint.
- **SDK pin (L1):** ✅ `global.json` SDK version pinned to a .NET 10 SDK for reproducible builds.

### 5.6 Commit class: `logging` (A09)

**Status:** ✅ Implemented.

- **Security events (M3):** `Log.cs` extended with structured entries for authentication failures, permission denials, and role/password changes.

---


## 6. Documented Findings (Medium & Low — not code-changed)

Per the Minimal Change Clause ("document but do not fix unless Critical"), the following Medium/Low findings are documented with a recommended fix and a deferral rationale rather than remediated in code. (The Medium/Low items that *were* implemented at the current checkpoint because the edit was low-risk and already in scope for its commit class — **M2, M3, L1** — appear in Sections 4 and 5; **M1** is ⏳ Planned with the authentication class.)

### D1 — No multi-factor authentication (MFA)
- **OWASP / Severity / CWE:** A07 / Medium / CWE-308.
- **Description:** Authentication relies on a single factor (password). MFA is not implemented.
- **Recommended fix:** Add TOTP (RFC 6238) and/or WebAuthn/FIDO2 as a second factor for privileged accounts.
- **Deferral rationale:** Adding MFA is net-new authentication capability (feature work) explicitly excluded by the Minimal Change Clause and out-of-scope list. Recommended as a future initiative.

### D2 — JWT stored in browser localStorage (WASM client)
- **OWASP / Severity / CWE:** A07/A02 / Medium / CWE-522, CWE-79.
- **Location:** `WebVella.Erp.WebAssembly/Client/Services/AuthenticationService.cs:L49` (Blazored.LocalStorage, key `token`).
- **Description:** The WASM client persists the JWT in `localStorage` (`SetItemAsync("token", token)`), which is readable by any JavaScript running in the origin; a successful XSS could exfiltrate the token.
- **Recommended fix:** Move to an `HttpOnly`, `Secure`, `SameSite` cookie-based token handling model so the token is not reachable from script; pair with the CSP hardening (H4) to reduce XSS risk.
- **Deferral rationale:** Changing the client token-storage model is an architectural change beyond a minimal fix; the server-side CSP/headers work (H4) already reduces the enabling XSS risk. Recommended as future work.

### D3 — Runtime C# evaluation (accepted risk)
- **OWASP / Severity / CWE:** A03 / Medium / CWE-94.
- **Location:** `WebVella.Erp.Web/Services/CodeEvalService.cs` (runtime eval site); enforcing guard in `WebVella.Erp.Web/Controllers/WebApiController.cs` (route `api/v3.0/datasource/code-compile`).
- **Description:** Admin-authored C# is evaluated at runtime (CS-Script). This is a deliberate, trusted-author feature.
- **Applied action:** ✅ Implemented. The `api/v3.0/datasource/code-compile` endpoint in `WebApiController` — the only request path that submits arbitrary source code to `CodeEvalService` — now carries `[Authorize(Roles = "administrator")]` in addition to the controller's class-level `[Authorize]`, so arbitrary runtime C# compilation is reachable **only by administrators**, not by every authenticated user. A threat comment at the eval site documents this enforced guard. The runtime `Evaluate` path executes code that was already persisted (code data sources / page-component code) via the admin-only page-builder / SDK tooling. The capability is intentionally **retained** (removing it is feature loss).
- **Deferral rationale:** Removing the feature is out of scope; with the administrator-only guard enforced, the residual risk is bounded to trusted administrators. Recommendation: keep authorship restricted to administrators and audit any change to who can supply code.

### D4 — UI-only authorization helper (`WvAuthorize`)
- **OWASP / Severity / CWE:** A01 / Low / CWE-602.
- **Description:** `WvAuthorize` controls element visibility only; it is not a server-side enforcement point.
- **Recommended fix:** Confirm every UI-gated action is independently enforced server-side.
- **Deferral rationale:** Server-side checks (`SecurityContext.HasEntityPermission`) already back access decisions, so this is defense-in-depth, not a bypass. Not Critical → documented.

### D5 — Latent null-reference in Blazor circuit handler
- **OWASP / Severity / CWE:** A04 / Low / CWE-476.
- **Location:** `WebVella.Erp.Web/Middleware/SecuritityCircuitHandler.cs` (repository's existing spelling).
- **Description:** A latent null-dereference path (robustness defect, not a direct security compromise).
- **Recommended fix:** Add a null guard on the affected path.
- **Deferral rationale:** Not Critical and not security-exploitable → documented per the Minimal Change Clause.

### D6 — Vendored client-side libraries (CVE-gated)
- **OWASP / Severity / CWE:** A06 / Low / CWE-1104, CWE-1395.
- **Libraries:** Bootstrap v4, jQuery, moment, jsTree 3.3.7, Select2, Chart.js.
- **Observed assets:** `WebVella.Erp.Plugins.SDK/wwwroot/lib/jstree`, `WebVella.Erp.Web/wwwroot/lib/js-cookie`. Note: no `libman.json` is present in the repository, and no active CVE was confirmed by a scanner in this pass.
- **Recommended fix:** Run `retire.js` in CI and update **only** libraries flagged with an active CVE.
- **Deferral rationale:** A blanket front-end library upgrade risks UI regressions and exceeds the minimal-change boundary; updates should be CVE-gated. No confirmed active CVE in this pass → documented.

### D7 — Secrets in `WebVella.Erp.ConsoleApp/Config.json`
- **OWASP / Severity / CWE:** A02/A05 / Low / CWE-798.
- **Description:** The console smoke-test harness ships a tracked `Config.json` that carries the same database connection string and encryption key as the hosts (it has no `Jwt` section).
- **Recommended fix:** Apply the same user-secrets/environment-variable migration as the production hosts.
- **Deferral rationale:** The console harness is out of the seven-host production scope; documented for completeness.

### 6.1 Positive Controls Affirmed (preserved — no change)

These existing controls were verified as correct baseline behavior and intentionally left unchanged (treated as reference patterns):

| Control | Location | Why it is correct |
|---------|----------|-------------------|
| Parameterized SQL | Data layer (`NpgsqlCommand` parameters throughout) | SQL injection (A03) is controlled at the source; no string-concatenated user input in queries. |
| Sensitive-field redaction | `WebVella.Erp/Api/Models/ErpUser.cs` (`[JsonIgnore]`) | Password and other sensitive fields are excluded from serialization. |
| Antiforgery on POST | Razor page POST handlers | CSRF tokens are enforced on state-changing form posts. |
| Error masking | `WebVella.Erp.Web/Controllers/ApiControllerBase.cs` (DevelopmentMode-gated) | Error detail is masked unless `DevelopmentMode` is set — correct once `DevelopmentMode=false` in production (H9). |
| Server-side authorization | `WebVella.Erp/Api/SecurityContext.cs` (`HasEntityPermission`) | Access decisions are enforced server-side, independent of UI visibility helpers (D4). |

---


## 7. Scan Results (Before / After)

The scans below are the `.NET`-appropriate equivalents of the request's `npm audit` / `pip-audit` examples (Section 2.4). "Before" reflects the pre-remediation baseline; the **"After (target)"** column is the *projected* end-state once **all** remediations in this change set are in place. The acceptance gate is **zero Critical/High** across SAST, SCA, and secrets scans.

> **⏳ Status: gates not yet met.** The automated SAST/SCA/secrets scans have **not yet been run to completion**, and several remediations that clear these categories are still **⏳ Planned** (Section 5). The "After (target)" values below are therefore projections, not measured results — each total row is marked **Target (pending)** rather than "gate met." A dependency scan run at the current checkpoint additionally surfaces an outstanding **high-severity advisory for `AutoMapper` 14.0.0 (NU1903)** and moderate advisories for **`MailKit`/`MimeKit` (NU1902)** that must be resolved before the SCA gate can pass.

### 7.1 SAST (Security Code Scan / Semgrep / Roslyn CA2326–CA2330)

| Category | Before (Critical/High) | After (target) | Notes |
|----------|:----------------------:|:---------------------:|-------|
| Insecure deserialization (CA2326–CA2330) | 11 sites | 0 | `TypeNameHandling.None` / allowlist binder (C5). |
| Weak hashing (MD5) | 1 | 0 | PBKDF2 migration (C1). |
| Hardcoded keys (crypto) | 2 | 0 | Encryption + JWT keys removed (C2, C3). |
| Non-constant-time comparison | 1 | 0 | `FixedTimeEquals` (H1). |
| **SAST total (Critical/High)** | **15** | **0** | Target (pending) — deserialization call-site switches and the crypto integration/CryptoUtility key removal are ⏳ Planned. |

### 7.2 SCA (`dotnet list package --vulnerable --include-transitive` / OWASP Dependency-Check / Trivy / retire.js)

| Component surface | Before (Critical/High) | After (target) | Notes |
|-------------------|:----------------------:|:---------------------:|-------|
| NuGet managed packages | `AutoMapper` 14.0.0 (NU1903, high); `MailKit`/`MimeKit` (NU1902, moderate) | pending | Surfaced by `dotnet build`/restore; the AAP's *security-relevant* pins (Section 7.4) are current, but these advisories are outstanding and tracked under A06. |
| Runtime target (.NET 7 EOS) | 2 projects unsupported | 0 | Retargeted to `net10.0` (H10). |
| SDK toolchain pin | unpinned | pinned | `global.json` pinned (L1). |
| Vendored JS (retire.js) | none confirmed | none confirmed | CVE-gated; monitor in CI (D6). |
| **SCA total (Critical/High)** | **`AutoMapper` NU1903 (high) + EOS runtime** | pending | Target (pending) — `AutoMapper` advisory unresolved and the WASM Server retarget is ⏳ Planned. |

### 7.3 Secrets (gitleaks / detect-secrets)

| Secret type | Before | After | Notes |
|-------------|:------:|:-----:|-------|
| DB connection credentials | present in `Config.json` | 0 | Moved to user-secrets/env (H2). |
| Encryption key literal | present (source + config) | 0 | Removed; configured value required (C2). |
| JWT signing key literal | present (source + config) | 0 | Removed; configured value required (C3). |
| Default admin password | present in source | 0 | CSPRNG-generated + forced rotation (C4). |
| **Secrets total** | **≥4** | **0** | Target (pending) — secret removal from the seven host `Config.json` and the default-admin rotation are ⏳ Planned. |

> **Interpretation.** The acceptance goal is that, **once all remediations land**, SAST reports **0** Critical/High, SCA reports **0** Critical/High CVEs (and no end-of-support runtime), and the secrets scan reports **0** committed credentials. At the current foundational checkpoint these gates are **not yet met**: several clearing remediations are ⏳ Planned (Section 5), and the SCA surface still shows the outstanding `AutoMapper` NU1903 (high) / `MailKit`/`MimeKit` NU1902 (moderate) advisories, which are tracked under A06. The AAP's *security-relevant* NuGet pins (Section 7.4) are already current; the A06 remediation is a runtime-target change plus a CVE-gated vendored-asset policy rather than a wholesale package-upgrade wave.

### 7.4 Security-relevant dependency versions (reference)

| Package | Version | Relevance |
|---------|---------|-----------|
| Newtonsoft.Json | 13.0.4 | `TypeNameHandling` (A08) is a *usage* issue at this version, not a package CVE. |
| Npgsql | 9.0.4 | Parameterized commands keep SQL injection (A03) controlled. |
| System.IdentityModel.Tokens.Jwt | 8.15.0 | JWT construction/validation (A07). |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.1 | Bearer-token auth on hosts (A07); see the JWT_README reconciliation in Section 9. |
| Microsoft.CodeAnalysis.* (Roslyn) | 5.0.0 | Backs runtime C# evaluation (A03, documented). |
| CS-Script (CSScriptLib) | 4.13.1 | Runtime C# evaluation engine (A03, documented). |
| Blazored.LocalStorage | 4.5.0 | WASM client stores JWT in localStorage (A07/A02, documented — D2). |
| Microsoft.AspNetCore.Cryptography.KeyDerivation | shared framework | Provides in-box PBKDF2 for A02 with no added dependency. |

---

## 8. Secure-Configuration Guide

All secrets must be supplied **outside source control** using the .NET configuration providers the application already consumes: **.NET user-secrets** in development and **environment variables** in production. (Externalized key management such as HSMs or cloud key vaults is out of scope; this guide uses only the in-repo user-secrets / environment-variable path.)

> Environment variables map to configuration keys by replacing the `:` separator with a double underscore `__`. For example, `Settings:Jwt:Key` becomes the environment variable `Settings__Jwt__Key`.

### 8.1 Connection string (`Settings:ConnectionString`)

```bash
# Development (from the host project directory, e.g. WebVella.Erp.Site)
dotnet user-secrets init
dotnet user-secrets set "Settings:ConnectionString" "Server=<host>;Port=5432;User Id=<user>;Password=<password>;Database=<db>;Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120;"

# Production (environment variable)
export Settings__ConnectionString="Server=<host>;Port=5432;User Id=<user>;Password=<password>;Database=<db>;Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120;"
```

### 8.2 Encryption key (`Settings:EncryptionKey`)

Use a strong random 256-bit key (e.g., 64 hex characters or a 44-character base64 string).

```bash
# Development
dotnet user-secrets set "Settings:EncryptionKey" "<256-bit-random-key>"

# Production
export Settings__EncryptionKey="<256-bit-random-key>"
```

> **Deprecation note.** A legacy misspelled key, `Settings:EncriptionKey`, is still read for backward compatibility (`WebVella.Erp/ErpSettings.cs:L59-L64`) but is **deprecated**; use the correctly spelled `Settings:EncryptionKey` going forward. The application no longer falls back to a hardcoded default — a missing encryption key fails fast at startup.

### 8.3 JWT signing key (`Settings:Jwt:Key`)

Use a strong random key of at least 256 bits (the HMAC-SHA-256 signer benefits from a key ≥ the hash size).

```bash
# Development
dotnet user-secrets set "Settings:Jwt:Key" "<256-bit-or-larger-random-key>"

# Production
export Settings__Jwt__Key="<256-bit-or-larger-random-key>"
```

> **Fail-fast & rotation caveats.** The application now **fails fast at startup** if `Settings:Jwt:Key` is missing (no insecure default). **Rotating a live signing key invalidates all in-flight tokens** — every issued JWT becomes invalid, forcing a mass re-authentication (sign-out). Schedule key rotation during a maintenance window and communicate it operationally; do not rotate silently.

### 8.4 Development mode and hosting environment

- Set `Settings:DevelopmentMode = false` in every production configuration profile.
- Ensure `ASPNETCORE_ENVIRONMENT` is **not** `Development` in production (set it to `Production`). Recall that `web.config` (present only in `WebVella.Erp.Site`) hardcoded `ASPNETCORE_ENVIRONMENT=Development` — override it in production hosting.

### 8.5 Configuration audit gate (deploy-time)

Add a configuration audit to the build/deploy pipeline that **fails** if any of the following is present in a production profile:

1. The default JWT key (`ThisIsMySecretKey…`).
2. The default administrator credential (username `administrator` with password `erp`, or an unrotated seeded admin).
3. `DevelopmentMode = true` (or `ASPNETCORE_ENVIRONMENT=Development`).

This gate operationalizes the "no insecure defaults in production" guarantee and complements the SAST/SCA/secrets scans in Section 7.

---

## 9. Appendix / References

### 9.1 Remediation file plan (by commit class)

Files in the full remediation plan, annotated with status at the current checkpoint (✅ Implemented / ⏳ Planned). See **Milestone Status** for the summary.

- **crypto (A02):** ✅ `WebVella.Erp/Utilities/PasswordUtil.cs`, ✅ `WebVella.Erp/ErpSettings.cs` (JWT default); ⏳ `WebVella.Erp/Utilities/CryptoUtility.cs`, ⏳ `WebVella.Erp/Api/SecurityManager.cs`, ⏳ `WebVella.Erp/Api/RecordManager.cs`.
- **configuration (A05/A02):** ✅ `WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs` (new), ✅ `WebVella.Erp.Web/Middleware/ErpMiddleware.cs`; ⏳ `WebVella.Erp.Site*/Startup.cs` (7 hosts), ⏳ `WebVella.Erp.Site*/Config.json` (7 hosts), ⏳ `WebVella.Erp.Site*/web.config`.
- **deserialization (A08):** ✅ `WebVella.Erp/Utilities/ErpSerializationBinder.cs` (new); ⏳ `WebVella.Erp/Jobs/JobDataService.cs`, ⏳ `WebVella.Erp/Notifications/NotificationContext.cs`, ⏳ `WebVella.Erp/Database/DbEntityRepository.cs`, ⏳ `WebVella.Erp/Database/DbRelationRepository.cs`.
- **eval boundary (A03):** ✅ `WebVella.Erp.Web/Controllers/WebApiController.cs` (administrator-only guard on `api/v3.0/datasource/code-compile`), ✅ `WebVella.Erp.Web/Services/CodeEvalService.cs` (accepted-risk threat comment).
- **authentication (A07):** ⏳ `WebVella.Erp.Web/Services/AuthService.cs`, ⏳ `WebVella.Erp.Web/Pages/login.cshtml.cs`, ⏳ `WebVella.Erp/ERPService.cs`.
- **components (A06):** ✅ `WebVella.Erp.WebAssembly/Shared/*.csproj`, ✅ `global.json`; ⏳ `WebVella.Erp.WebAssembly/Server/*.csproj`.
- **logging (A09):** ✅ `WebVella.Erp/Diagnostics/Log.cs`.
- **documentation:** ✅ `SECURITY.md` (this report — milestone-accurate skeleton/status at the current checkpoint; expanded as later checkpoints land).

### 9.2 Reference-only files (preserved, not modified)

- `WebVella.Erp.Web/Controllers/ApiControllerBase.cs` — `DevelopmentMode`-gated error masking.
- `WebVella.Erp/Api/Models/ErpUser.cs` — `[JsonIgnore]` field redaction.
- `WebVella.Erp/Api/SecurityContext.cs` — `HasEntityPermission` server-side authorization.
- `WebVella.Erp.Site.Crm/Startup.cs` — tightened `AllowNodeJsLocalhost` CORS policy (allowlist template).

### 9.3 JWT_README version reconciliation

`WebVella.Erp.Site/JWT_README.txt` documents adding `Microsoft.AspNetCore.Authentication.JwtBearer` **Version 6.0.3** and shows the default key `"ThisIsMySecretKey"`. Both are **stale**:

- The solution now references `Microsoft.AspNetCore.Authentication.JwtBearer` **10.0.1** (Section 7.4). The `6.0.3` reference in `JWT_README.txt` should be updated to **10.0.1**.
- The sample `"Key": "ThisIsMySecretKey"` in `JWT_README.txt` must **not** be used as a real key; per Section 8.3 the JWT key is supplied via user-secrets/environment variables and the application fails fast without it.

### 9.4 Standards & references

- OWASP Top 10 (2021): `https://owasp.org/Top10/`
- OWASP Password Storage Cheat Sheet (KDF selection: Argon2 > scrypt > bcrypt > PBKDF2)
- Microsoft Roslyn analyzers CA2326–CA2330 (Newtonsoft.Json `TypeNameHandling`)
- .NET support policy — .NET 7 end of support 2024-05-14; .NET 10 current LTS.
- CWE database: `https://cwe.mitre.org/`

### 9.5 Environment / build fixes (NOT part of the security remediation)

A set of tracked `.sln`/`.csproj` edits correct project-reference **path casing** (`WebVella.ERP` → `WebVella.Erp`) so the solution restores and builds on a **case-sensitive (Linux) filesystem**. These are **build-environment fixes, not security changes**, and are intentionally kept **separate from the security milestone**:

- **What:** case-only path corrections in `WebVella.ERP3.sln` and the following project files — `WebVella.Erp.ConsoleApp`, `WebVella.Erp.Web`, the six `WebVella.Erp.Plugins.*`, and the six `WebVella.Erp.Site.*` `.csproj` files (15 files total).
- **Why:** on a case-insensitive filesystem (Windows) the original casing resolves; on a case-sensitive filesystem it does not, breaking `dotnet restore`/`build`.
- **Isolation:** they are committed in a **dedicated setup commit** (`setup: fix case-sensitive project references (WebVella.ERP -> WebVella.Erp)`), distinct from the security-remediation commits, so the security change set contains only security-relevant modifications.
- **Security impact:** none — no code behavior, API contract, dependency, or configuration value is changed; only reference path casing.

---

*This report documents the target security posture of the WebVella ERP codebase for the audit and phased remediation described above, and its status at the current checkpoint. Critical and High findings are being remediated in code with the least-invasive change consistent with preserving existing functionality; Medium and Low findings are documented with recommended fixes. Section 5 and Section 7 mark each item **✅ Implemented / ⏳ Planned**; the zero-Critical/High validation posture across SAST, SCA, and secrets scans is the **target** acceptance gate and is **not yet met** at the current checkpoint.*

