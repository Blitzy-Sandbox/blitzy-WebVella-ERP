using System;
using System.Threading;
using Npgsql;
using Xunit;

namespace WebVellaErp.Reporting.Tests.Integration
{
    /// <summary>
    /// Custom xUnit <see cref="FactAttribute"/> that validates LocalStack RDS PostgreSQL
    /// is available on port 4510.
    ///
    /// LocalStack Pro is REQUIRED for integration tests. If RDS PostgreSQL is not
    /// available, the test will FAIL (not skip), ensuring environment misconfiguration
    /// is caught immediately rather than silently ignored.
    ///
    /// Usage: Replace <c>[Fact]</c> with <c>[RdsFact]</c> on any test method that
    /// requires RDS PostgreSQL operations (migrations, queries, transactions).
    ///
    /// The availability check is performed once (lazy, thread-safe) and cached for the
    /// lifetime of the test run.
    /// </summary>
    public sealed class RdsFactAttribute : FactAttribute
    {
        /// <summary>
        /// Lazily evaluated, thread-safe RDS PostgreSQL availability check.
        /// Cached for the lifetime of the test run to avoid repeated connection attempts.
        /// Returns null if RDS is available, or an error message if not.
        /// </summary>
        private static readonly Lazy<string?> _unavailableReason = new(EvaluateRdsAvailability, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Constructs the attribute. Unlike the previous skip-based approach,
        /// this attribute never sets <see cref="FactAttribute.Skip"/>.
        /// If RDS is not available, the test will fail at runtime rather than
        /// being silently skipped.
        /// </summary>
        public RdsFactAttribute()
        {
            // Intentionally NOT setting Skip — tests must fail, not skip,
            // when LocalStack Pro is not properly configured.
        }

        /// <summary>
        /// Call this from test fixture InitializeAsync or directly from test methods
        /// to fail fast if RDS PostgreSQL is not available.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when RDS PostgreSQL is not available in the LocalStack environment.
        /// </exception>
        public static void EnsureRdsAvailable()
        {
            if (_unavailableReason.Value is not null)
            {
                throw new InvalidOperationException(
                    $"ENVIRONMENT ERROR: {_unavailableReason.Value} " +
                    "Ensure LocalStack Pro is running with LOCALSTACK_AUTH_TOKEN configured " +
                    "and an RDS PostgreSQL instance has been created.");
            }
        }

        /// <summary>
        /// Attempts an actual Npgsql connection to localhost:4510 to determine if RDS
        /// PostgreSQL is available. A TCP-only check is insufficient because LocalStack
        /// Community Edition opens port 4510 but does not speak the PostgreSQL wire protocol.
        /// Returns null if available, or an error reason string if not available.
        /// </summary>
        private static string? EvaluateRdsAvailability()
        {
            const string connectionString =
                "Host=localhost;Port=4510;Database=postgres;Username=test;Password=test;Timeout=5";

            try
            {
                using var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                using var cmd = new NpgsqlCommand("SELECT 1", connection);
                cmd.ExecuteScalar();
                return null; // Connection succeeded — RDS PostgreSQL is available
            }
            catch (NpgsqlException)
            {
                return "LocalStack RDS PostgreSQL is not available on localhost:4510 (Npgsql connection failed). "
                     + "Requires LocalStack Pro license with RDS support enabled.";
            }
            catch (TimeoutException)
            {
                return "LocalStack RDS PostgreSQL timed out on localhost:4510. "
                     + "Requires LocalStack Pro license with RDS support enabled.";
            }
            catch (Exception ex)
            {
                return $"RDS PostgreSQL availability check failed: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
