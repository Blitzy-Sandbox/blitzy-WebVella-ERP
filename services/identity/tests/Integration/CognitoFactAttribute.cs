using System;
using System.Net.Http;
using Xunit;

namespace WebVellaErp.Identity.Tests.Integration
{
    /// <summary>
    /// Custom xUnit <see cref="FactAttribute"/> that validates Cognito (cognito-idp)
    /// is available in the current LocalStack Pro environment.
    ///
    /// LocalStack Pro is REQUIRED for integration tests. If Cognito is not available,
    /// the test will FAIL (not skip), ensuring environment misconfiguration is caught
    /// immediately rather than silently ignored.
    ///
    /// Usage: Replace <c>[Fact]</c> with <c>[CognitoFact]</c> on any test method that
    /// requires Cognito operations (user pool, auth flows, group management).
    ///
    /// The availability check is performed once (lazy, thread-safe) and cached for the
    /// lifetime of the test run. It sends a lightweight ListUserPools request to the
    /// LocalStack endpoint and checks if the response indicates the service is unavailable.
    /// </summary>
    public sealed class CognitoFactAttribute : FactAttribute
    {
        /// <summary>
        /// Lazily evaluated, thread-safe Cognito availability check.
        /// Cached for the lifetime of the test run to avoid repeated HTTP calls.
        /// Returns null if Cognito is available, or an error message if not.
        /// </summary>
        private static readonly Lazy<string?> _unavailableReason = new(EvaluateCognitoAvailability);

        /// <summary>
        /// Constructs the attribute. Unlike the previous skip-based approach,
        /// this attribute never sets <see cref="FactAttribute.Skip"/>.
        /// If Cognito is not available, the test will fail at runtime via
        /// <see cref="EnsureCognitoAvailable"/> rather than being silently skipped.
        /// </summary>
        public CognitoFactAttribute()
        {
            // Intentionally NOT setting Skip — tests must fail, not skip,
            // when LocalStack Pro is not properly configured.
        }

        /// <summary>
        /// Call this from test fixture InitializeAsync or directly from test methods
        /// to fail fast if Cognito is not available.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when Cognito is not available in the LocalStack environment.
        /// </exception>
        public static void EnsureCognitoAvailable()
        {
            if (_unavailableReason.Value is not null)
            {
                throw new InvalidOperationException(
                    $"ENVIRONMENT ERROR: {_unavailableReason.Value} " +
                    "Ensure LocalStack Pro is running with LOCALSTACK_AUTH_TOKEN configured.");
            }
        }

        /// <summary>
        /// Performs a synchronous HTTP probe to determine if the cognito-idp service is
        /// available in LocalStack. Returns null if available, or an error reason string
        /// if not available.
        /// </summary>
        private static string? EvaluateCognitoAvailability()
        {
            try
            {
                var endpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL")
                    ?? "http://localhost:4566";

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Add("X-Amz-Target", "AWSCognitoIdentityProviderService.ListUserPools");
                request.Content = new StringContent(
                    "{\"MaxResults\": 1}",
                    System.Text.Encoding.UTF8,
                    "application/x-amz-json-1.1");

                using var response = httpClient.Send(request);
                using var reader = new System.IO.StreamReader(response.Content.ReadAsStream());
                var body = reader.ReadToEnd();

                // LocalStack returns a specific error message when a service requires Pro
                if (body.Contains("not included within your LocalStack license", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("not yet supported", StringComparison.OrdinalIgnoreCase))
                {
                    return "Cognito (cognito-idp) is not available in the current LocalStack environment — requires LocalStack Pro license.";
                }

                // Any successful response (even an error like ValidationException) means
                // the service is responding and available
                return null;
            }
            catch (HttpRequestException)
            {
                return "Cannot connect to LocalStack endpoint — Cognito availability check failed.";
            }
            catch (TaskCanceledException)
            {
                return "LocalStack endpoint timed out — Cognito availability check failed.";
            }
            catch (Exception ex)
            {
                return $"Cognito availability check failed: {ex.Message}";
            }
        }
    }
}
