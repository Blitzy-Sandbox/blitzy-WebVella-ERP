# Security Policy

[![Security](https://img.shields.io/badge/Security-Policy-blue?style=for-the-badge)](https://github.com/WebVella/WebVella-ERP/blob/master/SECURITY.md)
[![OWASP](https://img.shields.io/badge/OWASP-Compliant-green?style=for-the-badge)](https://owasp.org/www-project-top-ten/)

This document outlines the security policies, configuration requirements, and best practices for deploying WebVella ERP in production environments. **All deployment teams must review and implement the requirements in this document before deploying to production.**

---

## Table of Contents

- [Security Vulnerability Reporting](#security-vulnerability-reporting)
- [Required Security Configuration](#required-security-configuration)
- [Environment Variables](#environment-variables)
- [OWASP Compliance](#owasp-compliance)
- [Pre-Deployment Security Checklist](#pre-deployment-security-checklist)
- [Post-Deployment Monitoring](#post-deployment-monitoring)
- [Security Best Practices](#security-best-practices)

---

## Security Vulnerability Reporting

### Reporting a Vulnerability

We take security vulnerabilities seriously. If you discover a security issue in WebVella ERP, please report it responsibly.

**How to Report:**

1. **Email:** Send details to security@webvella.com (or create a private security advisory on GitHub)
2. **GitHub Security Advisory:** Use the [Security Advisory](https://github.com/WebVella/WebVella-ERP/security/advisories/new) feature for private disclosure
3. **Do NOT** create public GitHub issues for security vulnerabilities

**What to Include:**

- Description of the vulnerability
- Steps to reproduce the issue
- Potential impact assessment
- Any proof-of-concept code (if applicable)
- Your contact information for follow-up

### Response Timeline

| Action | Expected Timeline |
|--------|-------------------|
| Initial acknowledgment | Within 48 hours |
| Preliminary assessment | Within 5 business days |
| Fix development | Depends on severity (Critical: 7 days, High: 14 days, Medium: 30 days) |
| Public disclosure | After fix is released and users have time to update |

### Scope of Security Reports

**In Scope:**
- Authentication and authorization vulnerabilities
- Cryptographic weaknesses
- Injection vulnerabilities (SQL, XSS, etc.)
- Cross-Site Request Forgery (CSRF)
- Security misconfigurations
- Sensitive data exposure
- Session management issues
- File upload vulnerabilities

**Out of Scope:**
- Denial of Service (DoS) attacks
- Social engineering attacks
- Physical security issues
- Third-party dependencies (report to upstream maintainers)
- Issues in outdated versions (only current major version supported)

---

## Required Security Configuration

**⚠️ CRITICAL: The application will fail to start if these security configurations are not properly set.**

### JWT Key Configuration

The JWT signing key is used to sign authentication tokens. A weak or default key allows attackers to forge valid authentication tokens.

| Requirement | Value |
|-------------|-------|
| Minimum Length | 32 characters (256 bits) |
| Character Set | Cryptographically random, use all ASCII printable characters |
| Default Value | **None** - Must be explicitly configured |
| Startup Behavior | Application fails to start if key is missing or weak |

**Generate a Secure JWT Key:**

```bash
# Linux/macOS
openssl rand -base64 48

# PowerShell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
```

**Configuration Example (Config.json):**

```json
{
  "Settings": {
    "JwtKey": "YOUR_GENERATED_KEY_HERE_MINIMUM_32_CHARACTERS"
  }
}
```

**Validation Rules:**
- Key must be at least 32 characters
- Key cannot be "ThisIsMySecretKey" or similar weak patterns
- Key cannot contain only repeated characters
- Application startup will fail with descriptive error if validation fails

### Encryption Key Configuration

The encryption key is used for encrypting sensitive data at rest. Without proper configuration, encryption operations will fail.

| Requirement | Value |
|-------------|-------|
| Minimum Length | 16 characters (128 bits) for AES-128, 32 characters for AES-256 |
| Format | Base64-encoded or raw string |
| Default Value | **None** - Must be explicitly configured |
| Key Derivation | PBKDF2 with minimum 10,000 iterations |

**Generate a Secure Encryption Key:**

```bash
# Linux/macOS
openssl rand -base64 32

# PowerShell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
```

**Configuration Example (Config.json):**

```json
{
  "Settings": {
    "EncryptionKey": "YOUR_GENERATED_ENCRYPTION_KEY_HERE"
  }
}
```

### CORS (Cross-Origin Resource Sharing) Configuration

CORS must be configured to allow only trusted origins. Overly permissive CORS policies allow malicious websites to make authenticated requests on behalf of users.

| Requirement | Value |
|-------------|-------|
| Default Policy | Deny all origins except explicitly configured |
| Allowed Methods | GET, POST, PUT, DELETE, PATCH |
| Credentials | Only with specific origins (not with wildcards) |

**Configuration Example (Config.json):**

```json
{
  "Settings": {
    "AllowedOrigins": "https://yourdomain.com,https://admin.yourdomain.com"
  }
}
```

**Important Notes:**
- Never use `*` (wildcard) in production
- List only domains that need API access
- Include protocol (https://) in origins
- Multiple origins separated by commas

### Session Management Configuration

Session cookies control how long users remain authenticated. Excessive session duration increases the window for session hijacking attacks.

| Setting | Default | Recommended Range |
|---------|---------|-------------------|
| Cookie Expiration | 30 days | 1-90 days |
| Sliding Expiration | Enabled | Keep enabled for better UX |
| Secure Flag | True (HTTPS only) | Always true in production |
| HttpOnly Flag | True | Always true |
| SameSite | Strict | Strict or Lax |

**Configuration Example (Config.json):**

```json
{
  "Settings": {
    "CookieExpirationDays": 30
  }
}
```

### File Upload Security Configuration

File uploads are restricted to prevent malicious file execution and storage exhaustion attacks.

| Setting | Default | Description |
|---------|---------|-------------|
| Maximum Size | 10 MB (10485760 bytes) | Per-file upload limit |
| Allowed Extensions | .jpg,.jpeg,.png,.gif,.pdf,.doc,.docx,.xls,.xlsx | Whitelist of permitted extensions |
| MIME Validation | Enabled | Content-type must match extension |
| Executable Blocking | Enabled | .exe, .bat, .sh, .php, etc. always blocked |

**Configuration Example (Config.json):**

```json
{
  "Settings": {
    "MaxUploadSizeBytes": 10485760,
    "AllowedFileExtensions": ".jpg,.jpeg,.png,.gif,.pdf,.doc,.docx,.xls,.xlsx,.csv,.txt"
  }
}
```

---

## Environment Variables

Environment variables provide a secure way to configure sensitive settings without storing them in configuration files. Environment variables take precedence over Config.json values.

| Variable | Description | Required | Default |
|----------|-------------|----------|---------|
| `ERP_JWT_KEY` | JWT signing key (minimum 32 characters) | **Yes** | None |
| `ERP_ENCRYPTION_KEY` | Data encryption key | **Yes** | None |
| `ERP_ALLOWED_ORIGINS` | Comma-separated list of allowed CORS origins | No | localhost only |
| `ERP_COOKIE_EXPIRATION_DAYS` | Session cookie lifetime in days | No | 30 |
| `ERP_MAX_UPLOAD_SIZE` | Maximum file upload size in bytes | No | 10485760 |

**Setting Environment Variables:**

**Linux/macOS:**
```bash
export ERP_JWT_KEY="your-secure-jwt-key-minimum-32-characters"
export ERP_ENCRYPTION_KEY="your-secure-encryption-key"
export ERP_ALLOWED_ORIGINS="https://yourdomain.com"
export ERP_COOKIE_EXPIRATION_DAYS="30"
export ERP_MAX_UPLOAD_SIZE="10485760"
```

**Windows PowerShell:**
```powershell
$env:ERP_JWT_KEY = "your-secure-jwt-key-minimum-32-characters"
$env:ERP_ENCRYPTION_KEY = "your-secure-encryption-key"
$env:ERP_ALLOWED_ORIGINS = "https://yourdomain.com"
$env:ERP_COOKIE_EXPIRATION_DAYS = "30"
$env:ERP_MAX_UPLOAD_SIZE = "10485760"
```

**Docker:**
```yaml
environment:
  - ERP_JWT_KEY=your-secure-jwt-key-minimum-32-characters
  - ERP_ENCRYPTION_KEY=your-secure-encryption-key
  - ERP_ALLOWED_ORIGINS=https://yourdomain.com
  - ERP_COOKIE_EXPIRATION_DAYS=30
  - ERP_MAX_UPLOAD_SIZE=10485760
```

**Configuration Priority:**
1. Environment variables (highest priority)
2. Config.json settings
3. No defaults for security-critical settings (application fails)

---

## OWASP Compliance

WebVella ERP implements security controls aligned with the OWASP Top 10 (2021) security risks.

### A02:2021 - Cryptographic Failures

| Control | Implementation |
|---------|----------------|
| Password Hashing | BCrypt with cost factor 12 (adaptive hashing) |
| Key Derivation | PBKDF2 with minimum 10,000 iterations |
| Initialization Vectors | Cryptographically random IV per encryption operation |
| Encryption Algorithm | AES-256 in CBC mode with PKCS7 padding |
| Salt Generation | Automatic 128-bit salt per password hash |

**Verification:**
- Password hashes in database start with `$2a$12$` (BCrypt version 2a, cost 12)
- Each encryption produces different ciphertext for same plaintext (random IV)

### A05:2021 - Security Misconfiguration

| Control | Implementation |
|---------|----------------|
| Security Headers | X-Frame-Options, X-Content-Type-Options, CSP, HSTS, Referrer-Policy |
| CORS Policy | Explicit origin whitelist (no wildcards in production) |
| Error Handling | Generic error messages to users, detailed logging for administrators |
| Default Credentials | No default keys or passwords - explicit configuration required |

**Security Headers Applied:**

| Header | Value | Purpose |
|--------|-------|---------|
| X-Frame-Options | DENY | Prevents clickjacking |
| X-Content-Type-Options | nosniff | Prevents MIME sniffing |
| Content-Security-Policy | default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline' | Controls resource loading |
| Strict-Transport-Security | max-age=31536000; includeSubDomains | Enforces HTTPS |
| Referrer-Policy | strict-origin-when-cross-origin | Controls referrer information |
| Permissions-Policy | geolocation=(), microphone=(), camera=() | Restricts browser features |

### A07:2021 - Identification and Authentication Failures

| Control | Implementation |
|---------|----------------|
| Password Storage | BCrypt adaptive hashing (not MD5, SHA-1, or plain SHA-256) |
| Session Duration | Configurable expiration with sliding window |
| JWT Validation | Strong key requirement, proper signature verification |
| Authentication Logging | Failed authentication attempts logged with details |
| Credential Rotation | Support for key rotation without downtime |

**Backward Compatibility:**
- Legacy MD5 password hashes are automatically upgraded to BCrypt upon successful login
- No user action required for password migration

### A08:2021 - Software and Data Integrity Failures

| Control | Implementation |
|---------|----------------|
| File Upload Validation | Extension whitelist, MIME type verification, size limits |
| Content Verification | Magic byte validation matches declared content type |
| Path Sanitization | Filename sanitization prevents path traversal attacks |
| Executable Blocking | Server-side scripts and executables are always rejected |

**Blocked File Types:**
- Executables: .exe, .dll, .so, .dylib
- Scripts: .php, .asp, .aspx, .jsp, .sh, .bat, .ps1, .py, .rb
- Archives with executables: Validated after extraction
- Double extensions: file.jpg.exe blocked

---

## Pre-Deployment Security Checklist

Complete this checklist before deploying WebVella ERP to production.

### Critical Security Configuration

- [ ] **JWT Key Configured:** Generate and configure a cryptographically random JWT key (minimum 32 characters)
- [ ] **Encryption Key Configured:** Generate and configure encryption key for data protection
- [ ] **CORS Origins Defined:** Configure specific allowed origins (no wildcards)
- [ ] **HTTPS Enabled:** SSL/TLS certificate installed and HTTP redirects to HTTPS
- [ ] **Environment Variables Set:** Production secrets configured via environment variables

### Security Verification

- [ ] **No Hard-coded Secrets:** Codebase scanned for hard-coded credentials
- [ ] **Security Headers Present:** All OWASP-recommended headers verified in HTTP responses
- [ ] **CORS Policy Tested:** Unauthorized origins confirmed blocked
- [ ] **File Upload Validation Tested:** Malicious files confirmed rejected
- [ ] **Session Management Tested:** Cookie expiration verified (login, timeout, logout)
- [ ] **Password Hashing Verified:** New password hashes in BCrypt format (`$2a$12$...`)

### Dependency Security

- [ ] **Dependencies Updated:** All NuGet packages at latest secure versions
- [ ] **Vulnerability Scan Passed:** `dotnet list package --vulnerable` shows no Critical/High CVEs
- [ ] **Security Advisories Reviewed:** GitHub security advisories checked for known issues

### Infrastructure Security

- [ ] **Database Access Restricted:** Database not publicly accessible, firewall configured
- [ ] **Logging Configured:** Security events logging to secure, tamper-resistant storage
- [ ] **Backup Encryption:** Database backups encrypted at rest
- [ ] **Network Segmentation:** Application server isolated from unnecessary network access

### Verification Commands

```bash
# Check for vulnerable packages
dotnet list package --vulnerable --include-transitive

# Run security tests
dotnet test --filter "Category=Security" --verbosity detailed

# Build with security analyzers
dotnet build /p:RunAnalyzers=true /p:TreatWarningsAsErrors=true

# Verify application starts with security configuration
dotnet run --project WebVella.Erp.Site
```

---

## Post-Deployment Monitoring

### Security Event Monitoring

Monitor these events to detect potential security incidents:

| Event Type | Alert Threshold | Action |
|------------|-----------------|--------|
| Authentication Failures | >100/hour from single IP | Investigate potential brute force |
| JWT Validation Errors | Any | Investigate token forgery attempt |
| CORS Violations | Any | Review origin configuration |
| File Upload Rejections | >50/hour | Check for malicious upload attempts |
| Configuration Errors | Any | Immediate investigation |

### Log Entries to Monitor

```
[Security] Authentication failure for user {username} from IP {ip}
[Security] JWT validation failed: {reason}
[Security] CORS request blocked from origin {origin}
[Security] File upload rejected: {filename} - {reason}
[Security] Application startup failed: {configuration_error}
[Security] Password upgraded from legacy hash for user {username}
```

### Recommended Monitoring Tools

- **Application Insights:** Azure-based monitoring with security event tracking
- **ELK Stack:** Elasticsearch, Logstash, Kibana for log aggregation
- **Prometheus + Grafana:** Metrics collection and visualization
- **Sentry:** Error tracking with security context

### Periodic Security Reviews

| Review Type | Frequency | Scope |
|-------------|-----------|-------|
| Dependency Vulnerability Scan | Weekly | All NuGet packages |
| Access Log Review | Weekly | Authentication and authorization events |
| Configuration Audit | Monthly | Security settings verification |
| Penetration Testing | Annually | Full application security assessment |
| Security Training | Annually | Development team security awareness |

---

## Security Best Practices

### For Administrators

1. **Use Strong Keys:** Generate cryptographically random keys for JWT and encryption
2. **Rotate Credentials:** Periodically rotate API keys and encryption keys
3. **Monitor Logs:** Set up alerts for security-related log entries
4. **Keep Updated:** Apply security patches promptly
5. **Backup Securely:** Encrypt backups and test restoration procedures
6. **Limit Access:** Apply principle of least privilege for all accounts

### For Developers

1. **Never Commit Secrets:** Use environment variables or secure vaults
2. **Validate Input:** Always validate and sanitize user input
3. **Use Parameterized Queries:** Prevent SQL injection
4. **Encode Output:** Prevent XSS by encoding output
5. **Handle Errors Securely:** Don't expose stack traces to users
6. **Review Dependencies:** Check for known vulnerabilities before adding packages

### For Deployment

1. **Use HTTPS:** Always use TLS 1.2 or higher
2. **Configure Firewalls:** Restrict network access to necessary ports
3. **Disable Debug Mode:** Never run debug mode in production
4. **Secure Configuration:** Store secrets in secure vaults (Azure Key Vault, HashiCorp Vault)
5. **Regular Updates:** Keep OS, runtime, and dependencies updated
6. **Audit Access:** Log and review all administrative actions

---

## Additional Resources

- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [OWASP .NET Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/DotNet_Security_Cheat_Sheet.html)
- [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
- [OWASP Secure Headers Project](https://owasp.org/www-project-secure-headers/)
- [ASP.NET Core Security Documentation](https://docs.microsoft.com/en-us/aspnet/core/security/)
- [NIST Cryptographic Standards](https://csrc.nist.gov/publications/sp)

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0.0 | 2025 | Initial security policy documentation |

---

## Contact

For security-related inquiries:
- **Security Reports:** security@webvella.com
- **General Questions:** [GitHub Discussions](https://github.com/WebVella/WebVella-ERP/discussions)
- **Homepage:** [webvella.com](https://webvella.com)
