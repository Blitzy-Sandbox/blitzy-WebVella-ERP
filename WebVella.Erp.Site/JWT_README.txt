=========================================================================
1. add to web site project 
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.1" />


=========================================================================
2. Config.json

add in settings section

"Jwt": {
	"Key": "",
	"Issuer": "webvella-erp-issuer",
	"Audience": "webvella-erp-audience"
}

SECURITY (OWASP A02/A05):
- Do NOT commit a real signing key. Leave "Key" empty in Config.json and supply the secret at runtime via the
  environment variable Settings__Jwt__Key (or a secret store). The application fails fast at startup if the JWT key
  is empty or left at the historical placeholder, so a host will refuse to run with an insecure/default key.
- Use a strong key (recommended 32+ bytes / 256+ bits of entropy from a cryptographically secure RNG).
- "Issuer" and "Audience" MUST be DISTINCT non-secret values (they are different above). Equal issuer/audience is a
  misconfiguration; override per environment via Settings__Jwt__Issuer / Settings__Jwt__Audience if desired.


=========================================================================
3. startup
in ConfigureServices method change auth to be 

 services.AddAuthentication(options =>
{
    options.DefaultScheme = "JWT_OR_COOKIE";
    options.DefaultChallengeScheme = "JWT_OR_COOKIE";
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Cookie.HttpOnly = true;
    // SECURITY (OWASP A05/A07): harden the auth cookie. Secure requires HTTPS in transit; SameSite mitigates CSRF.
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Name = "erp_auth_base";
    options.LoginPath = new PathString("/login");
    options.LogoutPath = new PathString("/logout");
    options.AccessDeniedPath = new PathString("/error?access_denied");
    options.ReturnUrlParameter = "returnUrl";
})
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = Configuration["Settings:Jwt:Issuer"],
            ValidAudience = Configuration["Settings:Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Configuration["Settings:Jwt:Key"]))
        };
    })
    .AddPolicyScheme("JWT_OR_COOKIE", "JWT_OR_COOKIE", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            string authorization = context.Request.Headers[HeaderNames.Authorization];
            if (!string.IsNullOrEmpty(authorization) && authorization.StartsWith("Bearer "))
                return JwtBearerDefaults.AuthenticationScheme;

            return CookieAuthenticationDefaults.AuthenticationScheme;
        };
    });

 in Configure method add 
 
 app.UseJwtMiddleware();

 =========================================================================
 