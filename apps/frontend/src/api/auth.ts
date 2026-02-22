/**
 * Cognito JWT Token Management Module
 *
 * Replaces the monolith's dual authentication system:
 * - AuthService.cs: Cookie-based ASP.NET Core authentication + HMAC-SHA256 JWT
 *   issuance (BuildTokenAsync with 1440-min expiry, 120-min refresh), validation
 *   (GetValidSecurityTokenAsync), and refresh (GetNewTokenAsync).
 * - JwtMiddleware.cs: Bearer token extraction from Authorization header and
 *   access_token store, JWT validation, and user identity attachment to HttpContext.
 *
 * In the target serverless architecture, AWS Cognito user pools handle credential
 * validation, token issuance, and claims management. This module manages the entire
 * Cognito auth lifecycle from the React SPA frontend.
 *
 * Security constraints (AAP §0.8.1, §0.8.3):
 * - Tokens stored in memory ONLY — never localStorage, sessionStorage, or cookies
 * - No secrets in bundle — COGNITO_CLIENT_SECRET is never included (SSM only)
 * - VITE_ prefix for all env vars via import.meta.env (Vite convention)
 * - LocalStack compatibility via custom endpoint when VITE_IS_LOCAL=true
 */

import {
  CognitoIdentityProviderClient,
  InitiateAuthCommand,
  GlobalSignOutCommand,
  GetUserCommand,
  AuthFlowType,
  type InitiateAuthCommandInput,
  type InitiateAuthCommandOutput,
} from '@aws-sdk/client-cognito-identity-provider';

// ---------------------------------------------------------------------------
// Type Definitions
// ---------------------------------------------------------------------------

/**
 * Result of a successful authentication operation.
 * Returned by login() after successful Cognito USER_PASSWORD_AUTH flow.
 */
export interface AuthResult {
  /** Authenticated user profile extracted from Cognito attributes */
  user: CognitoUser;
  /** Cognito access token for API authorization (Bearer token) */
  accessToken: string;
}

/**
 * Authenticated Cognito user profile.
 * Replaces ErpUser from the monolith (SecurityManager.GetUser).
 */
export interface CognitoUser {
  /** Cognito sub (subject) — replaces ErpUser.Id Guid */
  id: string;
  /** Cognito email attribute — replaces ErpUser.Email */
  email: string;
  /** Cognito groups — replaces ErpUser.Roles */
  roles: string[];
}

/**
 * Full set of tokens returned by Cognito authentication.
 * Used internally for token lifecycle management.
 */
export interface AuthTokens {
  /** JWT access token for API authorization */
  accessToken: string;
  /** Opaque refresh token for obtaining new access/id tokens */
  refreshToken: string;
  /** JWT ID token containing user profile claims */
  idToken: string;
  /** Token validity duration in seconds */
  expiresIn: number;
}

// ---------------------------------------------------------------------------
// Configuration (AAP §0.8.6 environment variables)
// ---------------------------------------------------------------------------

/**
 * Lazy configuration readers — values are read from `import.meta.env` at call
 * time rather than module-load time so that test harnesses (vitest) can set
 * environment variables before invoking auth functions.
 */

/** AWS region for Cognito — defaults to us-east-1 per AAP §0.8.6 */
function getCognitoRegion(): string {
  return (
    (import.meta.env.VITE_AWS_REGION as string | undefined) || 'us-east-1'
  );
}

/**
 * Cognito User Pool App Client ID.
 * Must be a "public client" (no client secret) for SPA use.
 */
function getCognitoClientId(): string {
  return (
    (import.meta.env.VITE_COGNITO_CLIENT_ID as string | undefined) || ''
  );
}

/** API endpoint — used for LocalStack Cognito endpoint override */
function getApiEndpoint(): string {
  return (
    (import.meta.env.VITE_API_URL as string | undefined) ||
    'http://localhost:4566'
  );
}

/** Whether running against LocalStack (development/test mode) */
function isLocalStack(): boolean {
  return import.meta.env.VITE_IS_LOCAL === 'true';
}

/**
 * Buffer (ms) before token expiry to trigger proactive auto-refresh.
 * Mirrors the monolith's JWT_TOKEN_FORCE_REFRESH_MINUTES (120 min) concept
 * from AuthService.cs BuildTokenAsync token_refresh_after claim.
 * Set to 5 minutes for practical SPA use so API calls do not hit an
 * expired token.
 */
const REFRESH_BUFFER_MS: number = 5 * 60 * 1000;

// ---------------------------------------------------------------------------
// In-Memory Token Storage
// CRITICAL: Module-level closure variables — NEVER localStorage/sessionStorage
// ---------------------------------------------------------------------------

let storedAccessToken: string | null = null;
let storedRefreshToken: string | null = null;
let storedIdToken: string | null = null;
let tokenExpiresAt: number = 0; // Unix timestamp in ms
let cachedUser: CognitoUser | null = null;

// ---------------------------------------------------------------------------
// Cognito Client (singleton, lazily initialised)
// ---------------------------------------------------------------------------

let cognitoClient: CognitoIdentityProviderClient | null = null;

/**
 * Returns the singleton CognitoIdentityProviderClient.
 * When running against LocalStack the client uses a custom endpoint and
 * dummy credentials; in production the default Cognito endpoint is used.
 */
function getClient(): CognitoIdentityProviderClient {
  if (!cognitoClient) {
    cognitoClient = new CognitoIdentityProviderClient({
      region: getCognitoRegion(),
      ...(isLocalStack() && {
        endpoint: getApiEndpoint(),
        credentials: {
          accessKeyId: 'test',
          secretAccessKey: 'test',
        },
      }),
    });
  }
  return cognitoClient;
}

// ---------------------------------------------------------------------------
// JWT Payload Decoding (client-side only — no signature verification)
// ---------------------------------------------------------------------------

/**
 * Decodes a JWT payload without verification.
 * Signature validation is the responsibility of API Gateway / Lambda authorizer.
 */
function decodeJwtPayload(
  token: string,
): Record<string, unknown> | null {
  try {
    const parts = token.split('.');
    if (parts.length !== 3) {
      return null;
    }
    const base64Url = parts[1];
    const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
    return JSON.parse(atob(padded)) as Record<string, unknown>;
  } catch {
    return null;
  }
}

/**
 * Extracts a CognitoUser from Cognito ID token claims.
 *
 * Standard Cognito ID-token claims used:
 *  - sub            → user id  (replaces ErpUser.Id)
 *  - email          → email    (replaces ErpUser.Email)
 *  - cognito:groups → roles    (replaces ErpUser.Roles)
 */
function extractUserFromIdToken(idToken: string): CognitoUser | null {
  const payload = decodeJwtPayload(idToken);
  if (!payload) {
    return null;
  }
  const sub = payload['sub'];
  if (typeof sub !== 'string') {
    return null;
  }
  return {
    id: sub,
    email: typeof payload['email'] === 'string' ? (payload['email'] as string) : '',
    roles: Array.isArray(payload['cognito:groups'])
      ? (payload['cognito:groups'] as unknown[]).filter(
          (g): g is string => typeof g === 'string',
        )
      : [],
  };
}

// ---------------------------------------------------------------------------
// Token Lifecycle Helpers
// ---------------------------------------------------------------------------

/**
 * Persists tokens from a Cognito InitiateAuth response into module-level
 * storage and updates the cached expiry timestamp.
 */
function storeTokens(response: InitiateAuthCommandOutput): void {
  const auth = response.AuthenticationResult;
  if (!auth) {
    return;
  }
  storedAccessToken = auth.AccessToken ?? null;
  storedIdToken = auth.IdToken ?? null;
  tokenExpiresAt = auth.ExpiresIn
    ? Date.now() + auth.ExpiresIn * 1000
    : 0;

  // Refresh token is not always returned (e.g. on REFRESH_TOKEN_AUTH)
  if (auth.RefreshToken) {
    storedRefreshToken = auth.RefreshToken;
  }
}

/**
 * Wipes all in-memory tokens, cached user data, and the Cognito client
 * singleton so that subsequent calls will create a fresh client with the
 * latest configuration.
 */
function clearTokens(): void {
  storedAccessToken = null;
  storedRefreshToken = null;
  storedIdToken = null;
  tokenExpiresAt = 0;
  cachedUser = null;
  cognitoClient = null;
}

/** True when no valid access token exists or the token has expired. */
function isTokenExpired(): boolean {
  return !storedAccessToken || tokenExpiresAt === 0 || Date.now() >= tokenExpiresAt;
}

/**
 * True when the access token is within the proactive refresh window.
 * Mirrors the monolith's 120-min token_refresh_after concept.
 */
function isTokenNearExpiry(): boolean {
  return (
    !storedAccessToken ||
    tokenExpiresAt === 0 ||
    Date.now() >= tokenExpiresAt - REFRESH_BUFFER_MS
  );
}

// ---------------------------------------------------------------------------
// Cognito GetUser helper (uses GetUserCommand)
// ---------------------------------------------------------------------------

/**
 * Fetches authoritative user attributes from Cognito via GetUserCommand.
 * Called during login to build the canonical user profile.
 * Falls back to ID-token extraction when the call fails.
 *
 * @param accessToken - valid Cognito access token
 */
async function fetchUserFromCognito(
  accessToken: string,
): Promise<CognitoUser | null> {
  const client = getClient();
  try {
    const command = new GetUserCommand({ AccessToken: accessToken });
    const response = await client.send(command);
    const attrs = response.UserAttributes ?? [];
    const sub =
      attrs.find((a) => a.Name === 'sub')?.Value ??
      response.Username ??
      '';
    const email = attrs.find((a) => a.Name === 'email')?.Value ?? '';

    // Cognito groups are NOT returned by GetUser — they live in the ID token.
    const idTokenUser = storedIdToken
      ? extractUserFromIdToken(storedIdToken)
      : null;

    return {
      id: sub,
      email,
      roles: idTokenUser?.roles ?? [],
    };
  } catch {
    // Graceful degradation: fall back to ID-token extraction
    return storedIdToken ? extractUserFromIdToken(storedIdToken) : null;
  }
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/**
 * Authenticates a user via Cognito USER_PASSWORD_AUTH flow.
 *
 * Replaces:
 *  - AuthService.Authenticate(email, password) — cookie auth
 *  - AuthService.GetTokenAsync(email, password) — JWT issuance
 *  - login.cshtml.cs OnPost — credential validation + redirect
 *
 * @param email    User email (login.cshtml.cs Username field)
 * @param password User password
 * @returns AuthResult with user profile and access token
 * @throws Error with user-friendly message for auth failures
 */
export async function login(
  email: string,
  password: string,
): Promise<AuthResult> {
  if (!email || !password) {
    throw new Error('Email and password are required');
  }
  const clientId = getCognitoClientId();
  if (!clientId) {
    throw new Error(
      'Cognito client ID is not configured. Set VITE_COGNITO_CLIENT_ID environment variable.',
    );
  }

  const client = getClient();
  const input: InitiateAuthCommandInput = {
    AuthFlow: AuthFlowType.USER_PASSWORD_AUTH,
    ClientId: clientId,
    AuthParameters: {
      USERNAME: email,
      PASSWORD: password,
    },
  };

  try {
    const command = new InitiateAuthCommand(input);
    const response: InitiateAuthCommandOutput = await client.send(command);

    if (
      !response.AuthenticationResult?.AccessToken ||
      !response.AuthenticationResult?.IdToken
    ) {
      throw new Error(
        'Authentication succeeded but tokens were not returned',
      );
    }

    // Persist tokens in-memory
    storeTokens(response);

    // Build user profile via GetUserCommand for authoritative attributes,
    // falling back to ID-token extraction
    const user = await fetchUserFromCognito(
      response.AuthenticationResult.AccessToken,
    );
    if (!user) {
      clearTokens();
      throw new Error(
        'Failed to extract user profile from authentication response',
      );
    }

    // Cache user for synchronous getCurrentUser() calls
    cachedUser = user;

    return {
      user,
      accessToken: response.AuthenticationResult.AccessToken,
    };
  } catch (error: unknown) {
    clearTokens();

    if (error instanceof Error) {
      const name = (error as { name?: string }).name;
      switch (name) {
        case 'NotAuthorizedException':
        case 'UserNotFoundException':
          // Identical message prevents user-enumeration attacks.
          // Mirrors login.cshtml.cs "Invalid username or password."
          throw new Error('Invalid username or password');
        case 'UserNotConfirmedException':
          throw new Error(
            'Account has not been confirmed. Please check your email for a confirmation link.',
          );
        case 'PasswordResetRequiredException':
          throw new Error(
            'Password reset is required. Please reset your password.',
          );
        case 'TooManyRequestsException':
          throw new Error(
            'Too many login attempts. Please try again later.',
          );
        case 'InvalidParameterException':
          throw new Error('Invalid login parameters provided');
        default:
          throw error;
      }
    }

    throw new Error(
      'An unexpected error occurred during authentication',
    );
  }
}

/**
 * Signs out the user by invalidating all Cognito tokens server-side and
 * clearing local in-memory storage.
 *
 * Replaces:
 *  - AuthService.Logout() → HttpContext.SignOutAsync(Cookie)
 *  - logout.cshtml.cs OnGet / OnPost → authService.Logout(), redirect "/"
 *
 * Does NOT navigate — callers handle redirect (separation of concerns
 * matching logout.cshtml.cs).
 */
export async function logout(): Promise<void> {
  if (!storedAccessToken) {
    clearTokens();
    return;
  }

  const client = getClient();

  try {
    const command = new GlobalSignOutCommand({
      AccessToken: storedAccessToken,
    });
    await client.send(command);
  } catch {
    // Silent failure — mirrors JwtMiddleware.cs catch-all.
    // Tokens already expired or revoked are not an error for the user.
  } finally {
    clearTokens();
    cognitoClient = null; // fresh client on next login
  }
}

/**
 * Refreshes the access token using the stored refresh token via Cognito
 * REFRESH_TOKEN_AUTH flow.
 *
 * Replaces AuthService.GetNewTokenAsync(tokenString) which validated the
 * existing token, extracted NameIdentifier, verified the user was enabled,
 * and re-issued a new JWT.
 *
 * @returns New access token, or null if refresh fails (re-login required)
 */
export async function refreshAccessToken(): Promise<string | null> {
  const clientId = getCognitoClientId();
  if (!storedRefreshToken || !clientId) {
    clearTokens();
    return null;
  }

  const client = getClient();
  const input: InitiateAuthCommandInput = {
    AuthFlow: AuthFlowType.REFRESH_TOKEN_AUTH,
    ClientId: clientId,
    AuthParameters: {
      REFRESH_TOKEN: storedRefreshToken,
    },
  };

  try {
    const command = new InitiateAuthCommand(input);
    const response: InitiateAuthCommandOutput = await client.send(command);

    if (!response.AuthenticationResult?.AccessToken) {
      clearTokens();
      return null;
    }

    storeTokens(response);

    // Refresh cached user from updated ID token
    if (storedIdToken) {
      const updatedUser = extractUserFromIdToken(storedIdToken);
      if (updatedUser) {
        cachedUser = updatedUser;
      }
    }

    return storedAccessToken;
  } catch {
    // Refresh token expired or revoked — mirrors GetNewTokenAsync returning null
    clearTokens();
    return null;
  }
}

/**
 * Returns a valid access token, auto-refreshing if needed.
 *
 * Replaces the token-extraction pipeline in JwtMiddleware.cs:
 *  1. context.GetTokenAsync("access_token")
 *  2. Authorization: Bearer header (substring 7)
 *  3. AuthService.GetValidSecurityTokenAsync validation
 *
 * Called by the API client interceptor (client.ts) to attach the Bearer
 * token to every outgoing request.
 *
 * Auto-refresh mirrors the monolith's dual-window approach: BuildTokenAsync
 * sets token_refresh_after at UTC+120 min and expires at 1440 min.
 *
 * @returns Valid access token, or null if unauthenticated / refresh fails
 */
export async function getAccessToken(): Promise<string | null> {
  if (!storedAccessToken) {
    return null;
  }

  // Fully expired — must refresh
  if (isTokenExpired()) {
    return refreshAccessToken();
  }

  // Near expiry — proactively refresh but return current token on failure
  if (isTokenNearExpiry()) {
    const refreshed = await refreshAccessToken();
    return refreshed ?? storedAccessToken;
  }

  return storedAccessToken;
}

/**
 * Synchronous check: does a valid (non-expired) access token exist?
 * Used by ProtectedRoute and auth context.
 * Does NOT trigger refresh — use getAccessToken() for that.
 */
export function isAuthenticated(): boolean {
  return storedAccessToken !== null && !isTokenExpired();
}

/**
 * Returns the current authenticated user's profile.
 *
 * Replaces:
 *  - AuthService.GetUser(ClaimsPrincipal) — NameIdentifier extraction + DB load
 *  - JwtMiddleware.cs HttpContext.Items["User"] user attachment
 *
 * Returns a cached CognitoUser built during login (via GetUserCommand) or
 * refreshed from the ID token on token refresh. Returns null when the user
 * is not authenticated.
 */
export function getCurrentUser(): CognitoUser | null {
  if (!storedIdToken) {
    return null;
  }

  // Return cached user when available; rebuild from token otherwise
  if (cachedUser) {
    return cachedUser;
  }

  const user = extractUserFromIdToken(storedIdToken);
  if (user) {
    cachedUser = user;
  }
  return user;
}
