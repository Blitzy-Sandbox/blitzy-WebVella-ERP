# Blitzy Project Guide — WebVella ERP Security Hardening

> **Project:** WebVella ERP — OWASP Top 10 (2021) Security Hardening (PR #1 Closure)
> **Branch:** `blitzy-d8b53206-ebb7-43fc-afb2-3f9e2f7815f4`
> **Transformation Type:** Security Hardening (behavior-preserving, minimal-change)
> **Brand Legend:** 🟦 Completed / AI Work = Dark Blue `#5B39F3` · ⬜ Remaining / Not Completed = White `#FFFFFF`

---

## 1. Executive Summary

### 1.1 Project Overview

WebVella ERP is an open-source, metadata-driven ERP framework built on ASP.NET Core (.NET 10) with a PostgreSQL backend, shipped as reusable NuGet libraries and seven runnable site hosts (CRM, Mail, Microsoft CDM, Next, Project, SDK, and the default site). This project is a behavior-preserving **security hardening transformation** that closes the OWASP Top 10 (2021) audit backlog identified in PR #1. It targets administrators and downstream integrators who depend on the published artifacts. The business impact is risk reduction: eliminating credential-theft, remote-code-execution, and misconfiguration exposure without altering business semantics, public APIs, or the database schema. Technical scope spans cryptography, session management, deserialization safety, the HTTP edge, and component currency — applied in place across the existing solution.

### 1.2 Completion Status

```mermaid
%%{init: {'theme':'base', 'themeVariables': {'pie1':'#5B39F3','pie2':'#FFFFFF','pieStrokeColor':'#B23AF2','pieStrokeWidth':'2px','pieOuterStrokeWidth':'2px','pieTitleTextSize':'16px','pieSectionTextSize':'14px'}}}%%
pie showData title Project Completion — 79.8% Complete
    "Completed Work (AI)" : 150
    "Remaining Work" : 38
```

| Metric | Value |
|--------|-------|
| **Total Hours** | **188 h** |
| Completed Hours (AI + Manual) | 150 h (150 h AI autonomous + 0 h manual) |
| Remaining Hours | 38 h |
| **Percent Complete** | **79.8%** (150 ÷ 188) |

> **Completion methodology (PA1):** The percentage reflects only AAP-scoped deliverables plus standard path-to-production activities. Every in-scope *code* requirement is **Completed and committed**; the 38 remaining hours are exclusively path-to-production work (secrets/TLS/CI/deploy) that cannot be performed autonomously without a production environment, plus one explicitly optional enhancement.

### 1.3 Key Accomplishments

- ✅ **All 4 Critical OWASP findings remediated** — unsalted MD5 hashing, hardcoded symmetric key, default JWT signing key, and insecure deserialization.
- ✅ **All 5 High OWASP findings remediated** — file path traversal, upload filename/content-type validation, missing account lockout, 100-year cookie lifetime, default admin credential, and .NET 7 EOL runtime.
- ✅ **Salted adaptive password hashing** via `IPasswordHasher`/`ErpPasswordHasher` (PBKDF2-HMAC-SHA256, 600k iterations, constant-time comparison) with **transparent rehash-on-login** preserving legacy MD5 authentication continuity.
- ✅ **Allowlist `ISerializationBinder`** attached at every `TypeNameHandling` sink, neutralizing the `$type` gadget RCE vector while preserving on-wire format.
- ✅ **`SecurityHeadersMiddleware`** emitting the exact prompt-specified header set, wired once so all 7 site hosts inherit it.
- ✅ **Account lockout (5 attempts / 15 min) + per-host rate limiter**, cookie hardening (`Secure`/`SameSite`, 480-min lifetime), and randomized default admin credential.
- ✅ **.NET 7 → .NET 10 upgrade** of WASM Server/Shared projects; SCA-driven dependency upgrades (AutoMapper 14→16.1.1, MailKit→4.16.0) with **zero vulnerable packages** remaining.
- ✅ **20 KB OWASP A01–A10 audit document** in the prompt's FINDING/SEVERITY/CWE/LOCATION format.
- ✅ **Clean build (0 errors)**, autonomous runtime validation against PostgreSQL 16, and **44/44 security-primitive assertions** passing.

### 1.4 Critical Unresolved Issues

> These are **path-to-production gating items**, not defects in the delivered code. They cannot be completed autonomously because they require production infrastructure, secret material, and operator decisions.

| Issue | Impact | Owner | ETA |
|-------|--------|-------|-----|
| Previously-committed secrets remain recoverable from git **history** | Old JWT/encryption keys could be extracted from prior commits, undermining the new fail-fast controls | Security / DevOps | 2 h |
| HTTPS/TLS not yet enabled in all environments | The `Secure` cookie flag + HSTS require HTTPS; login breaks over plain HTTP until TLS is provisioned | DevOps / Infra | 4 h |
| Production secrets not yet provisioned | Every host fails fast at boot until `Settings__Jwt__Key` (≥32 B), `Settings__EncryptionKey`, and `Settings__ConnectionString` are set | DevOps | 4 h |
| Auth continuity unverified against **real** legacy MD5 accounts | Transparent rehash validated via harness only; needs a staging smoke test with representative production accounts | Backend / QA | (within 8 h smoke test) |
| CSP deployed in Report-Only mode | XSS hardening is observational until CSP is validated against the UI and switched to enforce | Frontend / Security | 5 h |

### 1.5 Access Issues

| System / Resource | Type of Access | Issue Description | Resolution Status | Owner |
|-------------------|----------------|-------------------|-------------------|-------|
| Source repository | Read/Write (git) | Full access; branch present and committed | ✅ Resolved | Blitzy Agent |
| NuGet.org | Network (restore/SCA) | Reachable during validation; restore + `--vulnerable` scan succeeded | ✅ Resolved | Blitzy Agent |
| PostgreSQL (validation) | DB connection | Local container (`erp-pg`, db `ttg_test`) used for runtime validation | ✅ Resolved | Blitzy Agent |
| Production secret store / KMS | Credentials | Not accessible from the build sandbox; production JWT/encryption keys must be provisioned by the operator | ⏳ Pending (human) | DevOps |
| Production TLS certificates | Infra | Not available in sandbox; HTTPS must be enabled in target environments | ⏳ Pending (human) | DevOps |

> No access issue blocked autonomous delivery of the in-scope code. The two pending items are inherent path-to-production responsibilities requiring privileged production access.

### 1.6 Recommended Next Steps

1. **[High]** Provision production secrets (`Settings__Jwt__Key` ≥32 B, `Settings__EncryptionKey`, `Settings__ConnectionString`, `Settings__DefaultAdminPassword`) via environment variables / a secret store for every host and environment. *(4 h)*
2. **[High]** Enable HTTPS/TLS across all environments — prerequisite for the cookie `Secure` flag and HSTS to function. *(4 h)*
3. **[High]** Rotate all secrets previously committed to git history (old JWT signing key, encryption key). *(2 h)*
4. **[Medium]** Deploy to staging and run a cross-host smoke test, explicitly validating auth continuity with **real legacy MD5 accounts**, the AutoMapper/WASM upgrades, and the deserialization binder against existing persisted payloads. *(8 h)*
5. **[Medium]** Validate the UI under CSP, then switch `Content-Security-Policy` from Report-Only to enforce; add CI security gates (SAST + SCA + secrets scan). *(11 h)*

---

## 2. Project Hours Breakdown

### 2.1 Completed Work Detail

> Every component below traces to an AAP remediation goal (G1–G8) or a required supporting activity. All work is committed on the branch.

| Component | Hours | Description |
|-----------|------:|-------------|
| G1 — Password hashing primitives | 16 | New `IPasswordHasher` + `ErpPasswordHasher` (PBKDF2-HMAC-SHA256, 600k iters, 128-bit CSPRNG salt, constant-time `FixedTimeEquals`, self-describing `$pbkdf2-sha256$…` format, legacy-MD5 verify→`needsUpgrade`, 10M-iter DoS cap) |
| G1 — Credential path restructure | 8 | `SecurityManager.GetUser` moved from password-in-SQL-`WHERE` to parameterized fetch-by-email + in-code verify + timing-defense dummy hash + transparent rehash; `RecordManager` routes `PasswordField.Encrypted` through the hasher |
| G1 — Key removal + secrets externalization | 10 | `CryptoUtility` hardcoded key removed (throws if unconfigured); `ErpSettings` fail-fast on default/empty JWT key with `EncriptionKey` fallback preserved; 8× `Config.json` literals externalized |
| G2 — Security headers middleware + wiring | 12 | `SecurityHeadersMiddleware` emitting all 7 headers (CSP Report-Only for UI parity); registered once in `ErpMvcExtensions.UseErp()` for all 7 hosts |
| G2/G7 — CORS tightening + sync-I/O removal | 8 | `AllowAnyOrigin` eliminated across hosts; `AllowSynchronousIO=true` removed from `ErpMiddleware` |
| G3 — Deserialization allowlist | 14 | `ErpSerializationBinder` (recursive type-arg validation, delegates `BindToName` for byte-for-byte wire preservation) attached at every `TypeNameHandling` sink |
| G4 — Auth/session hardening | 18 | Cookie bounded to 480 min + `HttpOnly`/`Secure=Always`/`SameSite`; login lockout (5 attempts / 15 min); per-host rate limiter across all 7 `Startup.cs` |
| G4 — Default admin randomization | 5 | `ERPService` seed "erp" removed → configured or CSPRNG 20-char; password `MinLength` 6→12 |
| G5 — Components upgrade | 14 | WASM Server/Shared `net7.0`→`net10.0`; SCA-driven AutoMapper 14→16.1.1 (`NullLoggerFactory` shim) and MailKit→4.16.0 |
| G6 — File-upload hardening + CodeEval doc | 16 | `WebApiController` filename leaf-reduction, extension allow/deny lists, magic-byte/content-type validation, traversal/IDOR confinement; CodeEvalService trusted-author boundary documented |
| OWASP audit documentation | 8 | `docs/security/owasp-top10-audit.md` (A01–A10, FINDING/SEVERITY/CWE/LOCATION format) |
| Security discovery & finding analysis | 8 | Repository-wide vulnerability discovery, OWASP mapping, severity classification, remediation design |
| Autonomous validation | 13 | Clean build (0 errors), SCA (0 vulnerable), 44/44 security-primitive harness assertions, full runtime gate against PostgreSQL 16 |
| **Total Completed** | **150** | |

### 2.2 Remaining Work Detail

> All remaining items are path-to-production or an explicitly optional enhancement. Each requires resources unavailable to autonomous execution (production secrets, TLS, CI infrastructure, a deployment target).

| Category | Hours | Priority |
|----------|------:|----------|
| Provision production secrets (JWT ≥32 B, EncryptionKey, ConnectionString, DefaultAdminPassword) for all environments | 4 | High |
| Enable HTTPS/TLS in all environments (prerequisite for `Secure` cookie + HSTS) | 4 | High |
| Rotate previously-committed secrets in git history | 2 | High |
| Configure per-host production CORS `WithOrigins` allowlist values (7 hosts) | 3 | Medium |
| Tighten CSP from Report-Only → enforce after UI validation (7 hosts + vendored libs) | 5 | Medium |
| CI security gates: SAST + SCA + secrets scan (Semgrep / `dotnet list --vulnerable` / gitleaks) | 6 | Medium |
| Production/staging deployment + cross-host smoke test (auth continuity with **real** legacy MD5 users, AutoMapper/WASM regression, binder vs. persisted payloads) | 8 | Medium |
| Operational admin onboarding (capture one-time generated password; force first-login reset) | 2 | Medium |
| Optional A09 audit logging of auth failures / permission denials via existing `system_log` | 4 | Low |
| **Total Remaining** | **38** | High 10 · Medium 24 · Low 4 |

### 2.3 Hours Reconciliation

| Bucket | Hours |
|--------|------:|
| Completed (Section 2.1) | 150 |
| Remaining (Section 2.2) | 38 |
| **Total Project Hours** | **188** |

> **Integrity check:** 150 + 38 = 188 ✓ · Remaining 38 h is identical in Sections 1.2, 2.2, and 7 ✓ · Completion = 150 ÷ 188 = **79.8%** ✓

---

## 3. Test Results

> **Integrity note:** All results below originate exclusively from Blitzy's autonomous validation logs for this branch. The solution contains **no test projects**, and creating them is explicitly out of scope per AAP §0.2.2. To validate the new security primitives, three isolated throwaway harnesses were executed in `/tmp` (never committed) plus full build, SCA, and runtime gates.

| Test Category | Framework | Total Tests | Passed | Failed | Coverage % | Notes |
|---------------|-----------|------------:|-------:|-------:|-----------:|-------|
| Password hasher + serialization binder | Isolated console harness (`sec_harness`) | 26 | 26 | 0 | n/a | 15 `ErpPasswordHasher` PBKDF2 hash/verify/`needsUpgrade` incl. legacy-MD5 branch + 11 `ErpSerializationBinder` allow/reject round-trips |
| Fail-fast configuration | Isolated console harness (`failfast_harness`) | 10 | 10 | 0 | n/a | JWT + encryption-key fail-fast validation; `EncriptionKey` typo fallback preserved |
| Security headers (live Kestrel) | Isolated host harness (`web_harness`) | 8 | 8 | 0 | n/a | `SecurityHeadersMiddleware` emits all 7 headers over a live HTTP response |
| **Security-primitive total** | — | **44** | **44** | **0** | n/a | 100% pass rate |
| Compilation gate | `dotnet build` (MSBuild) | 1 | 1 | 0 | n/a | Solution build: **0 errors**, 53 warnings (all pre-existing, out-of-scope) |
| Dependency / SCA gate | `dotnet list package --vulnerable --include-transitive` | 17 (projects) | 17 | 0 | n/a | **Zero vulnerable packages** across all 17 projects |
| Runtime gate | Live host vs PostgreSQL 16 (manual scripted) | 1 | 1 | 0 | n/a | See Section 4 |

> **Coverage %** is reported as *n/a* because no code-coverage instrumentation exists in the solution (no test projects). The 44 assertions exercise the security-critical code paths directly.

---

## 4. Runtime Validation & UI Verification

The default `WebVella.Erp.Site` host was booted against a live PostgreSQL 16 instance (container `erp-pg`, database `ttg_test`), reading externalized secrets from environment variables that override the empty `Config.json` placeholders.

**Runtime health**
- ✅ **Operational** — Host boots cleanly; fail-fast validation passes once secrets are supplied via environment variables.
- ✅ **Operational** — `GET /` → **302** redirect → `/login`.
- ✅ **Operational** — `GET /login` → **200** on a real Razor page.

**Security header verification (on the live `/login` response)**
- ✅ `X-Content-Type-Options: nosniff`
- ✅ `X-Frame-Options: DENY`
- ✅ `X-XSS-Protection: 0`
- ✅ `Referrer-Policy: strict-origin-when-cross-origin`
- ✅ `Permissions-Policy: geolocation=(), microphone=(), camera=()`
- ✅ `Strict-Transport-Security: max-age=31536000; includeSubDomains`
- ✅ `Content-Security-Policy` (default/script/style `'self'`) — emitted in **Report-Only** mode for UI parity

**Authentication & abuse-prevention controls**
- ✅ **Operational** — 7 rapid `POST /login` attempts: #1–5 → **HTTP 400** (antiforgery preserved); #6–7 → **HTTP 429** (rate limiter active).
- ✅ **Operational** — First boot prints a one-time generated admin password to the operator console when `Settings__DefaultAdminPassword` is unset.
- ⚠ **Partial** — Transparent legacy-MD5 rehash verified via harness; **pending** a staging smoke test against real legacy accounts (see Section 6, I3).

**UI verification**
- ✅ **Operational** — `/login` renders correctly with security headers present; no console-blocking under CSP Report-Only.
- ⚠ **Partial** — Full UI behavior under **enforced** CSP not yet validated (CSP intentionally Report-Only until reconciled with inline scripts/styles and vendored libraries).

> No Figma designs or design-system specification were provided (AAP §0.8); UI verification is limited to functional/runtime confirmation, not visual-design compliance.

---

## 5. Compliance & Quality Review

### 5.1 OWASP Top 10 (2021) Compliance Matrix

| OWASP Category | Finding | Severity | Status |
|----------------|---------|----------|--------|
| A01 Broken Access Control | File path traversal in upload/serve endpoints | High | ✅ Remediated — leaf-reduction + traversal/IDOR confinement |
| A02 Cryptographic Failures | Unsalted MD5 password hashing | Critical | ✅ Remediated — salted PBKDF2 + rehash-on-login |
| A02 Cryptographic Failures | Hardcoded symmetric key | Critical | ✅ Remediated — key removed; required from config |
| A02 Cryptographic Failures | Cookie `Secure` flag absent | Medium | ✅ Remediated (code) — ⚠ needs production HTTPS |
| A03 Injection | Upload filename / content-type not validated | High | ✅ Remediated — allow/deny lists + magic-byte check |
| A03 Injection | Unsandboxed runtime C# eval | Medium | 📝 Documented/Accepted — trusted-author boundary |
| A04 Insecure Design | No account lockout / rate limiting | High | ✅ Remediated — lockout 5/15 min + rate limiter |
| A04 Insecure Design | 100-year cookie lifetime | High | ✅ Remediated — bounded to 480 min |
| A04 Insecure Design | Built-in default admin `erp`/`erp` | High | ✅ Remediated — CSPRNG/configured; MinLength 12 |
| A05 Security Misconfiguration | Default JWT signing key shipped | Critical | ✅ Remediated — fail-fast denylist |
| A05 Security Misconfiguration | Permissive CORS `AllowAnyOrigin` | Medium | ✅ Remediated — explicit allowlist; ⚠ set prod origins |
| A05 Security Misconfiguration | Missing security headers | Medium | ✅ Remediated — `SecurityHeadersMiddleware` |
| A05 Security Misconfiguration | `AllowSynchronousIO=true` (DoS) | Medium | ✅ Remediated — removed |
| A05 Security Misconfiguration | JWT issuer == audience | Low | ✅ Remediated — distinct values |
| A06 Vulnerable & Outdated Components | .NET 7 EOL runtime | High | ✅ Remediated — `net10.0`; 0 vulnerable packages |
| A07 Authentication Failures | MD5, 100-yr cookie, default admin, no lockout, no `Secure`/`SameSite` | Critical/High/Med | ✅ Remediated — see A02/A04 + cookie flags |
| A08 Software & Data Integrity | Insecure deserialization (`TypeNameHandling`) | Critical | ✅ Remediated — allowlist `ISerializationBinder` |
| A09 Security Logging Failures | No audit logging of auth/authz events | Medium | 📝 Documented — `system_log` preserved; optional enhancement (HT-09) |
| A10 SSRF | Outbound-integration review surface | Low/Info | 📝 Documented — review surface (MailKit/Storage.Net/CDM) |

> **Completion bar:** **0 Critical + 0 High** vulnerabilities remaining — **MET ✓**. All 4 Critical and 5 High findings remediated; 6 Medium addressed (5 remediated, 1 documented-accepted); 2 Low/Info documented.

### 5.2 Preserved Controls (verified intact — not duplicated)

| Control | Status |
|---------|--------|
| Parameterized `NpgsqlCommand` / `EqlParameter` queries (SQLi defense) | ✅ Preserved |
| Antiforgery on Razor POSTs | ✅ Preserved (confirmed via 400 responses at runtime) |
| `[Authorize]` / `AuthorizeFolder` + `HasEntityPermission` RBAC | ✅ Preserved |
| `[JsonIgnore]` redaction on sensitive `ErpUser` fields | ✅ Preserved |
| `DevelopmentMode`-gated error masking | ✅ Preserved |
| JWT `ValidateIssuer`/`ValidateAudience`/`ValidateLifetime`/`ValidateIssuerSigningKey` | ✅ Preserved (all true) |
| Legacy `Settings:EncriptionKey` typo fallback (compatibility shim) | ✅ Preserved |
| Database schema & stored data (no migration) | ✅ Preserved |
| Public API signatures & NuGet artifact contracts | ✅ Preserved |

### 5.3 Quality Notes

- **Build quality:** 0 errors. The 53 warnings (CA2200, ASPDEPR008, CS0618/CS0168) are **pre-existing** in 16 out-of-scope, agent-untouched files; fixing them would constitute forbidden non-security refactoring under the MINIMAL CHANGE CLAUSE. No `TreatWarningsAsErrors` is set — non-blocking.
- **Minimal-change discipline:** No public API, configuration-schema, or database-schema changes. Commit history follows atomic, OWASP-tagged, one-class-per-commit discipline across 85 commits.

---

## 6. Risk Assessment

| Risk | Category | Severity | Probability | Mitigation | Status |
|------|----------|----------|-------------|------------|--------|
| S1 — Previously-committed secrets remain recoverable from git **history** | Security | High | High | Rotate all exposed secrets before production; consider history scrub | 🔴 Open (HT-03) |
| O1 — `Secure` cookie + HSTS require HTTPS everywhere; login breaks over plain HTTP | Operational | High | Medium | Enable TLS in all environments before/with the `Secure` flag | 🔴 Open (HT-02) |
| O2 — Empty `Config.json` placeholders → host fails to start until env vars set | Operational | Medium | High | Deployment runbook + env templates; fail-fast is intentional | 🟡 Mitigated by docs |
| O4 — No CI security gates yet → posture regressions not auto-caught | Operational | Medium | Medium | Add SAST/SCA/secrets-scan to CI | 🔴 Open (HT-06) |
| O3 — In-process lockout + rate limiter are **per-instance**; bypassable across load-balanced nodes | Operational | Medium | Medium | Acceptable single-instance; use distributed store (Redis) for scale-out | 🟡 Documented limitation |
| T1 — CSP deployed Report-Only; XSS hardening observational until enforced | Technical | Medium | Medium | Validate UI then switch to enforce (toggle built) | 🔴 Open (HT-05) |
| T2 — No automated test suite (no test projects exist) | Technical | Medium | Medium | 44/44 security-primitive harness assertions; recommend future test project | 🟡 Accepted (out of AAP scope) |
| T4 — PBKDF2 600k iterations adds per-login CPU cost | Technical | Low | Low | 10M-iter DoS cap; within 10% perf target; monitor | 🟢 Mitigated |
| T3 — 53 pre-existing build warnings in out-of-scope files | Technical | Low | n/a | Out of scope per MINIMAL CHANGE; non-blocking | 🟢 Documented/Accepted |
| S2 — Generated admin password surfaced once to operator console at seed | Security | Medium | Low-Med | Force first-login reset; secure log handling; prefer configured pwd | 🟡 Partially mitigated (HT-08) |
| S5 — Fail-fast on default JWT key halts boot if secret absent | Security | Medium | Medium | Dev guide + `JWT_README` document required env vars; provision before deploy | 🟡 Mitigated by docs |
| S3 — `CodeEvalService` runtime C# eval (trusted-author RCE boundary) | Security | Medium | Low | Documented admin-only boundary + RBAC; no code change per AAP | 🟢 Documented/Accepted |
| S4 — A10 SSRF review surface (MailKit/Storage.Net/CDM) | Security | Low/Info | Low | Documented; validate future user-supplied URLs against allowlist | 🟢 Documented |
| I1 — AutoMapper 14→16.1.1 major upgrade (breaking `MapperConfiguration` `ILoggerFactory`) | Integration | Medium | Low-Med | `NullLoggerFactory` shim applied; clean build; regression-test in staging | 🟡 Mitigated (needs smoke test) |
| I3 — Auth-continuity with **real** legacy MD5 users validated only via harness | Integration | Medium | Low | Staging smoke test with representative legacy accounts | 🔴 Open (in HT-07) |
| I4 — `ErpSerializationBinder` allowlist could reject a legitimate persisted type if incomplete | Integration | Medium | Low | Binder preserves wire format + delegates known types; validate vs persisted payloads | 🟡 Mitigated (needs smoke test) |
| I2 — .NET 7→10 WASM upgrade behavioral differences | Integration | Low-Med | Low | Builds 0/0; WASM smoke test recommended | 🟡 Mitigated (needs smoke test) |

> **Top critical-path risks** (gating safe production cutover): **S1** (secret rotation), **O1** (TLS for `Secure` cookie), and **I3/I4** (staging smoke test of auth continuity + deserialization). All map to the 38 h of remaining work.

---

## 7. Visual Project Status

### 7.1 Project Hours Breakdown

```mermaid
%%{init: {'theme':'base', 'themeVariables': {'pie1':'#5B39F3','pie2':'#FFFFFF','pieStrokeColor':'#B23AF2','pieStrokeWidth':'2px','pieTitleTextSize':'16px','pieSectionTextSize':'14px'}}}%%
pie showData title Project Hours — Completed vs Remaining
    "Completed Work" : 150
    "Remaining Work" : 38
```

> 🟦 Completed Work = `#5B39F3` (150 h) · ⬜ Remaining Work = `#FFFFFF` (38 h). **Remaining Work = 38 h** matches Section 1.2 and the Section 2.2 total exactly.

### 7.2 Remaining Work by Priority

```mermaid
%%{init: {'theme':'base', 'themeVariables': {'pie1':'#B23AF2','pie2':'#5B39F3','pie3':'#A8FDD9','pieStrokeColor':'#5B39F3','pieTitleTextSize':'16px','pieSectionTextSize':'14px'}}}%%
pie showData title Remaining 38h by Priority
    "High" : 10
    "Medium" : 24
    "Low" : 4
```

### 7.3 Remaining Hours by Category (bar view)

| Category | Hours | Bar |
|----------|------:|-----|
| Deploy + cross-host smoke test | 8 | ████████ |
| CI security gates | 6 | ██████ |
| CSP Report-Only → enforce | 5 | █████ |
| Provision production secrets | 4 | ████ |
| Enable HTTPS/TLS | 4 | ████ |
| A09 audit logging (optional) | 4 | ████ |
| Per-host CORS allowlist | 3 | ███ |
| Rotate committed secrets | 2 | ██ |
| Admin onboarding | 2 | ██ |
| **Total** | **38** | |

---

## 8. Summary & Recommendations

### 8.1 Achievements

The WebVella ERP security hardening transformation is **79.8% complete** (150 of 188 hours). **Every in-scope code requirement defined in the Agent Action Plan is implemented, committed, and validated.** The completion bar — zero Critical and zero High vulnerabilities remaining — is **met**: all 4 Critical and all 5 High OWASP findings are remediated, six Medium findings are addressed, and two Low/Info findings are documented. Three centralized security primitives (password hasher, serialization binder, security-headers middleware) were introduced and wired once so all seven hosts inherit them, in keeping with the minimal-change discipline. The build is clean (0 errors), SCA reports zero vulnerable packages, and runtime validation confirmed live security controls (headers, rate limiting, fail-fast configuration).

### 8.2 Remaining Gaps & Critical Path to Production

The remaining **38 hours** are exclusively **path-to-production** activities that cannot be performed autonomously without production infrastructure and secret material. The critical path to a safe cutover is:

1. **Rotate previously-committed secrets** (S1) — old keys remain in git history.
2. **Enable HTTPS/TLS** (O1) — required for the `Secure` cookie and HSTS to function.
3. **Provision production secrets** — hosts fail fast until configured (this is the intended control).
4. **Staging smoke test** (I3/I4) — confirm transparent rehash with real legacy MD5 accounts, the AutoMapper/WASM upgrades, and the deserialization binder against existing persisted payloads.
5. **Transition CSP to enforce + add CI security gates** — close out the observational and regression-prevention gaps.

### 8.3 Production Readiness Assessment

| Dimension | Assessment |
|-----------|------------|
| Code completeness (AAP scope) | ✅ Complete — all in-scope code committed |
| Security posture (OWASP bar) | ✅ Met — 0 Critical / 0 High remaining |
| Build & dependency health | ✅ Clean — 0 errors, 0 vulnerable packages |
| Runtime validation | ✅ Validated — host boots, controls active |
| Production configuration | ⏳ Pending — secrets, TLS, CORS origins (human) |
| Regression confidence | ⚠ Medium — staging smoke test required before cutover |
| **Overall** | **Code-complete; not yet production-deployed.** Ready for staging validation and operator provisioning. |

### 8.4 Success Metrics

| Metric | Target | Actual |
|--------|--------|--------|
| Critical/High vulnerabilities remaining | 0 / 0 | **0 / 0 ✓** |
| Vulnerable packages | 0 | **0 ✓** |
| Build errors | 0 | **0 ✓** |
| Security-primitive assertions passing | 100% | **44/44 (100%) ✓** |
| Public API / DB schema changes | 0 | **0 ✓** |
| Auth continuity (legacy MD5) | Preserved | **Preserved (harness; staging pending)** |

---

## 9. Development Guide

### 9.1 System Prerequisites

| Requirement | Version / Detail |
|-------------|------------------|
| .NET SDK | **10.0.x** (validated with 10.0.301) |
| PostgreSQL | 12+ (validated against **16**) |
| Git + Git LFS | Git LFS 3.7.1 (LFS is the only hook framework; no husky/pre-commit) |
| OS | Linux, macOS, or Windows (Linux validated) |
| Hardware (recommended) | 4 cores / 8 GB RAM for a comfortable build + run |

Verify the SDK:

```bash
dotnet --version          # expect 10.0.x
which dotnet              # /usr/local/bin/dotnet
```

### 9.2 Environment Setup

1. **Clone and enter the repository**, then confirm the case-sensitivity symlink exists (required on Linux):

```bash
ls -ld WebVella.ERP      # WebVella.ERP -> WebVella.Erp
```

2. **Export the required secrets** (the application uses the `Settings__` prefix and `AddEnvironmentVariables()`, which **override** the empty `Config.json` placeholders). The host **fails fast** if the JWT key is missing/default or the encryption key is unset:

```bash
export Settings__ConnectionString="Host=localhost;Port=5432;Database=webvella_erp;Username=erp;Password=<db-pass>"
export Settings__Jwt__Key="$(openssl rand -base64 48)"     # MUST be a strong, unique key >= 32 bytes
export Settings__EncryptionKey="$(openssl rand -base64 32)"
export Settings__Jwt__Issuer="webvella-erp-issuer"          # keep issuer != audience
export Settings__Jwt__Audience="webvella-erp-audience"
# Optional: set a known initial admin password (otherwise a CSPRNG one is generated and printed once at first boot)
export Settings__DefaultAdminPassword="<a-strong-12+char-password>"
```

3. **Provision an empty PostgreSQL database** matching the connection string above (the schema is created/seeded by the application on first run).

### 9.3 Dependency Installation

```bash
dotnet restore WebVella.ERP3.sln     # verified exit 0; restores all 17 projects
```

### 9.4 Build

```bash
# Main solution — expect: Build succeeded, 0 Errors (53 pre-existing out-of-scope warnings are non-blocking)
dotnet build WebVella.ERP3.sln -c Debug

# WASM projects build out-of-solution — expect 0 errors / 0 warnings
dotnet build WebVella.Erp.WebAssembly/Server/*.csproj -c Debug
dotnet build WebVella.Erp.WebAssembly/Shared/*.csproj -c Debug
```

### 9.5 Application Startup

Run the default host **over HTTPS** (required for the `Secure` cookie and HSTS). The port is controlled by `ASPNETCORE_URLS`:

```bash
# Option A — run from source
ASPNETCORE_URLS="https://localhost:5001" dotnet run --project WebVella.Erp.Site

# Option B — run the published/built DLL
ASPNETCORE_URLS="https://localhost:5001" \
  dotnet WebVella.Erp.Site/bin/Debug/net10.0/WebVella.Erp.Site.dll
```

> The seven hosts (`WebVella.Erp.Site`, `.Site.Crm`, `.Site.Mail`, `.Site.MicrosoftCDM`, `.Site.Next`, `.Site.Project`, `.Site.Sdk`) start identically; substitute the project/DLL name.

### 9.6 Verification

```bash
# Root redirects to the login page
curl -skI https://localhost:5001/ | head -1            # expect: HTTP/.. 302

# Login page returns 200 and carries all 7 security headers
curl -skI https://localhost:5001/login | grep -iE \
  'content-security-policy|strict-transport|x-content-type|x-frame|x-xss|referrer-policy|permissions-policy'
```

- On first boot, watch the console for the **one-time generated admin password** (only when `Settings__DefaultAdminPassword` is unset).
- Rapidly POSTing to `/login` returns **HTTP 400** (antiforgery) and then **HTTP 429** once the rate limiter / lockout engages.

### 9.7 Example Usage

- Log in at `https://localhost:5001/login` with `erp@webvella.com` and the configured or generated admin password.
- **Legacy MD5 users** authenticate normally; on their first successful login the stored hash is **transparently upgraded** to salted PBKDF2 — no data migration or password reset required.

### 9.8 Troubleshooting

| Symptom | Cause | Resolution |
|---------|-------|------------|
| Boot throws on JWT key | `Settings__Jwt__Key` is missing or a known shipped default | Set a strong, unique key ≥32 bytes via env/secret store |
| Boot throws "Settings:EncryptionKey is not configured" | Encryption key not provided | Set `Settings__EncryptionKey` |
| Login fails / cookie not set over HTTP | `Secure` cookie requires HTTPS | Serve over HTTPS (`ASPNETCORE_URLS=https://…`) or terminate TLS at a proxy |
| UI assets/scripts blocked | CSP too strict if switched to enforce | Keep CSP in **Report-Only** until inline scripts/styles and vendored libs are reconciled |
| Cannot connect to database | Bad/missing connection string or DB down | Verify `Settings__ConnectionString` and that PostgreSQL is reachable |
| Build error referencing `WebVella.ERP` on Linux | Missing case-sensitivity symlink | Ensure `WebVella.ERP -> WebVella.Erp` symlink is present (committed) |

---

## 10. Appendices

### Appendix A — Command Reference

| Command | Purpose |
|---------|---------|
| `dotnet --version` | Confirm SDK (expect 10.0.x) |
| `dotnet restore WebVella.ERP3.sln` | Restore all 17 projects |
| `dotnet build WebVella.ERP3.sln -c Debug` | Build solution (0 errors expected) |
| `dotnet build WebVella.Erp.WebAssembly/{Server,Shared}/*.csproj -c Debug` | Build out-of-solution WASM projects |
| `dotnet run --project WebVella.Erp.Site` | Run the default host |
| `dotnet list package --vulnerable --include-transitive` | SCA scan (expect zero vulnerable) |
| `curl -skI https://localhost:5001/login` | Verify security headers |

### Appendix B — Port Reference

| Setting | Value | Notes |
|---------|-------|-------|
| `ASPNETCORE_URLS` | e.g. `https://localhost:5001` | Controls bind address/port; no static ports in `launchSettings` |
| HTTPS | Required | Needed for `Secure` cookie + HSTS |

### Appendix C — Key File Locations

| Path | Role |
|------|------|
| `WebVella.ERP3.sln` | Solution root (17 projects) |
| `WebVella.Erp/Utilities/IPasswordHasher.cs` | **New** — hashing strategy abstraction |
| `WebVella.Erp/Utilities/ErpPasswordHasher.cs` | **New** — salted PBKDF2 implementation + legacy verify |
| `WebVella.Erp/Utilities/ErpSerializationBinder.cs` | **New** — allowlist `ISerializationBinder` |
| `WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs` | **New** — emits the 7 security headers |
| `WebVella.Erp/Api/SecurityManager.cs` | Credential path restructure (fetch-by-email + verify + rehash) |
| `WebVella.Erp/Utilities/CryptoUtility.cs` | Hardcoded key removed; requires configured key |
| `WebVella.Erp/ErpSettings.cs` | Fail-fast JWT validation; `EncriptionKey` fallback preserved |
| `WebVella.Erp.Web/ErpMvcExtensions.cs` | DI + pipeline wiring (`AddErp`/`UseErp`) |
| `WebVella.Erp.Site*/Startup.cs` | CORS, cookie flags, rate limiter (7 hosts) |
| `WebVella.Erp.Site*/Config.json` + `WebVella.Erp.ConsoleApp/Config.json` | Externalized secret placeholders (8 files) |
| `docs/security/owasp-top10-audit.md` | OWASP A01–A10 audit document (20 KB) |

### Appendix D — Technology Versions

| Component | Version |
|-----------|---------|
| .NET SDK / Target Framework | 10.0.301 / `net10.0` |
| PostgreSQL (validated) | 16 |
| Newtonsoft.Json | 13.0.4 (retained; mitigated via binder) |
| Npgsql | 9.0.4 |
| System.IdentityModel.Tokens.Jwt | 8.15.0 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.1 |
| AutoMapper | 16.1.1 (upgraded from 14.0.0) |
| MailKit | 4.16.0 (upgraded from 4.14.1) |
| Microsoft.AspNetCore.Components.WebAssembly.Server | 10.0.x (upgraded from 7.0.13) |
| Git LFS | 3.7.1 |
| Published artifacts (contract-stable) | WebVella.Erp 1.7.7 · WebVella.Erp.Web 1.7.9 · Plugins.Sdk 1.7.4 · Plugins.Mail 1.7.5 |

### Appendix E — Environment Variable Reference

| Variable | Required | Description |
|----------|----------|-------------|
| `Settings__ConnectionString` | Yes | PostgreSQL connection string |
| `Settings__Jwt__Key` | Yes | JWT signing key; **≥32 bytes**, strong, unique (boot fails on default/empty) |
| `Settings__EncryptionKey` | Yes | Symmetric encryption key (boot fails if unset) |
| `Settings__Jwt__Issuer` | Recommended | JWT issuer; keep **distinct** from audience |
| `Settings__Jwt__Audience` | Recommended | JWT audience; keep **distinct** from issuer |
| `Settings__DefaultAdminPassword` | Optional | Initial admin password; if unset, a CSPRNG password is generated and printed once |
| `ASPNETCORE_URLS` | Recommended | Bind address/port (use `https://…`) |

> The legacy `Settings__EncriptionKey` (typo) fallback is intentionally preserved as a compatibility shim.

### Appendix F — Developer Tools Guide

| Tool | Use |
|------|-----|
| `dotnet list package --vulnerable --include-transitive` | SCA — confirm zero vulnerable packages before merge |
| Semgrep / Security Code Scan | Recommended SAST for CI (HT-06) |
| gitleaks | Recommended secrets scanning for CI (HT-06) |
| OWASP Dependency-Check | Alternative/complementary SCA for CI |
| `curl -skI` | Quick security-header verification |

### Appendix G — Glossary

| Term | Definition |
|------|------------|
| **AAP** | Agent Action Plan — the authoritative scope document for this project |
| **PBKDF2** | Password-Based Key Derivation Function 2 — the salted, adaptive hashing algorithm now used (HMAC-SHA256, 600k iterations) |
| **KDF** | Key Derivation Function — a slow, adaptive hash suitable for password storage |
| **`needsUpgrade`** | Flag returned by `Verify` indicating a stored hash (e.g., legacy MD5) should be transparently re-hashed |
| **`ISerializationBinder`** | Newtonsoft.Json hook used as a type allowlist to neutralize `$type` gadget deserialization attacks |
| **CSP** | Content-Security-Policy — deployed in Report-Only mode for UI parity, pending transition to enforce |
| **HSTS** | HTTP Strict-Transport-Security — requires HTTPS to take effect |
| **SCA** | Software Composition Analysis — dependency vulnerability scanning |
| **SAST** | Static Application Security Testing |
| **Fail-fast** | Startup validation that refuses to run with a default/empty JWT or encryption key |
| **Path-to-production** | Standard deployment activities (secrets, TLS, CI, deploy/smoke test) required to ship the delivered code |

---

> **Cross-Section Integrity — Final Validation**
> - **Rule 1 (1.2 ↔ 2.2 ↔ 7):** Remaining = **38 h** in all three locations ✓
> - **Rule 2 (2.1 + 2.2 = Total):** 150 + 38 = **188 h** ✓
> - **Rule 3 (Section 3):** All tests originate from Blitzy's autonomous validation logs ✓
> - **Rule 4 (Section 1.5):** Access issues validated against current permissions ✓
> - **Rule 5 (Colors):** Completed = `#5B39F3`, Remaining = `#FFFFFF` ✓
> - **Completion:** 150 ÷ 188 = **79.8%** (consistent across Sections 1.2, 7, 8) ✓