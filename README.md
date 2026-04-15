[NEW PROJECT ALERT] Check out our new project for [Data collaboration - Tefter.bg](https://github.com/WebVella/WebVella.Tefter).

[NEW PROJECT ALERT] Check out our new project for [Document template generation](https://github.com/WebVella/WebVella.DocumentTemplates).

---

[![Project Homepage](https://img.shields.io/badge/Homepage-blue?style=for-the-badge)](https://webvella.com)
[![Dotnet](https://img.shields.io/badge/platform-.NET-blue?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![GitHub Repo stars](https://img.shields.io/github/stars/WebVella/WebVella-ERP?style=for-the-badge)](https://github.com/WebVella/WebVella-ERP/stargazers)
[![Nuget version](https://img.shields.io/nuget/v/WebVella.ERP?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![Nuget download](https://img.shields.io/nuget/dt/WebVella.ERP?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![WebVella Document Templates License](https://img.shields.io/badge/MIT-green?style=for-the-badge)](https://github.com/WebVella/WebVella-ERP/blob/master/LICENSE.txt)

---

WebVella ERP 
======
**WebVella ERP** is a free and open-source web software, that targets extreme customization and plugability in service of any business data management needs. It is build upon our experience, best practices and the newest available technologies. Currently it targets ASP.NET Core 9. Our database of choice is PostgreSQL 16. Targets Linux or Windows as host OS. Currently tested only on Windows.

If you want this project to continue or just like it, we will greatly appreciate your support of the project by: 
* giving it a "star" 
* contributing to the source
* Become a Sponsor: Click on the Sponsor button and Thank you in advance

Related repositories

[WebVella-ERP-StencilJs](https://github.com/WebVella/WebVella-ERP-StencilJs)

[WebVella-ERP-Seed](https://github.com/WebVella/WebVella-ERP-Seed)

[WebVella-TagHelpers](https://github.com/WebVella/TagHelpers)


## Security Configuration

> ⚠️ **IMPORTANT:** Security configuration is **mandatory** before production deployment. The application will fail to start if required security settings are not configured.

For detailed security policies, vulnerability reporting procedures, and security best practices, see [SECURITY.md](https://github.com/WebVella/WebVella-ERP/blob/master/SECURITY.md).

### Required Security Settings

The following security settings must be configured in `Config.json` or via environment variables before deploying to production:

| Setting | Description | Requirement |
|---------|-------------|-------------|
| `JwtKey` | JWT signing key for authentication tokens | **Minimum 32 characters**, cryptographically random |
| `EncryptionKey` | Data encryption key | Must be configured, no default value |
| `AllowedOrigins` | CORS allowed origins | Comma-separated list of allowed domains |
| `CookieExpirationDays` | Session cookie lifetime | Default: 30 days |
| `MaxUploadSizeBytes` | Maximum file upload size | Default: 10485760 (10MB) |
| `AllowedFileExtensions` | Permitted file upload extensions | Default: `.jpg,.jpeg,.png,.gif,.pdf,.doc,.docx,.xls,.xlsx` |

### Generating Secure Keys

Use cryptographically secure random generation for keys:

```bash
# Generate secure JWT key (48+ bytes recommended)
openssl rand -base64 48

# Generate secure encryption key (32 bytes)
openssl rand -base64 32
```

### Environment Variable Overrides

Security settings can be overridden via environment variables for production deployments:

| Environment Variable | Config.json Setting | Description |
|---------------------|---------------------|-------------|
| `ERP_JWT_KEY` | `Settings:JwtKey` | JWT signing key (≥32 characters) |
| `ERP_ENCRYPTION_KEY` | `Settings:EncryptionKey` | Data encryption key |
| `ERP_ALLOWED_ORIGINS` | `Settings:AllowedOrigins` | CORS allowed origins |
| `ERP_COOKIE_EXPIRATION_DAYS` | `Settings:CookieExpirationDays` | Session duration in days |
| `ERP_MAX_UPLOAD_SIZE` | `Settings:MaxUploadSizeBytes` | Max upload size in bytes |

**Configuration Priority:** Environment Variables > Config.json > (no defaults for security-critical settings)

### Security Standards

This project follows security guidelines from:
- [OWASP Top 10 (2021)](https://owasp.org/Top10/)
- [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
- [OWASP Security Headers Project](https://owasp.org/www-project-secure-headers/)
- [OWASP .NET Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/DotNet_Security_Cheat_Sheet.html)

### Pre-Deployment Checklist

Before deploying to production, ensure:

- [ ] Strong JWT signing key configured (≥32 random characters)
- [ ] Encryption key configured (no default value used)
- [ ] CORS origins restricted to your domain(s)
- [ ] File upload restrictions appropriate for your use case
- [ ] Security headers enabled (automatic with middleware)
- [ ] HTTPS enforced in production environment


### Third party libraries
* see [LIBRARIES](https://github.com/WebVella/WebVella-ERP/blob/master/LIBRARIES.md) files

## License 
* see [LICENSE](https://github.com/WebVella/WebVella-ERP/blob/master/LICENSE.txt) file

## Contact
#### Developer/Company
* Homepage: [webvella.com](http://webvella.com)
* Twitter: [@webvella](https://twitter.com/webvella "webvella on twitter")



