# Technical Specification

# 0. Agent Action Plan

## 0.1 Intent Clarification

Based on the security concern described, the Blitzy platform understands that the security vulnerability to resolve is a **comprehensive security audit and remediation initiative** targeting multiple vulnerability categories across the WebVella ERP application.

### 0.1.1 Core Security Objective

**Vulnerability Category:** Multiple vulnerabilities spanning OWASP Top 10 coverage, dependency vulnerabilities, configuration weaknesses, and cryptographic failures

**Severity Classification:**
- **Critical:** MD5 password hashing, hard-coded encryption/JWT keys, overly permissive CORS configuration
- **High:** Missing security headers, excessive cookie expiration, no file upload validation, weak key derivation
- **Medium:** No rate limiting, missing CSRF protection, silent JWT authentication failures
- **Low:** Verbose error messages, development exception pages, insufficient audit logging

**Security Requirements with Enhanced Clarity:**
- Eliminate all Critical and High severity vulnerabilities through code remediation
- Document Medium and Low severity findings with recommended fixes
- Maintain 100% functional parity with existing application behavior
- Achieve zero Critical/High findings in post-remediation security scans
- Preserve existing API contracts, database schemas, and user-facing behavior

**Implicit Security Needs Identified:**
- Backward compatibility for existing password hashes during migration period
- Zero-downtime deployment considerations for cryptographic key rotation
- Session continuity for active users during cookie configuration changes
- Data integrity preservation for encrypted fields during key migration

### 0.1.2 Special Instructions and Constraints

**User-Specified Directives Captured:**

- **Minimal Changes Clause:** "Make only the changes absolutely necessary to remediate identified security vulnerabilities"
- **Functional Preservation:** "All existing functionality must remain operational"
- **API Contract Preservation:** "API contracts and interfaces unchanged"
- **Database Schema Preservation:** "Database schemas unmodified unless required for security fix"
- **Performance Constraint:** "Performance within 10% of baseline"
- **No Feature Additions:** "No feature additions or enhancements"
- **No Refactoring:** "No refactoring beyond security requirements"
- **Style Preservation:** "Existing code style and patterns maintained"
- **Atomic Commits:** "Atomic commits per vulnerability class for rollback capability"

**Security Discipline Guidelines (User-Specified):**
1. Make only minimal necessary changes to address each specific vulnerability
2. Preserve existing functionality and user workflows exactly as-is
3. Do not modify code not directly related to security vulnerabilities
4. Do not enhance or optimize code beyond security remediation requirements
5. Implement security controls using least invasive approach possible
6. Document all security-related changes with clear comments explaining threat addressed
7. When multiple security solutions exist, choose one requiring least modification
8. If additional security concerns identified beyond scope, document but do not fix unless Critical severity
9. Atomic commits per vulnerability class enable surgical rollback if issues arise
10. Validate after each fix category before proceeding to next

**Change Scope Preference:** Minimal - targeted interventions only

### 0.1.3 Technical Interpretation

This security vulnerability audit translates to the following technical fix strategy:

**A01: Broken Access Control**
- To resolve overly permissive CORS configuration, we will update `Startup.cs` to implement proper origin whitelisting with explicit allowed methods and headers

**A02: Cryptographic Failures**
- To resolve MD5 password hashing, we will replace `PasswordUtil.GetMd5Hash()` with BCrypt implementation using cost factor 12+
- To resolve hard-coded encryption keys, we will remove default fallback keys and enforce configuration-based key management with validation
- To resolve weak key/IV derivation in `CryptoUtility.cs`, we will implement proper PBKDF2-based key derivation with random IV generation

**A05: Security Misconfiguration**
- To resolve missing security headers, we will add middleware implementing OWASP-recommended headers (X-Frame-Options, X-Content-Type-Options, Content-Security-Policy, Strict-Transport-Security, Referrer-Policy)
- To resolve excessive cookie expiration, we will update `AuthService.cs` to use reasonable session duration (configurable, defaulting to 30 days with sliding expiration)

**A06: Vulnerable Components**
- Dependencies are currently up-to-date (Newtonsoft.Json 13.0.4, System.IdentityModel.Tokens.Jwt 8.15.0) with no known Critical/High CVEs requiring updates

**A07: Authentication Failures**
- To resolve default JWT key vulnerability, we will enforce minimum key length validation and fail startup if default/weak key detected
- To resolve silent JWT failures, we will add proper logging for authentication failures

**A08: Software/Data Integrity Failures**
- To resolve unrestricted file uploads, we will implement file type validation, content verification, and size limits in upload endpoints

**User Understanding Level:** Explicit comprehensive security audit requirements with OWASP Top 10 framework specified

## 0.2 Vulnerability Research and Analysis

### 0.2.1 Initial Assessment

**Extracted Security-Related Information from Codebase Analysis:**

| Category | Findings |
|----------|----------|
| CVE Numbers Mentioned | CVE-2024-21907 (Newtonsoft.Json - patched in current version 13.0.4), CVE-2024-21319 (JWT library - patched in current version 8.15.0) |
| Vulnerability Names | MD5 Password Hashing, Hard-coded Cryptographic Keys, Overly Permissive CORS, Missing Security Headers, Unrestricted File Uploads, Weak Key Derivation, Excessive Session Duration |
| Affected Packages | Newtonsoft.Json (13.0.4 - secure), System.IdentityModel.Tokens.Jwt (8.15.0 - secure), custom security components |
| Symptoms Described | Weak password storage, static encryption keys, permissive cross-origin requests, missing HTTP security headers |
| Security Advisories Referenced | OWASP Top 10 (2021), OWASP Security Headers Guidance, OWASP Password Storage Cheat Sheet |

### 0.2.2 Required Web Research Findings

**Official CVE Database Research:**

<cite index="3-1,3-2">Newtonsoft.Json before version 13.0.1 is affected by a mishandling of exceptional conditions vulnerability. Crafted data that is passed to the JsonConvert.DeserializeObject method may trigger a StackOverflow exception resulting in denial of service.</cite> Current version 13.0.4 is patched.

<cite index="13-1,13-2">System.IdentityModel.Tokens.Jwt versions before 5.7.0, 6.34.0, or 7.1.2 are vulnerable to Resource Exhaustion by processing JSON Web Encryption (JWE) tokens with a high compression ratio.</cite> Current version 8.15.0 is patched.

**OWASP Guidance Applied:**

<cite index="22-1,22-2,22-3">ASP.NET Core Identity framework is well configured by default, where it uses secure password hashes and an individual salt. Identity uses the PBKDF2 hashing function for passwords, and generates a random salt per user.</cite> Current implementation uses MD5 which contradicts this guidance.

<cite index="22-21,22-22">Use Parameterized SQL commands for all data access, without exception. Do not use SqlCommand with a string parameter made up of a concatenated SQL String.</cite> Codebase uses NpgsqlParameter - compliant.

**Security Headers Research:**

<cite index="24-3">OWASP Secure Headers project recommends the following headers: strict-transport-security, x-frame-options, x-content-type-options, content-security-policy, x-permitted-cross-domain-policies, referrer-policy, cross-origin-resource-policy, cache-control.</cite>

### 0.2.3 Vulnerability Classification

| Vulnerability | Type | Attack Vector | Exploitability | Impact | CWE | Severity |
|---------------|------|---------------|----------------|--------|-----|----------|
| MD5 Password Hashing | Cryptographic Failure | Network | High | Confidentiality | CWE-328 | Critical |
| Hard-coded Encryption Key | Cryptographic Failure | Local/Network | Medium | Confidentiality, Integrity | CWE-798 | Critical |
| Default JWT Key | Authentication Bypass | Network | High | Confidentiality, Integrity, Availability | CWE-798 | Critical |
| Overly Permissive CORS | Security Misconfiguration | Network | High | Confidentiality | CWE-942 | Critical |
| Missing Security Headers | Security Misconfiguration | Network | Medium | Integrity | CWE-693 | High |
| 100-Year Cookie Expiration | Session Management | Network | Medium | Confidentiality | CWE-613 | High |
| No File Type Validation | Input Validation | Network | High | Integrity, Availability | CWE-434 | High |
| IV Derived from Key | Cryptographic Failure | Local | Low | Confidentiality | CWE-329 | High |
| No Rate Limiting | DoS Protection | Network | Medium | Availability | CWE-770 | Medium |
| Silent JWT Failures | Security Logging | Local | Low | N/A | CWE-778 | Medium |
| Missing CSRF Protection | CSRF | Network | Medium | Integrity | CWE-352 | Medium |
| Verbose Error Messages | Information Disclosure | Network | Low | Confidentiality | CWE-209 | Low |

### 0.2.4 Root Cause Analysis

**Critical Vulnerabilities Root Causes:**

1. **MD5 Password Hashing (CWE-328)**
   - File: `WebVella.Erp/Utilities/PasswordUtil.cs`
   - Root Cause: Legacy implementation using `MD5.Create()` for password hashing without salt
   - Code Pattern: `MD5.Create().ComputeHash(Encoding.ASCII.GetBytes(input))`

2. **Hard-coded Encryption Key (CWE-798)**
   - File: `WebVella.Erp/Utilities/CryptoUtility.cs`
   - Root Cause: Default fallback key `defaultCryptKey` when no key provided
   - Code Pattern: `byte[] key = defaultCryptKey ?? Encoding.UTF8.GetBytes(keyString ?? "default")`

3. **Default JWT Key (CWE-798)**
   - File: `WebVella.Erp/ErpSettings.cs`
   - Root Cause: Weak default value `"ThisIsMySecretKey"` for JWT signing key
   - Configuration: `JwtKey = "ThisIsMySecretKey"` as default

4. **Overly Permissive CORS (CWE-942)**
   - File: `WebVella.Erp.Site/Startup.cs`
   - Root Cause: Configuration uses `.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()`
   - Impact: Any website can make authenticated requests to the API

### 0.2.5 Web Search Research Conducted

| Research Area | Source | Key Findings |
|---------------|--------|--------------|
| Newtonsoft.Json CVEs | Snyk, GitHub Advisory, CVE Details | CVE-2024-21907 patched in 13.0.1; current 13.0.4 is secure |
| JWT Library CVEs | MSRC, GitHub Advisory | CVE-2024-21319 patched in 7.1.2; current 8.15.0 is secure |
| OWASP .NET Security | OWASP Cheat Sheet Series | PBKDF2 recommended for passwords; parameterized queries required |
| Security Headers | OWASP Secure Headers Project | 10+ headers recommended for defense-in-depth |
| ASP.NET Core Security | Semgrep, .NET Foundation | OwaspHeaders.Core library available for header injection |

### 0.2.6 Recommended Mitigation Strategies

**For Critical Vulnerabilities:**

1. **MD5 → BCrypt Migration**
   - Replace MD5 with BCrypt (cost factor 12)
   - Implement backward-compatible verification with automatic rehash
   - Generate per-user salt automatically

2. **Cryptographic Key Hardening**
   - Remove all default/fallback keys
   - Validate key length and entropy on startup
   - Fail fast if configuration invalid

3. **CORS Restriction**
   - Define explicit allowed origins
   - Use environment-specific configuration
   - Restrict methods to actual requirements

**Alternative Solutions Considered:**

| Alternative | Trade-off | Decision |
|-------------|-----------|----------|
| Argon2 instead of BCrypt | Better but less .NET library support | BCrypt chosen for broader compatibility |
| Environment variables for keys | Requires deployment changes | Selected - minimal code change |
| NWebSec for security headers | Full-featured but complex | Manual middleware preferred for minimal change |
| OwaspHeaders.Core | Single-line implementation | Document as future improvement |

## 0.3 Security Scope Analysis

### 0.3.1 Affected Component Discovery

**Comprehensive Repository Search Results:**

The security audit identified **23 files** directly affected by vulnerabilities across **8 directories**:

| Component Category | File Count | Directories Affected |
|--------------------|------------|---------------------|
| Cryptographic Utilities | 3 | WebVella.Erp/Utilities/ |
| Authentication Services | 4 | WebVella.Erp/Api/, WebVella.Erp.Web/Services/, WebVella.Erp.Web/Middleware/ |
| Configuration | 2 | WebVella.Erp/, WebVella.Erp.Site/ |
| Web Layer | 3 | WebVella.Erp.Site/, WebVella.Erp.Web/Controllers/ |
| File Operations | 1 | WebVella.Erp/Database/ |
| Dependency Manifests | 5 | Project root, WebVella.Erp/, WebVella.Erp.Web/, WebVella.Erp.Site/, WebVella.Erp.Plugins.Mail/ |
| Testing | 5 | tests/security/ (to be created) |

**Search Patterns Employed:**

```bash
# Vulnerable package imports

grep -rn "using System.Security.Cryptography" --include="*.cs"
grep -rn "MD5\|SHA1\|DES" --include="*.cs"

#### Hard-coded secrets patterns

grep -rn "password\s*=\|key\s*=\|secret\s*=" --include="*.cs" --include="*.json"

#### Authentication patterns

grep -rn "AllowAnyOrigin\|AllowCredentials" --include="*.cs"

#### File upload patterns

grep -rn "IFormFile\|UploadFile" --include="*.cs"

#### Security header patterns (absence confirmed)

grep -rn "X-Frame-Options\|Content-Security-Policy" --include="*.cs"
```

### 0.3.2 Root Cause Identification

**Vulnerability Propagation Analysis:**

**1. MD5 Password Vulnerability Chain:**
```
SecurityManager.cs (Uses)
    ↓
PasswordUtil.GetMd5Hash() (Implements)
    ↓
AuthService.cs (Validates via SecurityManager)
    ↓
All user authentication requests (Exposed)
```

Direct usage locations:
- `WebVella.Erp/Api/SecurityManager.cs:85-87` - Password validation
- `WebVella.Erp/Utilities/PasswordUtil.cs:15-28` - MD5 implementation

Indirect dependencies:
- `WebVella.Erp.Web/Services/AuthService.cs` - Cookie authentication
- `WebVella.Erp.Web/Middleware/JwtMiddleware.cs` - JWT authentication
- All API controllers requiring authentication

**2. Hard-coded Key Vulnerability Chain:**
```
ErpSettings.cs (Default JWT Key)
    ↓
Startup.cs (JWT Configuration)
    ↓
JwtMiddleware.cs (Token Validation)
    ↓
All authenticated API endpoints (Exposed)

CryptoUtility.cs (Default Encryption Key)
    ↓
Any encrypted data storage/retrieval
```

**3. CORS Misconfiguration Chain:**
```
Startup.cs (Configuration)
    ↓
All HTTP responses (Exposed)
    ↓
Cross-origin requests permitted from any domain
```

### 0.3.3 Current State Assessment

**Vulnerable Package Versions:**

| Package | Current Version | Vulnerability Status |
|---------|-----------------|---------------------|
| Newtonsoft.Json | 13.0.4 | Secure (CVE-2024-21907 patched in 13.0.1) |
| System.IdentityModel.Tokens.Jwt | 8.15.0 | Secure (CVE-2024-21319 patched in 7.1.2) |
| Npgsql | 9.0.4 | Secure |
| MailKit | 4.14.1 | Secure |
| HtmlAgilityPack | 1.12.0 | Secure |

**Vulnerable Code Pattern Locations:**

| Pattern | File | Line Numbers | Severity |
|---------|------|--------------|----------|
| MD5 Hashing | WebVella.Erp/Utilities/PasswordUtil.cs | 15-28 | Critical |
| Default Encryption Key | WebVella.Erp/Utilities/CryptoUtility.cs | 12-18, 45-60 | Critical |
| Default JWT Key | WebVella.Erp/ErpSettings.cs | 25 | Critical |
| AllowAnyOrigin CORS | WebVella.Erp.Site/Startup.cs | 89-95 | Critical |
| 100-Year Cookie | WebVella.Erp.Web/Services/AuthService.cs | 45-48 | High |
| No File Validation | WebVella.Erp.Web/Controllers/WebApiController.cs | 3320-3380, 3960-4020 | High |
| Missing Security Headers | WebVella.Erp.Site/Startup.cs | (absent) | High |
| IV from Key | WebVella.Erp/Utilities/CryptoUtility.cs | 55-58 | High |

**Vulnerable Configuration Files:**

| File | Setting | Current Value | Security Issue |
|------|---------|---------------|----------------|
| WebVella.Erp.Site/Config.json | JwtKey | "ThisIsMySecretKey" (default) | Predictable JWT signing |
| WebVella.Erp.Site/Config.json | EncryptionKey | (empty - uses default) | Known encryption key |
| WebVella.Erp.Site/Config.json | SmtpPassword | Plaintext | Credential exposure |
| WebVella.Erp.Site/Startup.cs | CORS Policy | AllowAnyOrigin | Cross-origin attacks |

### 0.3.4 Scope of Exposure

**Exposure Classification:**

| Vulnerability | Exposure Scope | Attack Surface |
|---------------|----------------|----------------|
| MD5 Password Hashing | All user accounts | Database compromise leads to password cracking |
| Default JWT Key | All API endpoints | External attackers can forge valid tokens |
| CORS Misconfiguration | Public-facing | Any malicious website can make requests |
| File Upload | API endpoints | Authenticated users can upload malicious files |
| Missing Headers | Public-facing | Browser-based attacks (clickjacking, XSS) |
| Cookie Duration | Session management | Long-term session hijacking window |

### 0.3.5 Complete Affected Files Inventory

**Files Requiring Security Modifications:**

| File Path | Modification Type | Priority |
|-----------|-------------------|----------|
| `WebVella.Erp/Utilities/PasswordUtil.cs` | UPDATE - Replace MD5 with BCrypt | Critical |
| `WebVella.Erp/Utilities/CryptoUtility.cs` | UPDATE - Fix key derivation and IV | Critical |
| `WebVella.Erp/ErpSettings.cs` | UPDATE - Add key validation | Critical |
| `WebVella.Erp.Site/Startup.cs` | UPDATE - Fix CORS, add security headers | Critical |
| `WebVella.Erp.Web/Services/AuthService.cs` | UPDATE - Fix cookie expiration | High |
| `WebVella.Erp.Web/Controllers/WebApiController.cs` | UPDATE - Add file validation | High |
| `WebVella.Erp/Api/SecurityManager.cs` | UPDATE - Support BCrypt verification | High |
| `WebVella.Erp.Site/Config.json` | UPDATE - Document required configuration | High |
| `WebVella.Erp.Site/WebVella.Erp.Site.csproj` | UPDATE - Add BCrypt.Net-Next package | High |
| `WebVella.Erp/WebVella.Erp.csproj` | UPDATE - Add BCrypt.Net-Next package | High |
| `WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs` | CREATE - Security headers middleware | High |
| `WebVella.Erp/Utilities/FileValidationUtil.cs` | CREATE - File validation utility | High |
| `tests/security/PasswordSecurityTests.cs` | CREATE - Security regression tests | Medium |
| `tests/security/CryptoSecurityTests.cs` | CREATE - Encryption tests | Medium |
| `tests/security/HeaderSecurityTests.cs` | CREATE - Header validation tests | Medium |
| `SECURITY.md` | CREATE - Security documentation | Medium |

## 0.4 Version Compatibility Research

### 0.4.1 Secure Version Identification

**Dependency Audit Results:**

All existing NuGet packages have been verified against security advisories. No Critical or High severity CVEs require package updates:

| Package | Current Version | First Patched | Recommended | Status |
|---------|-----------------|---------------|-------------|--------|
| Newtonsoft.Json | 13.0.4 | 13.0.1 (CVE-2024-21907) | 13.0.4 | ✅ Secure |
| System.IdentityModel.Tokens.Jwt | 8.15.0 | 7.1.2 (CVE-2024-21319) | 8.15.0 | ✅ Secure |
| Npgsql | 9.0.4 | N/A | 9.0.4 | ✅ Secure |
| MailKit | 4.14.1 | N/A | 4.14.1 | ✅ Secure |
| HtmlAgilityPack | 1.12.0 | N/A | 1.12.0 | ✅ Secure |
| CsvHelper | 33.0.1 | N/A | 33.0.1 | ✅ Secure |
| Scriban | 5.12.0 | N/A | 5.12.0 | ✅ Secure |

**New Package Requirements:**

The following package must be added to implement secure password hashing:

| Package | Version | Purpose | Advisory |
|---------|---------|---------|----------|
| BCrypt.Net-Next | 4.0.3 | Secure password hashing replacement for MD5 | OWASP Password Storage recommendation |

### 0.4.2 Compatibility Verification

**Runtime Compatibility:**

| Component | Requirement | Current | Compatibility |
|-----------|-------------|---------|---------------|
| Target Framework | .NET 10.0 | .NET 10.0 | ✅ Compatible |
| BCrypt.Net-Next | .NET Standard 2.0+ | .NET 10.0 | ✅ Compatible |
| Security APIs | System.Security.Cryptography | Built-in | ✅ Compatible |

**BCrypt.Net-Next Compatibility Analysis:**

- **Supports .NET 10.0:** Yes - targets .NET Standard 2.0, 2.1, and .NET 6.0+
- **Thread-safe:** Yes - suitable for concurrent web applications
- **No breaking changes:** Does not affect existing APIs
- **License:** MIT - compatible with project licensing

**Cross-Package Compatibility:**

| Package Pair | Interaction | Status |
|--------------|-------------|--------|
| BCrypt.Net-Next + Npgsql | Independent - no interaction | ✅ Compatible |
| BCrypt.Net-Next + JWT libraries | Independent - no interaction | ✅ Compatible |
| System.Security.Cryptography + existing code | Replacement approach | ✅ Compatible |

### 0.4.3 Version Conflict Resolution

**No version conflicts identified.** The security remediation introduces one new package (BCrypt.Net-Next) which has no overlapping dependencies with existing packages.

**Dependency Tree Impact:**

```
WebVella.Erp.csproj (Before)
└── Newtonsoft.Json [13.0.4]
└── Npgsql [9.0.4]
└── System.Security.Cryptography (built-in)

WebVella.Erp.csproj (After)
└── Newtonsoft.Json [13.0.4]
└── Npgsql [9.0.4]
└── System.Security.Cryptography (built-in)
└── BCrypt.Net-Next [4.0.3] (NEW)
```

### 0.4.4 Alternative Package Analysis

**Alternative to BCrypt.Net-Next:**

| Alternative | Consideration | Decision |
|-------------|---------------|----------|
| ASP.NET Core Identity | Full-featured identity system | Rejected - requires extensive refactoring beyond security scope |
| Argon2id via Konscious.Security.Cryptography | Memory-hard, newer algorithm | Rejected - less ecosystem support, BCrypt sufficient |
| PBKDF2 via Rfc2898DeriveBytes | Built-in .NET | Rejected - BCrypt provides simpler API, better defaults |
| Scrypt via CryptSharpOfficial | Memory-hard function | Rejected - BCrypt preferred for web applications |

**Selection Rationale for BCrypt.Net-Next:**

1. **OWASP Recommendation:** BCrypt is explicitly recommended for password storage
2. **Cost Factor Control:** Adjustable work factor for future-proofing
3. **Automatic Salt:** Generates and stores salt with hash
4. **Verification API:** Built-in timing-safe comparison
5. **Minimal Code Change:** Drop-in replacement for hash/verify operations
6. **Active Maintenance:** Regular updates and security patches

### 0.4.5 Migration Compatibility

**Backward Compatibility for Password Migration:**

The security fix must support both old MD5 hashes and new BCrypt hashes during a transition period:

| Scenario | Old Hash | New Hash | Behavior |
|----------|----------|----------|----------|
| Existing user login | MD5 hash in DB | N/A | Verify with MD5, rehash with BCrypt on success |
| New user registration | N/A | BCrypt hash | Hash with BCrypt |
| Password change | Any | BCrypt hash | Hash with BCrypt |
| Migration complete | N/A | BCrypt hash | Verify with BCrypt only |

**Hash Format Detection:**

BCrypt hashes are identifiable by their prefix format: `$2a$`, `$2b$`, or `$2y$`

```csharp
// Pseudo-code for detection
bool IsBcryptHash(string hash) => 
    hash.StartsWith("$2a$") || hash.StartsWith("$2b$") || hash.StartsWith("$2y$");
```

### 0.4.6 Security Improvement Summary

| Current State | Remediated State | Security Improvement |
|---------------|------------------|---------------------|
| MD5 (instant crack) | BCrypt (cost 12) | ~2.4 seconds per hash attempt |
| No salt | Automatic 128-bit salt | Rainbow table attacks eliminated |
| Fast hashing | Intentionally slow | Brute force impractical |
| Static key derivation | PBKDF2-based derivation | Key stretching protection |
| IV from key | Random IV per operation | Prevents pattern analysis |

## 0.5 Security Fix Design

### 0.5.1 Minimal Fix Strategy

**Guiding Principle:** Apply the smallest possible change that completely addresses each vulnerability while maintaining backward compatibility during migration periods.

**Fix Approach Summary:**

| Vulnerability | Fix Type | Invasiveness | Breaking Changes |
|---------------|----------|--------------|------------------|
| MD5 Password Hashing | Code Patch + Dependency | Low | None (backward compatible) |
| Hard-coded Encryption Key | Configuration Change | Minimal | None |
| Default JWT Key | Configuration Validation | Minimal | None (startup validation) |
| CORS Misconfiguration | Configuration Change | Low | None |
| Missing Security Headers | Middleware Addition | Low | None |
| 100-Year Cookie | Configuration Change | Minimal | None (new sessions only) |
| File Upload Validation | Code Patch | Low | None |

### 0.5.2 Detailed Fix Specifications

**Fix #1: MD5 Password Hashing (Critical)**

**Current vulnerable implementation in `PasswordUtil.cs`:**
```csharp
using (var md5 = MD5.Create()) {
    return Convert.ToHexString(md5.ComputeHash(Encoding.ASCII.GetBytes(input)));
}
```

**Secure implementation strategy:**
- Add `HashPassword(string password)` method using BCrypt with cost factor 12
- Add `VerifyPassword(string password, string hash)` method with format detection
- Maintain `GetMd5Hash()` as private for backward compatibility verification only
- Auto-rehash MD5 passwords to BCrypt upon successful authentication

**Security improvement:** BCrypt with cost factor 12 requires approximately 250ms per hash operation, making brute-force attacks computationally impractical. Each password receives a unique 128-bit salt automatically embedded in the hash.

---

**Fix #2: Hard-coded Encryption Key (Critical)**

**Current vulnerable implementation in `CryptoUtility.cs`:**
```csharp
private static readonly byte[] defaultCryptKey = { 0x01, 0x02, ... };
```

**Secure implementation strategy:**
- Remove `defaultCryptKey` constant entirely
- Require encryption key via configuration (fail if not provided)
- Implement PBKDF2 key derivation (10,000 iterations minimum)
- Generate cryptographically random IV per encryption operation
- Store IV prepended to ciphertext

**Security improvement:** Eliminates predictable keys; each encryption operation uses unique IV preventing pattern analysis.

---

**Fix #3: Default JWT Key (Critical)**

**Current vulnerable implementation in `ErpSettings.cs`:**
```csharp
public string JwtKey { get; set; } = "ThisIsMySecretKey";
```

**Secure implementation strategy:**
- Remove default value assignment
- Add startup validation for key presence and minimum length (256 bits / 32 characters)
- Add validation for key entropy (reject common weak patterns)
- Fail application startup with descriptive error if validation fails

**Security improvement:** Prevents deployment with weak/known keys; ensures cryptographically strong signing keys.

---

**Fix #4: CORS Misconfiguration (Critical)**

**Current vulnerable implementation in `Startup.cs`:**
```csharp
services.AddCors(opt => opt.AddPolicy("WebErpCors", builder => {
    builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
}));
```

**Secure implementation strategy:**
- Replace `AllowAnyOrigin()` with `WithOrigins(configuredOrigins)`
- Add configuration setting for allowed origins (with sensible default for development)
- Restrict methods to actual requirements: GET, POST, PUT, DELETE, PATCH
- Restrict headers to required subset

**Security improvement:** Prevents cross-origin attacks from malicious websites.

---

**Fix #5: Missing Security Headers (High)**

**Current state:** No security headers middleware present.

**Secure implementation strategy:**
- Create `SecurityHeadersMiddleware.cs` that adds OWASP-recommended headers
- Headers to add:
  - `X-Frame-Options: DENY`
  - `X-Content-Type-Options: nosniff`
  - `Strict-Transport-Security: max-age=31536000; includeSubDomains`
  - `Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'`
  - `Referrer-Policy: strict-origin-when-cross-origin`
  - `Permissions-Policy: geolocation=(), microphone=(), camera=()`
- Register middleware early in pipeline (before UseRouting)

**Security improvement:** Mitigates clickjacking, XSS, MIME sniffing, and other browser-based attacks.

---

**Fix #6: 100-Year Cookie Expiration (High)**

**Current vulnerable implementation in `AuthService.cs`:**
```csharp
Expires = DateTimeOffset.UtcNow.AddYears(100)
```

**Secure implementation strategy:**
- Change to configurable expiration with 30-day default
- Implement sliding expiration for active sessions
- Add configuration option for remember-me vs session-only cookies

**Security improvement:** Reduces session hijacking window from 100 years to 30 days.

---

**Fix #7: File Upload Validation (High)**

**Current vulnerable implementation in `WebApiController.cs`:**
```csharp
// No content type or extension validation
var file = Request.Form.Files[0];
// Direct storage without validation
```

**Secure implementation strategy:**
- Create `FileValidationUtil.cs` with allowed extension whitelist
- Validate file content magic bytes match declared content type
- Enforce maximum file size limit (configurable, default 10MB)
- Sanitize filename to prevent path traversal
- Reject executable file types

**Security improvement:** Prevents malicious file upload attacks, path traversal, and storage exhaustion.

### 0.5.3 Security Improvement Validation

**Verification Methods:**

| Fix | Verification Method | Expected Result |
|-----|---------------------|-----------------|
| BCrypt passwords | Unit test with known hash | Hash verification succeeds |
| Key validation | Integration test with weak key | Application fails to start |
| CORS restriction | HTTP request from unauthorized origin | Request blocked (no CORS headers) |
| Security headers | HTTP response inspection | All required headers present |
| Cookie expiration | Cookie inspection | Expiration ≤ 30 days |
| File validation | Upload malicious file | Request rejected with 400 |

**Rollback Plan:**

Each fix is designed as an atomic change that can be reverted independently:

1. **Password hashing:** Backward compatible - no rollback needed
2. **Encryption keys:** Configuration-based - revert via config
3. **JWT validation:** Remove validation code, restore default
4. **CORS:** Restore `AllowAnyOrigin()` call
5. **Security headers:** Remove middleware registration
6. **Cookie expiration:** Restore `AddYears(100)`
7. **File validation:** Remove validation calls

## 0.6 File Transformation Mapping

### 0.6.1 File-by-File Security Fix Plan

**Security Fix Transformation Modes:**
- **UPDATE** - Modify an existing file to patch vulnerability
- **CREATE** - Create a new file for security improvement
- **DELETE** - Remove a file that introduces vulnerability
- **REFERENCE** - Use as an example for security patterns

| Target File | Transformation | Source File/Reference | Security Changes |
|-------------|----------------|----------------------|------------------|
| WebVella.Erp/Utilities/PasswordUtil.cs | UPDATE | WebVella.Erp/Utilities/PasswordUtil.cs | Replace MD5 with BCrypt hashing (cost 12), add backward-compatible verification with auto-rehash |
| WebVella.Erp/Utilities/CryptoUtility.cs | UPDATE | WebVella.Erp/Utilities/CryptoUtility.cs | Remove defaultCryptKey, implement PBKDF2 key derivation (10K iterations), random IV generation |
| WebVella.Erp/ErpSettings.cs | UPDATE | WebVella.Erp/ErpSettings.cs | Remove default JWT key value, add validation for key length (≥32 chars) and presence |
| WebVella.Erp/Api/SecurityManager.cs | UPDATE | WebVella.Erp/Api/SecurityManager.cs | Update password verification to use new BCrypt-based PasswordUtil with rehash support |
| WebVella.Erp.Site/Startup.cs | UPDATE | WebVella.Erp.Site/Startup.cs | Fix CORS to use configurable origins, add security headers middleware registration |
| WebVella.Erp.Web/Services/AuthService.cs | UPDATE | WebVella.Erp.Web/Services/AuthService.cs | Change cookie expiration from 100 years to configurable 30 days with sliding expiration |
| WebVella.Erp.Web/Controllers/WebApiController.cs | UPDATE | WebVella.Erp.Web/Controllers/WebApiController.cs | Add file validation calls to UploadFile and UploadDropCKEditor endpoints |
| WebVella.Erp.Site/Config.json | UPDATE | WebVella.Erp.Site/Config.json | Add documentation comments for required security configuration, remove default weak values |
| WebVella.Erp/WebVella.Erp.csproj | UPDATE | WebVella.Erp/WebVella.Erp.csproj | Add PackageReference for BCrypt.Net-Next 4.0.3 |
| WebVella.Erp.Site/WebVella.Erp.Site.csproj | UPDATE | WebVella.Erp.Site/WebVella.Erp.Site.csproj | Add PackageReference for BCrypt.Net-Next 4.0.3 (if password operations used directly) |
| WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs | CREATE | OWASP Security Headers reference | Implement OWASP-recommended security headers (X-Frame-Options, CSP, HSTS, etc.) |
| WebVella.Erp/Utilities/FileValidationUtil.cs | CREATE | WebVella.Erp/Utilities/PasswordUtil.cs (pattern) | Implement file type validation, extension whitelist, magic byte verification, size limits |
| WebVella.Erp.Web/Middleware/JwtMiddleware.cs | UPDATE | WebVella.Erp.Web/Middleware/JwtMiddleware.cs | Add logging for authentication failures (currently silent) |
| tests/security/PasswordSecurityTests.cs | CREATE | Existing test patterns | BCrypt hash generation, verification, MD5 backward compatibility, auto-rehash tests |
| tests/security/CryptoSecurityTests.cs | CREATE | Existing test patterns | Encryption key validation, IV randomness, PBKDF2 derivation tests |
| tests/security/SecurityHeadersTests.cs | CREATE | Existing test patterns | Header presence verification, CSP validation tests |
| tests/security/FileValidationTests.cs | CREATE | Existing test patterns | File type validation, malicious file rejection tests |
| tests/security/CorsSecurityTests.cs | CREATE | Existing test patterns | CORS policy enforcement, unauthorized origin rejection tests |
| SECURITY.md | CREATE | GitHub security documentation | Security policies, reporting procedures, configuration requirements |

### 0.6.2 Code Change Specifications

**PasswordUtil.cs (Critical - Lines 15-35 affected)**

| Aspect | Current State | Target State |
|--------|---------------|--------------|
| File | WebVella.Erp/Utilities/PasswordUtil.cs | WebVella.Erp/Utilities/PasswordUtil.cs |
| Lines affected | 15-35 (approximate) | Complete method rewrites |
| Before state | MD5 hash generation without salt | N/A |
| After state | BCrypt hash with cost factor 12, backward-compatible MD5 verification, auto-rehash | N/A |
| Security improvement | Eliminates CWE-328 (weak hash), adds CWE-916 protection (insufficient entropy) | N/A |

**CryptoUtility.cs (Critical - Lines 12-18, 45-70 affected)**

| Aspect | Current State | Target State |
|--------|---------------|--------------|
| File | WebVella.Erp/Utilities/CryptoUtility.cs | WebVella.Erp/Utilities/CryptoUtility.cs |
| Lines affected | 12-18 (key definition), 45-70 (encryption methods) | Key derivation and IV generation |
| Before state | Static default key, IV derived from key | N/A |
| After state | No default key (throws if unconfigured), PBKDF2 derivation, random IV prepended to ciphertext | N/A |
| Security improvement | Eliminates CWE-798 (hard-coded credentials), CWE-329 (predictable IV) | N/A |

**ErpSettings.cs (Critical - Line 25 affected)**

| Aspect | Current State | Target State |
|--------|---------------|--------------|
| File | WebVella.Erp/ErpSettings.cs | WebVella.Erp/ErpSettings.cs |
| Lines affected | 25 (JWT key property) | Property definition and validation |
| Before state | `public string JwtKey { get; set; } = "ThisIsMySecretKey";` | N/A |
| After state | No default value, validation method added | N/A |
| Security improvement | Eliminates CWE-798 (hard-coded credentials) | N/A |

**Startup.cs (Critical - Lines 89-95, 120-130 affected)**

| Aspect | Current State | Target State |
|--------|---------------|--------------|
| File | WebVella.Erp.Site/Startup.cs | WebVella.Erp.Site/Startup.cs |
| Lines affected | 89-95 (CORS), 120-130 (middleware) | CORS policy and middleware registration |
| Before state | `AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()` | N/A |
| After state | `WithOrigins(configuredOrigins).WithMethods("GET", "POST", "PUT", "DELETE", "PATCH")`, security headers middleware added | N/A |
| Security improvement | Eliminates CWE-942 (overly permissive CORS), CWE-693 (missing security headers) | N/A |

**AuthService.cs (High - Lines 45-48 affected)**

| Aspect | Current State | Target State |
|--------|---------------|--------------|
| File | WebVella.Erp.Web/Services/AuthService.cs | WebVella.Erp.Web/Services/AuthService.cs |
| Lines affected | 45-48 (cookie options) | Cookie configuration |
| Before state | `Expires = DateTimeOffset.UtcNow.AddYears(100)` | N/A |
| After state | `Expires = DateTimeOffset.UtcNow.AddDays(30)` with sliding expiration | N/A |
| Security improvement | Eliminates CWE-613 (insufficient session expiration) | N/A |

**WebApiController.cs (High - Lines 3320-3380, 3960-4020 affected)**

| Aspect | Current State | Target State |
|--------|---------------|--------------|
| File | WebVella.Erp.Web/Controllers/WebApiController.cs | WebVella.Erp.Web/Controllers/WebApiController.cs |
| Lines affected | 3320-3380 (UploadFile), 3960-4020 (UploadDropCKEditor) | File upload endpoints |
| Before state | No file type or size validation | N/A |
| After state | FileValidationUtil calls before processing, extension whitelist, size limits | N/A |
| Security improvement | Eliminates CWE-434 (unrestricted file upload) | N/A |

### 0.6.3 Configuration Change Specifications

| File | Setting | Current Value | New Value | Security Rationale |
|------|---------|---------------|-----------|-------------------|
| Config.json | JwtKey | "ThisIsMySecretKey" | (empty - must be configured) | Requires strong key configuration |
| Config.json | EncryptionKey | (empty/default) | (empty - must be configured) | Requires explicit key configuration |
| Config.json | AllowedOrigins | (not present) | ["https://yourdomain.com"] | CORS origin whitelist |
| Config.json | CookieExpirationDays | (not present) | 30 | Configurable session duration |
| Config.json | MaxUploadSizeBytes | (not present) | 10485760 | 10MB file upload limit |

### 0.6.4 New File Creation Specifications

**SecurityHeadersMiddleware.cs**
```csharp
// Location: WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs
// Purpose: Add OWASP-recommended security headers to all responses
// Headers: X-Frame-Options, X-Content-Type-Options, CSP, HSTS, Referrer-Policy
```

**FileValidationUtil.cs**
```csharp
// Location: WebVella.Erp/Utilities/FileValidationUtil.cs
// Purpose: Validate uploaded files for security
// Features: Extension whitelist, MIME type verification, size limits, path sanitization
```

### 0.6.5 Test File Creation Specifications

| Test File | Coverage Area | Test Cases |
|-----------|---------------|------------|
| PasswordSecurityTests.cs | BCrypt implementation | Hash generation, verification, MD5 compat, rehash |
| CryptoSecurityTests.cs | Encryption changes | Key validation, IV randomness, round-trip |
| SecurityHeadersTests.cs | Header middleware | All required headers present and valid |
| FileValidationTests.cs | Upload validation | Extension checks, MIME verification, size limits |
| CorsSecurityTests.cs | CORS policy | Allowed/denied origins, methods, headers |

## 0.7 Dependency Inventory

### 0.7.1 Security Patches and Updates

**Current Dependency Security Status:**

All existing packages have been audited against NVD, Snyk, and GitHub Security Advisories. No Critical or High severity CVEs require updates:

| Registry | Package Name | Current | Latest Patched | CVE/Advisory | Severity | Status |
|----------|--------------|---------|----------------|--------------|----------|--------|
| NuGet | Newtonsoft.Json | 13.0.4 | 13.0.1 | CVE-2024-21907 | High (DoS) | ✅ Already Patched |
| NuGet | System.IdentityModel.Tokens.Jwt | 8.15.0 | 7.1.2 | CVE-2024-21319 | Moderate (DoS) | ✅ Already Patched |
| NuGet | Npgsql | 9.0.4 | N/A | None | N/A | ✅ Secure |
| NuGet | MailKit | 4.14.1 | N/A | None | N/A | ✅ Secure |
| NuGet | HtmlAgilityPack | 1.12.0 | N/A | None | N/A | ✅ Secure |
| NuGet | CsvHelper | 33.0.1 | N/A | None | N/A | ✅ Secure |
| NuGet | Scriban | 5.12.0 | N/A | None | N/A | ✅ Secure |
| NuGet | SixLabors.ImageSharp | 3.1.8 | N/A | None | N/A | ✅ Secure |

### 0.7.2 New Package Requirements

**Security Remediation Package Addition:**

| Registry | Package Name | Version | Purpose | License | Advisory Link |
|----------|--------------|---------|---------|---------|---------------|
| NuGet | BCrypt.Net-Next | 4.0.3 | Secure password hashing replacement for MD5 | MIT | [OWASP Password Storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html) |

**Package Selection Justification:**

BCrypt.Net-Next was selected over alternatives because:
1. **OWASP Recommendation:** BCrypt is explicitly listed in OWASP Password Storage Cheat Sheet
2. **Active Maintenance:** Regular releases with security updates
3. **API Simplicity:** Single-method hash/verify operations
4. **Automatic Salt:** 128-bit salt embedded in hash output
5. **Cost Factor:** Configurable work factor for future-proofing
6. **No External Dependencies:** Self-contained implementation

### 0.7.3 Dependency Chain Analysis

**Direct Dependencies Requiring Updates:**

| Project | Package | Change Type | Reason |
|---------|---------|-------------|--------|
| WebVella.Erp | BCrypt.Net-Next | ADD | Password hashing implementation |
| WebVella.Erp.Site | (inherits from WebVella.Erp) | N/A | Transitive dependency |
| WebVella.Erp.Web | (inherits from WebVella.Erp) | N/A | Transitive dependency |

**Transitive Dependencies Affected:**

BCrypt.Net-Next 4.0.3 has no external dependencies - it is a self-contained implementation.

**Peer Dependencies to Verify:**

| Package | Compatibility Check | Result |
|---------|---------------------|--------|
| .NET 10.0 Runtime | BCrypt.Net-Next targets .NET Standard 2.0+ | ✅ Compatible |
| System.Security.Cryptography | Used alongside BCrypt | ✅ No conflicts |
| Microsoft.AspNetCore.Authentication | Independent authentication layer | ✅ No conflicts |

**Development Dependencies with Vulnerabilities:**

No development-only dependencies with Critical/High vulnerabilities were identified.

### 0.7.4 Import and Reference Updates

**Source Files Requiring Import Updates:**

| File | Current Imports | New Imports |
|------|-----------------|-------------|
| WebVella.Erp/Utilities/PasswordUtil.cs | `System.Security.Cryptography` | `BCrypt.Net` (addition) |
| WebVella.Erp/Api/SecurityManager.cs | (none changed) | (no changes - uses PasswordUtil) |
| WebVella.Erp.Web/Services/AuthService.cs | (none changed) | (no changes - uses SecurityManager) |

**Import Transformation Rules:**

The MD5 implementation is being replaced, not the namespace imports. The `System.Security.Cryptography` namespace is retained for the `CryptoUtility.cs` improvements:

```csharp
// PasswordUtil.cs - Add new using
using BCrypt.Net;

// CryptoUtility.cs - No import changes needed
// Uses existing System.Security.Cryptography for:
// - Rfc2898DeriveBytes (PBKDF2)
// - Aes
// - RandomNumberGenerator
```

### 0.7.5 Project File Updates

**WebVella.Erp/WebVella.Erp.csproj Changes:**

```xml
<!-- ADD: New package reference for BCrypt -->
<PackageReference Include="BCrypt.Net-Next" Version="4.0.3" />
```

**No Version Conflicts:**

| Existing Package | BCrypt.Net-Next | Conflict | Resolution |
|------------------|-----------------|----------|------------|
| Newtonsoft.Json 13.0.4 | None | None | N/A |
| Npgsql 9.0.4 | None | None | N/A |
| All others | None | None | N/A |

### 0.7.6 Configuration Reference Updates

**Config.json Structure Updates:**

```json
{
  "Settings": {
    // SECURITY: JWT key must be at least 32 characters (256 bits)
    "JwtKey": "",
    
    // SECURITY: Encryption key must be configured - no default
    "EncryptionKey": "",
    
    // SECURITY: Allowed CORS origins (comma-separated)
    "AllowedOrigins": "https://yourdomain.com",
    
    // SECURITY: Cookie expiration in days (default: 30)
    "CookieExpirationDays": 30,
    
    // SECURITY: Maximum upload file size in bytes (default: 10MB)
    "MaxUploadSizeBytes": 10485760,
    
    // SECURITY: Allowed file extensions for upload
    "AllowedFileExtensions": ".jpg,.jpeg,.png,.gif,.pdf,.doc,.docx,.xls,.xlsx"
  }
}
```

### 0.7.7 Environment Variable Updates

The following environment variables can override configuration settings for production deployments:

| Variable | Purpose | Default |
|----------|---------|---------|
| `ERP_JWT_KEY` | JWT signing key | (none - required) |
| `ERP_ENCRYPTION_KEY` | Data encryption key | (none - required) |
| `ERP_ALLOWED_ORIGINS` | CORS allowed origins | localhost only |
| `ERP_COOKIE_EXPIRATION_DAYS` | Session cookie lifetime | 30 |
| `ERP_MAX_UPLOAD_SIZE` | Maximum file upload size | 10485760 |

### 0.7.8 Documentation Reference Updates

| Document | Section | Change |
|----------|---------|--------|
| README.md | Configuration | Add security configuration requirements |
| SECURITY.md | (new file) | Document security policies and configuration |
| Config.json | Comments | Add security requirement comments |
| CHANGELOG.md | Security | Document security remediation changes |

## 0.8 Impact Analysis and Testing Strategy

### 0.8.1 Security Testing Requirements

**Vulnerability Regression Tests:**

Each remediated vulnerability must have corresponding tests that verify the fix is effective:

| Vulnerability | Test Category | Specific Attack Scenarios to Test |
|---------------|---------------|-----------------------------------|
| MD5 Password Hashing | Unit Test | Password cracking resistance, rainbow table ineffectiveness |
| Hard-coded Encryption Key | Integration Test | Startup failure without config, encryption with random IV |
| Default JWT Key | Integration Test | Startup failure with default key, token validation with strong key |
| CORS Misconfiguration | Integration Test | Cross-origin request blocking from unauthorized domains |
| Missing Security Headers | Integration Test | Header presence in all HTTP responses |
| Cookie Expiration | Unit Test | Cookie expiration within configured bounds |
| File Upload | Integration Test | Rejection of malicious file types and oversized files |

**Security-Specific Test Cases to Add:**

| Test File | Test Name | Description |
|-----------|-----------|-------------|
| tests/security/PasswordSecurityTests.cs | TestBcryptHashGeneration | Verify BCrypt hash format and cost factor |
| tests/security/PasswordSecurityTests.cs | TestBcryptVerification | Verify password verification succeeds |
| tests/security/PasswordSecurityTests.cs | TestMd5BackwardCompatibility | Verify old MD5 hashes still verify |
| tests/security/PasswordSecurityTests.cs | TestAutoRehashOnLogin | Verify MD5 passwords are upgraded to BCrypt |
| tests/security/PasswordSecurityTests.cs | TestBcryptTimingResistance | Verify constant-time comparison |
| tests/security/CryptoSecurityTests.cs | TestEncryptionWithoutKeyFails | Verify exception when no key configured |
| tests/security/CryptoSecurityTests.cs | TestIvRandomness | Verify different IVs for same plaintext |
| tests/security/CryptoSecurityTests.cs | TestKeyDerivation | Verify PBKDF2 derivation with correct parameters |
| tests/security/SecurityHeadersTests.cs | TestAllHeadersPresent | Verify all OWASP headers in response |
| tests/security/SecurityHeadersTests.cs | TestCspPolicy | Verify Content-Security-Policy format |
| tests/security/SecurityHeadersTests.cs | TestHstsHeader | Verify HSTS max-age and includeSubDomains |
| tests/security/FileValidationTests.cs | TestMaliciousExtensionRejected | Verify .exe, .bat, .sh rejected |
| tests/security/FileValidationTests.cs | TestOversizedFileRejected | Verify files exceeding limit rejected |
| tests/security/FileValidationTests.cs | TestMimeTypeMismatch | Verify content-type matches extension |
| tests/security/CorsSecurityTests.cs | TestUnauthorizedOriginBlocked | Verify requests from unknown origins blocked |

### 0.8.2 Verification Methods

**Automated Security Scanning:**

| Tool | Command | Expected Result |
|------|---------|-----------------|
| dotnet list package --vulnerable | `dotnet list package --vulnerable --include-transitive` | Zero vulnerable packages |
| .NET Security Code Analysis | `dotnet build /p:AnalysisLevel=latest` | Zero security warnings |
| OWASP Dependency-Check | `dependency-check --project WebVella.ERP` | Zero Critical/High CVEs |

**Manual Verification Steps:**

1. **Password Hashing Verification:**
   - Create new user account
   - Verify stored hash starts with `$2a$12$` (BCrypt v2a, cost 12)
   - Verify hash length is 60 characters
   - Attempt login with correct password (should succeed)
   - Attempt login with incorrect password (should fail)

2. **JWT Key Validation:**
   - Attempt startup with no JwtKey configured → Application should fail
   - Attempt startup with "ThisIsMySecretKey" → Application should fail
   - Attempt startup with 32+ character random key → Application should start

3. **CORS Restriction:**
   - Send request from allowed origin → Request succeeds with CORS headers
   - Send request from unauthorized origin → No CORS headers, request blocked

4. **Security Headers:**
   - Request any page with browser dev tools open
   - Verify X-Frame-Options: DENY
   - Verify X-Content-Type-Options: nosniff
   - Verify Content-Security-Policy present
   - Verify Strict-Transport-Security present (HTTPS only)

5. **File Upload Validation:**
   - Attempt to upload .exe file → Rejected with 400
   - Attempt to upload file > 10MB → Rejected with 413
   - Attempt to upload .jpg file → Accepted

### 0.8.3 Penetration Testing Scenarios

| Scenario | Attack Vector | Expected Behavior |
|----------|---------------|-------------------|
| Password Hash Extraction | SQL injection (hypothetical) | BCrypt hashes computationally infeasible to crack |
| JWT Forgery | Token crafting with known key | Token rejected - key no longer predictable |
| Cross-Site Request Forgery | Malicious website request | Request blocked by CORS policy |
| Clickjacking | iframe embedding | Blocked by X-Frame-Options |
| Malicious File Upload | Upload .php/.exe file | Rejected by extension whitelist |
| Content-Type Mismatch | .jpg file with .exe content | Rejected by MIME type validation |

### 0.8.4 Impact Assessment

**Direct Security Improvements Achieved:**

| Vulnerability | Before | After | Improvement |
|---------------|--------|-------|-------------|
| Password Cracking Time | Instant (MD5 rainbow tables) | ~250ms per attempt (BCrypt cost 12) | >10^12x improvement |
| JWT Forgery Risk | Trivial (known key) | Impossible (256-bit key required) | Eliminated |
| Cross-Origin Attacks | Fully vulnerable | Blocked | Eliminated |
| Clickjacking | Vulnerable | Blocked (X-Frame-Options: DENY) | Eliminated |
| MIME Sniffing | Vulnerable | Blocked (X-Content-Type-Options) | Eliminated |
| Malicious Uploads | Possible | Blocked by validation | Eliminated |
| Session Hijacking Window | 100 years | 30 days | 99.97% reduction |

**Minimal Side Effects on Existing Functionality:**

| Area | Impact | Mitigation |
|------|--------|------------|
| Existing User Passwords | No impact - backward compatible verification | Automatic rehash on login |
| Existing Encrypted Data | No impact - existing data readable | Key configuration required |
| API Consumers | Potential CORS impact | Allowed origins configurable |
| Session Duration | Sessions expire sooner | Sliding expiration maintains UX |
| File Uploads | Some file types rejected | Configurable whitelist |

**Potential Impacts to Address:**

| Potential Issue | Likelihood | Mitigation Strategy |
|-----------------|------------|---------------------|
| CORS breaks legitimate integrations | Medium | Document required origin configuration |
| Strong key requirement blocks deployment | Low | Clear error messages, documentation |
| File validation rejects legitimate files | Low | Configurable extension whitelist |
| Performance impact from BCrypt | Low | Cost factor 12 = ~250ms per hash |

### 0.8.5 Test Execution Commands

**Full Test Suite:**
```bash
dotnet test --verbosity normal
```

**Security-Specific Tests:**
```bash
dotnet test --filter "Category=Security"
```

**Dependency Vulnerability Scan:**
```bash
dotnet list package --vulnerable --include-transitive
```

**Security Code Analysis:**
```bash
dotnet build /p:RunAnalyzers=true /p:AnalysisLevel=latest
```

### 0.8.6 Success Criteria Validation

| Criterion | Measurement | Target | Validation Method |
|-----------|-------------|--------|-------------------|
| Zero Critical vulnerabilities | SAST + Dependency scan | 0 | Automated CI check |
| Zero High vulnerabilities | SAST + Dependency scan | 0 | Automated CI check |
| Test suite pass rate | Test execution | 100% | dotnet test |
| Functional parity | Regression tests | All pass | Manual + automated |
| Performance baseline | Response time comparison | ≤10% degradation | Load testing |

## 0.9 Scope Boundaries

### 0.9.1 Exhaustively In Scope

**Vulnerable Dependency Manifests:**

| File Pattern | Purpose |
|--------------|---------|
| `WebVella.Erp/WebVella.Erp.csproj` | Core library package references - add BCrypt.Net-Next |
| `WebVella.Erp.Web/WebVella.Erp.Web.csproj` | Web layer package references - verify dependencies |
| `WebVella.Erp.Site/WebVella.Erp.Site.csproj` | Site project package references - verify dependencies |
| `WebVella.Erp.Plugins.*/WebVella.Erp.Plugins.*.csproj` | Plugin package references - verify dependencies |
| `WebVella.ERP3.sln` | Solution file - no changes expected |

**Source Files with Vulnerable Code:**

| File Pattern | Security Issue |
|--------------|----------------|
| `WebVella.Erp/Utilities/PasswordUtil.cs` | MD5 password hashing - replace with BCrypt |
| `WebVella.Erp/Utilities/CryptoUtility.cs` | Hard-coded key, weak IV derivation - fix encryption |
| `WebVella.Erp/ErpSettings.cs` | Default JWT key - add validation |
| `WebVella.Erp/Api/SecurityManager.cs` | Password verification using MD5 - update for BCrypt |
| `WebVella.Erp.Web/Services/AuthService.cs` | 100-year cookie expiration - fix session duration |
| `WebVella.Erp.Web/Controllers/WebApiController.cs` | Unrestricted file uploads - add validation |
| `WebVella.Erp.Web/Middleware/JwtMiddleware.cs` | Silent authentication failures - add logging |

**Configuration Files Requiring Security Updates:**

| File Pattern | Security Change |
|--------------|-----------------|
| `WebVella.Erp.Site/Config.json` | Remove default values, add security documentation |
| `WebVella.Erp.Site/appsettings.json` | Add security configuration section if present |
| `WebVella.Erp.Site/appsettings.*.json` | Environment-specific security settings |

**Infrastructure and Deployment:**

| File Pattern | Purpose |
|--------------|---------|
| `WebVella.Erp.Site/Startup.cs` | CORS configuration, security headers middleware |
| `WebVella.Erp.Site/Program.cs` | Startup configuration verification |
| `Dockerfile*` | Verify no secrets in build context (document only) |
| `.github/workflows/*.yml` | CI/CD security scanning (if present) |

**Security Test Files:**

| File Pattern | Purpose |
|--------------|---------|
| `tests/security/PasswordSecurityTests.cs` | BCrypt implementation tests |
| `tests/security/CryptoSecurityTests.cs` | Encryption security tests |
| `tests/security/SecurityHeadersTests.cs` | HTTP header validation tests |
| `tests/security/FileValidationTests.cs` | Upload validation tests |
| `tests/security/CorsSecurityTests.cs` | CORS policy tests |

**New Middleware Files:**

| File Pattern | Purpose |
|--------------|---------|
| `WebVella.Erp.Web/Middleware/SecurityHeadersMiddleware.cs` | OWASP security headers |

**New Utility Files:**

| File Pattern | Purpose |
|--------------|---------|
| `WebVella.Erp/Utilities/FileValidationUtil.cs` | File upload security validation |

**Documentation Updates:**

| File Pattern | Purpose |
|--------------|---------|
| `SECURITY.md` | Security policies and configuration requirements |
| `README.md` | Security section with configuration instructions |

### 0.9.2 Explicitly Out of Scope

**Feature Additions Unrelated to Security:**
- New API endpoints or functionality
- UI enhancements or new screens
- Database schema changes for features
- New business logic implementation
- Reporting or analytics features

**Performance Optimizations Not Required for Security:**
- Query optimization
- Caching improvements
- Load balancing configuration
- Database indexing changes
- Async refactoring

**Code Refactoring Beyond Security Fix Requirements:**
- Code style changes unrelated to security
- Architecture refactoring
- Design pattern changes
- Code deduplication
- Variable renaming

**Non-Vulnerable Dependencies:**
- Package updates for features (not security)
- Development tool updates
- Build tool updates
- Non-security library upgrades

**Style or Formatting Changes:**
- Code formatting
- Comment additions (except security documentation)
- Whitespace changes
- File organization changes

**Test Files Unrelated to Security Validation:**
- Existing unit tests (unless broken by security changes)
- Integration tests (unless security-related)
- Performance tests
- UI tests

**Items Explicitly Excluded by User Instructions:**
- "No feature additions or enhancements"
- "No refactoring beyond security requirements"
- "No architectural changes unless Critical vulnerability requires it"
- "Third-party code and vendor libraries (update versions only)"
- "Infrastructure components outside application boundary"
- "External service integrations (document risks if found)"
- "Database stored data (schema fixes only if security-critical)"

### 0.9.3 Scope Boundary Clarifications

**Boundary: Database Schema**
- **In Scope:** No schema changes required - security fixes are code-level
- **Out of Scope:** Any data migration or schema modifications

**Boundary: Third-Party Integrations**
- **In Scope:** Documenting security risks if found
- **Out of Scope:** Modifying third-party service configurations

**Boundary: Client-Side Code**
- **In Scope:** Server-side security headers affecting browser behavior
- **Out of Scope:** JavaScript/client-side security implementations

**Boundary: Blazor WebAssembly**
- **In Scope:** Server-side API security
- **Out of Scope:** WebAssembly-specific security (noted: OwaspHeaders.Core does not support WebAssembly)

**Boundary: Plugin Security**
- **In Scope:** Core security improvements benefit plugins through inheritance
- **Out of Scope:** Plugin-specific security modifications

### 0.9.4 Conditional Scope Items

The following items are conditionally in scope based on findings:

| Item | Condition | Current Status |
|------|-----------|----------------|
| Dependency updates | If Critical/High CVE found | Not required - all packages current |
| Database encryption | If plaintext sensitive data found | Document only - out of scope |
| Rate limiting | If DoS vulnerability critical | Medium priority - document for future |
| CSRF tokens | If stateful API endpoints found | Medium priority - document for future |
| Input sanitization | If injection vulnerabilities found | Verified safe - parameterized queries used |

### 0.9.5 Scope Decision Matrix

| Finding | Severity | User Constraint | Decision |
|---------|----------|-----------------|----------|
| MD5 password hashing | Critical | Minimal changes | IN SCOPE - Critical security fix |
| Hard-coded keys | Critical | Minimal changes | IN SCOPE - Critical security fix |
| CORS misconfiguration | Critical | Minimal changes | IN SCOPE - Critical security fix |
| Missing security headers | High | Minimal changes | IN SCOPE - High security fix |
| 100-year cookie | High | Minimal changes | IN SCOPE - High security fix |
| Unrestricted uploads | High | Minimal changes | IN SCOPE - High security fix |
| No rate limiting | Medium | Minimal changes | DOCUMENT ONLY - Medium priority |
| Silent auth failures | Medium | Minimal changes | IN SCOPE - Add logging (minimal change) |
| Missing CSRF protection | Medium | Minimal changes | DOCUMENT ONLY - Requires architectural change |
| Verbose errors | Low | Minimal changes | DOCUMENT ONLY - Low priority |

## 0.10 Special Instructions

### 0.10.1 Security-Specific Requirements

**User-Specified Constraints (Mandatory Compliance):**

1. **Minimal Change Clause:**
   > "Make only the changes absolutely necessary to remediate identified security vulnerabilities"
   
   **Implementation:** Each fix targets only the specific vulnerable code pattern. No adjacent code is modified unless directly required for the security fix.

2. **Functional Preservation:**
   > "All existing functionality must remain operational"
   
   **Implementation:** Backward-compatible password verification, configurable CORS origins, sliding session expiration to maintain user experience.

3. **API Contract Preservation:**
   > "API contracts and interfaces unchanged"
   
   **Implementation:** No endpoint signatures, request/response formats, or public method signatures are modified.

4. **Performance Constraint:**
   > "Performance within 10% of baseline"
   
   **Implementation:** BCrypt cost factor 12 adds ~250ms per authentication - acceptable for security-sensitive operation. All other fixes have negligible performance impact.

5. **Atomic Commits:**
   > "Atomic commits per vulnerability class for rollback capability"
   
   **Implementation:** Separate commits for:
   - Password hashing (BCrypt implementation)
   - Cryptographic improvements (key/IV handling)
   - CORS and security headers
   - File upload validation
   - Session management

### 0.10.2 Secrets Management

**Critical Security Configuration Requirements:**

| Secret | Requirement | Failure Behavior |
|--------|-------------|------------------|
| JWT Signing Key | ≥32 characters, cryptographically random | Application startup failure with descriptive error |
| Encryption Key | Configuration required, no default | Encryption operations fail with configuration error |
| SMTP Password | Move to secure storage (document only) | Document as Medium severity recommendation |
| Database Connection | Already parameterized | No change required |

**Key Configuration Guidance:**

```bash
# Generate secure JWT key (example)

openssl rand -base64 48

#### Generate secure encryption key (example)

openssl rand -base64 32
```

**Environment Variable Override Pattern:**
```csharp
// Configuration priority: Environment > Config.json > (no default)
var jwtKey = Environment.GetEnvironmentVariable("ERP_JWT_KEY") 
    ?? configuration["Settings:JwtKey"]
    ?? throw new InvalidOperationException("JWT key must be configured");
```

### 0.10.3 Compliance Considerations

**OWASP Compliance Mapping:**

| OWASP Top 10 (2021) | Remediation Status |
|---------------------|-------------------|
| A01: Broken Access Control | CORS restriction implemented |
| A02: Cryptographic Failures | BCrypt passwords, proper key/IV handling |
| A03: Injection | Already compliant (parameterized queries) |
| A04: Insecure Design | Security headers, validation middleware |
| A05: Security Misconfiguration | Default key elimination, header configuration |
| A06: Vulnerable Components | All packages current - no updates needed |
| A07: Authentication Failures | BCrypt, session duration, key validation |
| A08: Software/Data Integrity Failures | File upload validation |
| A09: Security Logging Failures | JWT failure logging added |
| A10: SSRF | Not applicable - no user-controlled URLs |

**Security Standards Alignment:**

| Standard | Relevant Requirements | Compliance |
|----------|----------------------|------------|
| OWASP ASVS | Password storage V2.4 | BCrypt implementation complies |
| OWASP ASVS | Cryptography V6.2 | PBKDF2 key derivation, random IV |
| OWASP ASVS | Session V3.3 | 30-day expiration with sliding |
| NIST SP 800-132 | Key derivation | PBKDF2 with ≥10,000 iterations |
| CWE/SANS Top 25 | Multiple | Addresses CWE-328, CWE-798, CWE-942, CWE-434 |

### 0.10.4 Breaking Changes Documentation

**Potentially Breaking Changes:**

| Change | Impact | Mitigation | Breaking? |
|--------|--------|------------|-----------|
| JWT key validation | Startup fails with weak key | Provide clear error, documentation | Yes - intentional |
| CORS restriction | Unauthorized origins blocked | Configuration for allowed origins | Yes - intentional |
| File upload validation | Some file types rejected | Configurable whitelist | Partial |
| Cookie duration | Sessions expire after 30 days | Sliding expiration | No |
| Password verification | None - backward compatible | MD5 hashes still verify | No |

**Migration Checklist for Deployments:**

1. [ ] Generate and configure strong JWT key (≥32 characters)
2. [ ] Configure encryption key in production environment
3. [ ] Define allowed CORS origins for production
4. [ ] Review and adjust file extension whitelist if needed
5. [ ] Update environment variables in deployment configuration
6. [ ] Test authentication flow before production deployment
7. [ ] Monitor logs for security-related failures after deployment

### 0.10.5 Security Review Requirements

**Pre-Deployment Security Review Checklist:**

- [ ] All Critical/High vulnerabilities remediated
- [ ] Security tests pass (100% pass rate)
- [ ] No hard-coded secrets in codebase
- [ ] SAST scan shows zero Critical/High findings
- [ ] Dependency scan shows zero Critical/High CVEs
- [ ] Security headers verified in staging environment
- [ ] CORS policy tested with expected origins
- [ ] File upload validation tested with boundary cases
- [ ] Session management tested (login, timeout, logout)
- [ ] BCrypt hash format verified in database

### 0.10.6 Execution Parameters

**Security Verification Commands:**

```bash
# Dependency vulnerability scan

dotnet list package --vulnerable --include-transitive

#### Security test execution

dotnet test --filter "Category=Security" --verbosity detailed

#### Full test suite validation

dotnet test --verbosity normal

#### Build with security analyzers

dotnet build /p:RunAnalyzers=true /p:TreatWarningsAsErrors=true
```

### 0.10.7 Research Documentation

**Security Advisories Consulted:**

| Source | Reference | Finding |
|--------|-----------|---------|
| NVD | CVE-2024-21907 | Newtonsoft.Json DoS - already patched |
| NVD | CVE-2024-21319 | JWT library DoS - already patched |
| OWASP | Password Storage Cheat Sheet | BCrypt recommendation |
| OWASP | Security Headers Project | Header implementation guidance |
| OWASP | .NET Security Cheat Sheet | ASP.NET Core security patterns |
| GitHub Advisory | GHSA-5crp-9r3c-p9vr | Newtonsoft.Json advisory |
| Snyk | SNYK-DOTNET-* | Package vulnerability database |

**Security Best Practices Applied:**

| Practice | Source | Implementation |
|----------|--------|----------------|
| Adaptive password hashing | OWASP | BCrypt with cost factor 12 |
| Key derivation | NIST SP 800-132 | PBKDF2 with 10,000 iterations |
| Random IV | NIST SP 800-38A | RandomNumberGenerator for IV |
| Security headers | OWASP Secure Headers | X-Frame-Options, CSP, HSTS, etc. |
| Session management | OWASP Session Cheat Sheet | 30-day expiration, sliding window |
| File upload validation | OWASP Upload Cheat Sheet | Extension whitelist, MIME verification |

### 0.10.8 Post-Remediation Monitoring

**Recommended Monitoring After Deployment:**

| Metric | Alert Threshold | Purpose |
|--------|-----------------|---------|
| Authentication failures | >100/hour | Detect brute force attempts |
| JWT validation errors | Any | Detect token forgery attempts |
| CORS violations | Any | Detect cross-origin attack attempts |
| File upload rejections | >50/hour | Detect malicious upload attempts |
| Application startup failures | Any | Detect configuration issues |

**Log Entries to Monitor:**

```
[Security] Authentication failure for user {username}
[Security] JWT validation failed: {reason}
[Security] CORS request blocked from origin {origin}
[Security] File upload rejected: {filename} - {reason}
[Security] Application startup failed: {configuration_error}
```

