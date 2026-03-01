using System;
using System.Threading;
using Npgsql;
using Xunit;

namespace WebVellaErp.Invoicing.Tests.Integration
{
    /// <summary>
    /// Custom xUnit <see cref="FactAttribute"/> that dynamically skips the test when
    /// LocalStack RDS PostgreSQL is not available on port 4510.
    ///
    /// LocalStack Community Edition does not include the RDS PostgreSQL service — it
    /// requires LocalStack Pro. The port may be open (LocalStack listens on it) but
    /// will not speak the PostgreSQL wire protocol. This attribute attempts an actual
    /// Npgsql connection to distinguish "port open but not PostgreSQL" from "real RDS".
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
        /// </summary>
        private static readonly Lazy<string?> _skipReason = new(EvaluateRdsAvailability, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Constructs the attribute and sets <see cref="FactAttribute.Skip"/> if RDS
        /// PostgreSQL is not available, causing xUnit to report the test as Skipped.
        /// </summary>
        public RdsFactAttribute()
        {
            if (_skipReason.Value is not null)
            {
                Skip = _skipReason.Value;
            }
        }

        /// <summary>
        /// Attempts an actual Npgsql connection to localhost:4510 to determine if RDS
        /// PostgreSQL is available. A TCP-only check is insufficient because LocalStack
        /// Community Edition opens port 4510 but does not speak the PostgreSQL wire protocol.
        /// Returns null if available, or a skip reason string if not available.
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
