using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using WebVella.Erp.Service.Admin.Jobs;
using WebVella.Erp.Service.Admin.Services;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Admin.Jobs
{
    /// <summary>
    /// Unit tests for <see cref="ClearJobAndErrorLogsJob"/> — the Admin service's background
    /// log cleanup worker migrated from WebVella.Erp.Plugins.SDK.Jobs.ClearJobAndErrorLogsJob.
    ///
    /// Tests cover:
    /// - BackgroundService type inheritance verification (migration from ErpJob)
    /// - Constructor DI null guard validation (IServiceScopeFactory, ILogger, IConfiguration)
    /// - RunCleanupAsync business logic delegation to ILogService.ClearJobAndErrorLogs()
    /// - Error isolation: exceptions from ILogService caught and logged via ILogger (never propagated)
    /// - SecurityContext.OpenSystemScope() pattern preservation and scope disposal on exception
    /// - ExecuteAsync method signature (override from BackgroundService, CancellationToken parameter)
    /// - Configurable timer interval (default 1440 minutes = 24 hours from SdkPlugin.cs line 95)
    /// - Graceful cancellation via CancellationToken replacing monolith's JobContext.Aborted
    /// </summary>
    public class ClearJobAndErrorLogsJobTests
    {
        #region Helper Methods

        /// <summary>
        /// Creates an empty <see cref="IConfiguration"/> where GetValue returns defaults.
        /// When the constructor reads Jobs:ClearLogsIntervalMinutes with default 1440,
        /// the empty config returns 1440 (no override present).
        /// </summary>
        private static IConfiguration CreateDefaultConfiguration()
        {
            return new ConfigurationBuilder().Build();
        }

        /// <summary>
        /// Builds the mock DI chain required by ClearJobAndErrorLogsJob's constructor and
        /// RunCleanupAsync's DI scope resolution pattern:
        ///   IServiceScopeFactory → CreateScope() → IServiceScope → ServiceProvider
        ///     → GetRequiredService&lt;ILogService&gt;()
        ///
        /// Optionally configures ILogService.ClearJobAndErrorLogs() to throw an exception
        /// for error-handling tests (preserving monolith source lines 20-23 catch pattern).
        /// </summary>
        /// <param name="logServiceThrows">When true, ClearJobAndErrorLogs() throws an exception.</param>
        /// <param name="customException">Specific exception to throw; defaults to InvalidOperationException.</param>
        /// <returns>Tuple of mock dependencies for test arrangement.</returns>
        private static (
            Mock<IServiceScopeFactory> ScopeFactory,
            Mock<ILogService> LogService,
            Mock<ILogger<ClearJobAndErrorLogsJob>> Logger
        ) CreateMockDependencies(bool logServiceThrows = false, Exception customException = null)
        {
            // Mock ILogService — the core dependency whose ClearJobAndErrorLogs() method
            // is the business rule being tested (monolith source line 18)
            var mockLogService = new Mock<ILogService>();
            if (logServiceThrows)
            {
                var exception = customException ?? new InvalidOperationException("Test cleanup failure");
                mockLogService.Setup(s => s.ClearJobAndErrorLogs()).Throws(exception);
            }

            // Mock IServiceProvider to resolve ILogService via GetRequiredService<ILogService>()
            // Replicates the DI resolution in RunCleanupAsync (dest line 145):
            //   scope.ServiceProvider.GetRequiredService<ILogService>()
            var mockServiceProvider = new Mock<IServiceProvider>();
            mockServiceProvider
                .Setup(p => p.GetService(typeof(ILogService)))
                .Returns(mockLogService.Object);

            // Mock IServiceScope wrapping the service provider
            var mockScope = new Mock<IServiceScope>();
            mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);

            // Mock IServiceScopeFactory — injected into the BackgroundService constructor
            // to create scoped DI containers in RunCleanupAsync (dest line 144):
            //   using var scope = _serviceScopeFactory.CreateScope();
            var mockScopeFactory = new Mock<IServiceScopeFactory>();
            mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

            // Mock ILogger<ClearJobAndErrorLogsJob> — verifiable for error logging tests
            var mockLogger = new Mock<ILogger<ClearJobAndErrorLogsJob>>();

            return (mockScopeFactory, mockLogService, mockLogger);
        }

        /// <summary>
        /// Creates a fully-configured <see cref="ClearJobAndErrorLogsJob"/> instance
        /// with the provided or default configuration (1440-minute interval).
        /// </summary>
        private static ClearJobAndErrorLogsJob CreateJob(
            Mock<IServiceScopeFactory> scopeFactory,
            Mock<ILogger<ClearJobAndErrorLogsJob>> logger,
            IConfiguration configuration = null)
        {
            configuration = configuration ?? CreateDefaultConfiguration();
            return new ClearJobAndErrorLogsJob(scopeFactory.Object, logger.Object, configuration);
        }

        /// <summary>
        /// Invokes the private RunCleanupAsync method via reflection to test the core
        /// business logic without the 30-second initial delay in ExecuteAsync.
        ///
        /// RunCleanupAsync contains the extracted monolith pattern:
        ///   1. Opens SecurityContext.OpenSystemScope() (source line 14)
        ///   2. Creates DI scope and resolves ILogService (replacing source line 18: new LogService())
        ///   3. Calls ILogService.ClearJobAndErrorLogs() (source line 18)
        ///   4. Catches and logs exceptions (source lines 20-23)
        /// </summary>
        private static async Task InvokeRunCleanupAsync(
            ClearJobAndErrorLogsJob job,
            CancellationToken cancellationToken = default)
        {
            var method = typeof(ClearJobAndErrorLogsJob)
                .GetMethod("RunCleanupAsync", BindingFlags.NonPublic | BindingFlags.Instance);

            if (method == null)
            {
                throw new InvalidOperationException(
                    "RunCleanupAsync method not found on ClearJobAndErrorLogsJob. " +
                    "Ensure the method exists as a private instance method.");
            }

            var task = (Task)method.Invoke(job, new object[] { cancellationToken });
            await task;
        }

        #endregion

        #region Test 1: BackgroundService Inheritance

        /// <summary>
        /// Verifies ClearJobAndErrorLogsJob inherits from <see cref="BackgroundService"/>,
        /// confirming the migration from the monolith's ErpJob base class to the
        /// ASP.NET Core hosted service pattern.
        ///
        /// Monolith: [Job("99D9A8BB-...")] public class ClearJobAndErrorLogsJob : ErpJob
        /// Microservice: public class ClearJobAndErrorLogsJob : BackgroundService
        /// </summary>
        [Fact]
        public void ClearJobAndErrorLogsJob_ShouldInheritFromBackgroundService()
        {
            // Act & Assert
            typeof(ClearJobAndErrorLogsJob)
                .Should().BeAssignableTo<BackgroundService>(
                    "the monolith's ErpJob base class is replaced by BackgroundService " +
                    "in the microservice architecture");
        }

        #endregion

        #region Tests 2-4: Constructor Null Guard Validation

        /// <summary>
        /// Verifies the constructor throws <see cref="ArgumentNullException"/> when
        /// IServiceScopeFactory is null. IServiceScopeFactory is required to create
        /// DI scopes for resolving scoped ILogService inside the singleton BackgroundService.
        /// Source: dest ClearJobAndErrorLogsJob.cs line 74.
        /// </summary>
        [Fact]
        public void ClearJobAndErrorLogsJob_Constructor_ShouldThrowOnNullServiceScopeFactory()
        {
            // Arrange
            var (_, _, logger) = CreateMockDependencies();
            var config = CreateDefaultConfiguration();

            // Act
            Action act = () => new ClearJobAndErrorLogsJob(null, logger.Object, config);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("serviceScopeFactory",
                    "IServiceScopeFactory is required for creating DI scopes in RunCleanupAsync");
        }

        /// <summary>
        /// Verifies the constructor throws <see cref="ArgumentNullException"/> when
        /// ILogger is null. ILogger is required for error logging when
        /// ILogService.ClearJobAndErrorLogs() fails, preserving the monolith's
        /// Log().Create(LogType.Error, ...) pattern converted to ILogger.LogError.
        /// Source: dest ClearJobAndErrorLogsJob.cs line 75.
        /// </summary>
        [Fact]
        public void ClearJobAndErrorLogsJob_Constructor_ShouldThrowOnNullLogger()
        {
            // Arrange
            var (scopeFactory, _, _) = CreateMockDependencies();
            var config = CreateDefaultConfiguration();

            // Act
            Action act = () => new ClearJobAndErrorLogsJob(scopeFactory.Object, null, config);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("logger",
                    "ILogger is required for error logging in RunCleanupAsync catch block");
        }

        /// <summary>
        /// Verifies the constructor throws when IConfiguration is null.
        /// The constructor accesses configuration.GetValue&lt;int&gt;() on line 79
        /// without an explicit null check, so a null configuration causes an exception.
        /// Source: dest ClearJobAndErrorLogsJob.cs line 79.
        /// </summary>
        [Fact]
        public void ClearJobAndErrorLogsJob_Constructor_ShouldThrowOnNullConfiguration()
        {
            // Arrange
            var (scopeFactory, _, logger) = CreateMockDependencies();

            // Act
            Action act = () => new ClearJobAndErrorLogsJob(scopeFactory.Object, logger.Object, null);

            // Assert — NullReferenceException thrown because GetValue<int> is called on null
            act.Should().Throw<Exception>(
                "passing null configuration must throw because GetValue<int> is called on it " +
                "without an explicit null guard");
        }

        #endregion

        #region Tests 5-9: Execute/RunCleanupAsync Business Logic

        /// <summary>
        /// Verifies RunCleanupAsync resolves ILogService from a DI scope and calls
        /// ClearJobAndErrorLogs() exactly once per cleanup cycle.
        ///
        /// Preserves the core business rule from monolith source line 18:
        ///   new LogService().ClearJobAndErrorLogs()
        /// Converted to DI-based resolution in microservice (dest lines 144-145):
        ///   using var scope = _serviceScopeFactory.CreateScope();
        ///   var logService = scope.ServiceProvider.GetRequiredService&lt;ILogService&gt;();
        ///   logService.ClearJobAndErrorLogs()
        /// </summary>
        [Fact]
        public async Task Execute_ShouldCallClearJobAndErrorLogs()
        {
            // Arrange
            var (scopeFactory, logService, logger) = CreateMockDependencies();
            var job = CreateJob(scopeFactory, logger);

            // Act — invoke RunCleanupAsync directly to bypass 30-second initial delay
            await InvokeRunCleanupAsync(job);

            // Assert
            logService.Verify(
                s => s.ClearJobAndErrorLogs(),
                Times.Once(),
                "ClearJobAndErrorLogs must be called exactly once per cleanup cycle — " +
                "this is the core business rule from monolith source line 18");
        }

        /// <summary>
        /// Verifies that exceptions thrown by ILogService.ClearJobAndErrorLogs() are caught
        /// and NOT propagated to the caller. Error isolation is critical for background
        /// service stability — an exception in cleanup must not crash the timer loop.
        ///
        /// Preserves error-handling behavior from monolith source lines 20-23:
        ///   catch (Exception ex) { new Log().Create(LogType.Error, "ClearJobAndErrorLogsJob", ex); }
        /// Converted to (dest lines 152-159):
        ///   catch (Exception ex) when (ex is not OperationCanceledException)
        ///   { _logger.LogError(ex, "Error occurred..."); }
        /// </summary>
        [Fact]
        public async Task Execute_ShouldNotThrowWhenLogServiceThrows()
        {
            // Arrange
            var (scopeFactory, _, logger) = CreateMockDependencies(logServiceThrows: true);
            var job = CreateJob(scopeFactory, logger);

            // Act
            Func<Task> act = () => InvokeRunCleanupAsync(job);

            // Assert
            await act.Should().NotThrowAsync(
                "exceptions from ILogService must be caught and logged, never propagated — " +
                "preserving the error isolation pattern from monolith source lines 20-23");
        }

        /// <summary>
        /// Verifies that when ILogService.ClearJobAndErrorLogs() throws an exception,
        /// the error is logged via ILogger.LogError with the original exception preserved.
        ///
        /// Preserves diagnostic behavior from monolith source line 22:
        ///   new Log().Create(LogType.Error, "ClearJobAndErrorLogsJob", ex)
        /// Converted to (dest line 159):
        ///   _logger.LogError(ex, "Error occurred while executing ClearJobAndErrorLogsJob")
        /// </summary>
        [Fact]
        public async Task Execute_ShouldLogErrorWhenLogServiceThrows()
        {
            // Arrange
            var testException = new InvalidOperationException("Simulated log service failure");
            var (scopeFactory, _, logger) = CreateMockDependencies(
                logServiceThrows: true,
                customException: testException);
            var job = CreateJob(scopeFactory, logger);

            // Act
            await InvokeRunCleanupAsync(job);

            // Assert — verify ILogger.Log was called at LogLevel.Error with the original exception
            // ILogger.LogError(ex, message) internally calls ILogger.Log<TState>(LogLevel.Error, ...)
            logger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => true),
                    It.Is<Exception>(ex => ReferenceEquals(ex, testException)),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once(),
                "error must be logged via ILogger.LogError with the original exception — " +
                "preserving the monolith's Log().Create(LogType.Error, ...) diagnostic pattern");
        }

        /// <summary>
        /// Verifies that RunCleanupAsync executes within SecurityContext.OpenSystemScope(),
        /// ensuring system-level elevated permissions during log cleanup. The system user
        /// has the Administrator role and bypasses all entity permission checks.
        ///
        /// Preserves security scope pattern from monolith source line 14:
        ///   using (SecurityContext.OpenSystemScope()) { ... }
        /// Preserved exactly in microservice (dest line 136):
        ///   using (SecurityContext.OpenSystemScope()) { ... }
        /// </summary>
        [Fact]
        public async Task Execute_ShouldRunWithinSystemSecurityScope()
        {
            // Arrange — capture SecurityContext.CurrentUser during ClearJobAndErrorLogs execution
            ErpUser capturedUser = null;
            var (scopeFactory, logService, logger) = CreateMockDependencies();

            logService.Setup(s => s.ClearJobAndErrorLogs())
                .Callback(() => capturedUser = SecurityContext.CurrentUser);

            var job = CreateJob(scopeFactory, logger);

            // Act
            await InvokeRunCleanupAsync(job);

            // Assert — verify the system user was active during execution
            capturedUser.Should().NotBeNull(
                "ClearJobAndErrorLogs must execute within SecurityContext.OpenSystemScope() — " +
                "monolith source line 14: using (SecurityContext.OpenSystemScope())");

            capturedUser.Username.Should().Be("system",
                "the system scope uses the built-in system user with username 'system' " +
                "who has the Administrator role and bypasses all permission checks");
        }

        /// <summary>
        /// Verifies that the SecurityContext scope opened by RunCleanupAsync is properly
        /// disposed even when ILogService.ClearJobAndErrorLogs() throws an exception.
        ///
        /// Preserves the using block pattern from monolith source line 14:
        ///   using (SecurityContext.OpenSystemScope()) { try { ... } catch { ... } }
        /// The C# using statement guarantees IDisposable.Dispose() is called on both
        /// normal completion and exceptional exit paths. After disposal,
        /// SecurityContext.CurrentUser must return null (no lingering scope).
        /// </summary>
        [Fact]
        public async Task Execute_SecurityScope_ShouldBeDisposedEvenOnException()
        {
            // Arrange
            var (scopeFactory, _, logger) = CreateMockDependencies(logServiceThrows: true);
            var job = CreateJob(scopeFactory, logger);

            // Pre-condition: no scope should be open before test
            SecurityContext.CurrentUser.Should().BeNull(
                "no security scope should be open before test execution");

            // Act — RunCleanupAsync opens scope, calls LogService (which throws),
            // catches the exception, logs it, then the using block disposes the scope
            await InvokeRunCleanupAsync(job);

            // Assert — after RunCleanupAsync completes (even with exception),
            // the using block must have disposed the security scope
            SecurityContext.CurrentUser.Should().BeNull(
                "SecurityContext scope must be disposed after RunCleanupAsync completes, " +
                "even when ILogService throws an exception — " +
                "monolith source line 14 using block ensures disposal on all exit paths");
        }

        #endregion

        #region Tests 10-11: ExecuteAsync Method Signature

        /// <summary>
        /// Verifies ExecuteAsync is overridden from BackgroundService, confirming
        /// proper integration with the ASP.NET Core hosted service lifecycle.
        /// The base definition should resolve to BackgroundService.ExecuteAsync.
        /// </summary>
        [Fact]
        public void ExecuteAsync_ShouldBeOverriddenFromBackgroundService()
        {
            // Act
            var method = typeof(ClearJobAndErrorLogsJob).GetMethod(
                "ExecuteAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Assert
            method.Should().NotBeNull(
                "ExecuteAsync must exist on ClearJobAndErrorLogsJob");

            var baseDefinition = method.GetBaseDefinition();
            baseDefinition.DeclaringType.Should().Be(
                typeof(BackgroundService),
                "ExecuteAsync must override BackgroundService.ExecuteAsync — " +
                "not be a new method — ensuring proper hosted service lifecycle integration");
        }

        /// <summary>
        /// Verifies ExecuteAsync accepts a single CancellationToken parameter and returns Task,
        /// matching the BackgroundService.ExecuteAsync(CancellationToken) signature exactly.
        /// The CancellationToken replaces the monolith's JobContext parameter for
        /// cooperative shutdown support.
        /// </summary>
        [Fact]
        public void ExecuteAsync_ShouldAcceptCancellationTokenParameter()
        {
            // Act
            var method = typeof(ClearJobAndErrorLogsJob).GetMethod(
                "ExecuteAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Assert
            method.Should().NotBeNull("ExecuteAsync must exist");

            var parameters = method.GetParameters();
            parameters.Should().HaveCount(1,
                "ExecuteAsync takes exactly one parameter (CancellationToken)");

            parameters[0].ParameterType.Should().Be(typeof(CancellationToken),
                "the single parameter must be CancellationToken, " +
                "replacing the monolith's JobContext parameter");

            method.ReturnType.Should().Be(typeof(Task),
                "ExecuteAsync must return Task for async hosted service execution");
        }

        #endregion

        #region Tests 12-13: Timer and Cancellation Behavior

        /// <summary>
        /// Verifies the default timer interval is 1440 minutes (24 hours) when no
        /// configuration override is provided for Jobs:ClearLogsIntervalMinutes.
        ///
        /// Preserves the original schedule plan interval from SdkPlugin.cs line 95:
        ///   logsSchedulePlan.IntervalInMinutes = 1440
        /// In the microservice architecture, this is the default value used when
        /// the Jobs:ClearLogsIntervalMinutes configuration key is absent.
        /// </summary>
        [Fact]
        public void ExecuteAsync_ShouldDefaultTo1440MinuteInterval()
        {
            // Arrange — use empty configuration so GetValue returns the 1440 default
            var (scopeFactory, _, logger) = CreateMockDependencies();
            var config = CreateDefaultConfiguration();
            var job = CreateJob(scopeFactory, logger, config);

            // Act — access the private _interval field via reflection
            var intervalField = typeof(ClearJobAndErrorLogsJob).GetField(
                "_interval",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Assert
            intervalField.Should().NotBeNull(
                "_interval field must exist on ClearJobAndErrorLogsJob");

            var interval = (TimeSpan)intervalField.GetValue(job);
            interval.Should().Be(
                TimeSpan.FromMinutes(1440),
                "default interval must be 1440 minutes (24 hours) matching the original " +
                "SchedulePlan.IntervalInMinutes = 1440 from SdkPlugin.cs line 95");
        }

        /// <summary>
        /// Verifies that ExecuteAsync supports graceful cancellation via CancellationToken.
        /// When cancellation is requested, the service exits by throwing
        /// OperationCanceledException (the standard .NET cancellation pattern), which
        /// BackgroundService.StopAsync handles as a normal shutdown signal.
        ///
        /// Replaces the monolith's JobContext.Aborted polling mechanism with the
        /// cooperative CancellationToken cancellation pattern.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_ShouldSupportGracefulCancellation()
        {
            // Arrange
            var (scopeFactory, _, logger) = CreateMockDependencies();
            var job = CreateJob(scopeFactory, logger);

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel immediately to trigger graceful shutdown

            // Act — invoke ExecuteAsync via reflection (it's protected override)
            var method = typeof(ClearJobAndErrorLogsJob).GetMethod(
                "ExecuteAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method.Should().NotBeNull("ExecuteAsync must exist for cancellation test");

            var task = (Task)method.Invoke(job, new object[] { cts.Token });

            // Assert — ExecuteAsync should throw OperationCanceledException when the
            // CancellationToken is already cancelled. This is the standard .NET cancellation
            // pattern: Task.Delay throws TaskCanceledException (a subclass of
            // OperationCanceledException), and BackgroundService.StopAsync handles
            // this as a normal graceful shutdown signal.
            Func<Task> act = () => task;
            await act.Should().ThrowAsync<OperationCanceledException>(
                "ExecuteAsync should throw OperationCanceledException when CancellationToken " +
                "is cancelled — this is the standard graceful shutdown pattern that " +
                "BackgroundService.StopAsync handles as normal termination");
        }

        #endregion
    }
}
