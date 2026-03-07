using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace WebVella.Erp.Tests.Crm.Fixtures
{
    /// <summary>
    /// Immutable value object that records a single invocation of
    /// <see cref="MockSearchService.RegenSearchField"/>. Used by test assertions
    /// to verify that the correct entity, record, and indexed fields were
    /// passed to the search indexing pipeline.
    /// </summary>
    public class SearchServiceCall
    {
        /// <summary>
        /// The entity name that was passed to RegenSearchField (e.g. "account", "contact", "case").
        /// </summary>
        public string EntityName { get; }

        /// <summary>
        /// The record ID extracted from the record dictionary via record["id"].
        /// Returns <see cref="Guid.Empty"/> when the record is null or does not contain an "id" key.
        /// </summary>
        public Guid RecordId { get; }

        /// <summary>
        /// The list of indexed field names that were passed to RegenSearchField.
        /// This corresponds to the search index field definitions from Configuration.cs
        /// (e.g. AccountSearchIndexFields, CaseSearchIndexFields, ContactSearchIndexFields).
        /// </summary>
        public List<string> IndexedFields { get; }

        /// <summary>
        /// UTC timestamp captured at the moment the mock recorded this call.
        /// Useful for verifying call ordering in multi-step test scenarios.
        /// </summary>
        public DateTime InvokedAt { get; }

        /// <summary>
        /// Initializes a new <see cref="SearchServiceCall"/> recording all parameters
        /// from a single <see cref="MockSearchService.RegenSearchField"/> invocation.
        /// </summary>
        /// <param name="entityName">The entity name passed to the method.</param>
        /// <param name="recordId">The extracted record ID (Guid.Empty if not available).</param>
        /// <param name="indexedFields">The indexed fields list (may be null; stored as empty list).</param>
        /// <param name="invokedAt">The UTC timestamp of the invocation.</param>
        public SearchServiceCall(string entityName, Guid recordId, List<string> indexedFields, DateTime invokedAt)
        {
            EntityName = entityName ?? string.Empty;
            RecordId = recordId;
            IndexedFields = indexedFields != null ? new List<string>(indexedFields) : new List<string>();
            InvokedAt = invokedAt;
        }
    }

    /// <summary>
    /// Configurable mock replacement for the CRM service's SearchService.
    /// <para>
    /// In the monolith, <c>SearchService.RegenSearchField()</c> performs heavy
    /// infrastructure operations: loading entity/relation metadata via
    /// <c>EntityRelationManager</c> and <c>EntityManager</c>, executing EQL queries,
    /// formatting field values by type, and updating the record's <c>x_search</c> field
    /// via <c>RecordManager</c>. This mock replaces all of that with configurable
    /// test behavior, recording calls for assertion while optionally allowing
    /// controlled responses (success, exception, or custom action).
    /// </para>
    /// <para>
    /// Thread-safe for concurrent test execution. All shared state is guarded by
    /// a lock object, and the public <see cref="Calls"/> property returns a snapshot
    /// copy to prevent external mutation.
    /// </para>
    /// </summary>
    public class MockSearchService
    {
        // ──────────────────────────────────────────────────────────────
        //  Call Recording — Thread-safe state
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Internal mutable list of recorded calls. All access is synchronized
        /// via <see cref="_lock"/>.
        /// </summary>
        private readonly List<SearchServiceCall> _calls = new List<SearchServiceCall>();

        /// <summary>
        /// Lock object used to synchronize access to <see cref="_calls"/> and
        /// the configurable behavior fields for thread safety during concurrent
        /// test execution.
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// Returns a thread-safe snapshot copy of all recorded calls.
        /// Each access creates a new list so that external consumers cannot
        /// hold a reference to the mutable internal list.
        /// </summary>
        public IReadOnlyList<SearchServiceCall> Calls
        {
            get
            {
                lock (_lock)
                {
                    return _calls.ToList();
                }
            }
        }

        /// <summary>
        /// Returns the current number of recorded calls in a thread-safe manner.
        /// </summary>
        public int CallCount
        {
            get
            {
                lock (_lock)
                {
                    return _calls.Count;
                }
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Configurable Behavior
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, <see cref="RegenSearchField"/> will throw
        /// <see cref="_exceptionToThrow"/> after recording the call.
        /// </summary>
        private bool _shouldThrow = false;

        /// <summary>
        /// The exception instance to throw when <see cref="_shouldThrow"/> is true.
        /// Nullable — only set when <see cref="SetupToThrow(Exception)"/> or
        /// <see cref="SetupToThrow{TException}"/> is called.
        /// </summary>
        private Exception? _exceptionToThrow = null;

        /// <summary>
        /// Optional custom action to execute after recording the call.
        /// Receives the same parameters as <see cref="RegenSearchField"/>:
        /// (entityName, record, indexedFields). Nullable — only set when
        /// <see cref="SetupWithAction"/> is called.
        /// </summary>
        private Action<string, object, List<string>>? _customAction = null;

        /// <summary>
        /// Configures the mock to succeed silently (default behavior).
        /// Clears any previously configured exception or custom action.
        /// Calls are still recorded for assertion.
        /// </summary>
        /// <returns>This instance for fluent chaining.</returns>
        public MockSearchService SetupToSucceed()
        {
            _shouldThrow = false;
            _exceptionToThrow = null;
            _customAction = null;
            return this;
        }

        /// <summary>
        /// Configures the mock to throw the specified exception after recording the call.
        /// This allows tests to verify error-handling paths when search indexing fails.
        /// </summary>
        /// <param name="exception">The exception to throw. Must not be null.</param>
        /// <returns>This instance for fluent chaining.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="exception"/> is null.</exception>
        public MockSearchService SetupToThrow(Exception exception)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception), "Exception to throw must not be null.");
            }

            _shouldThrow = true;
            _exceptionToThrow = exception;
            _customAction = null;
            return this;
        }

        /// <summary>
        /// Configures the mock to throw a new instance of <typeparamref name="TException"/>
        /// after recording the call.
        /// </summary>
        /// <typeparam name="TException">
        /// The exception type to instantiate and throw. Must have a parameterless constructor.
        /// </typeparam>
        /// <returns>This instance for fluent chaining.</returns>
        public MockSearchService SetupToThrow<TException>() where TException : Exception, new()
        {
            _shouldThrow = true;
            _exceptionToThrow = new TException();
            _customAction = null;
            return this;
        }

        /// <summary>
        /// Configures the mock to execute a custom action after recording the call.
        /// The action receives the same parameters as <see cref="RegenSearchField"/>:
        /// (entityName, record, indexedFields).
        /// This is useful for tests that need to perform additional side effects or
        /// capture values beyond what the standard call recording provides.
        /// </summary>
        /// <param name="action">The custom action to execute. Must not be null.</param>
        /// <returns>This instance for fluent chaining.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="action"/> is null.</exception>
        public MockSearchService SetupWithAction(Action<string, object, List<string>> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action), "Custom action must not be null.");
            }

            _shouldThrow = false;
            _exceptionToThrow = null;
            _customAction = action;
            return this;
        }

        // ──────────────────────────────────────────────────────────────
        //  Core Mock Method — RegenSearchField
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Mock implementation of the CRM SearchService's RegenSearchField method.
        /// <para>
        /// In the real implementation (WebVella.Erp.Plugins.Next/Services/SearchService.cs),
        /// this method:
        /// 1. Loads entity/relation metadata from the database
        /// 2. Validates indexed fields against entity metadata
        /// 3. Executes an EQL query to fetch the record
        /// 4. Formats field values by type (AutoNumber, Currency, Date, etc.)
        /// 5. Updates the record's x_search field via RecordManager
        /// </para>
        /// <para>
        /// This mock replaces all of that by simply recording the call parameters
        /// and then executing the configured behavior (succeed, throw, or custom action).
        /// </para>
        /// </summary>
        /// <param name="entityName">
        /// The entity name (e.g. "account", "contact", "case") whose search index
        /// field should be regenerated.
        /// </param>
        /// <param name="record">
        /// The entity record object. In the monolith this is an <c>EntityRecord</c>
        /// (which inherits from <c>Expando</c> implementing <c>IDictionary&lt;string, object&gt;</c>).
        /// The mock extracts <c>record["id"]</c> for tracking when the record implements
        /// <c>IDictionary&lt;string, object&gt;</c>.
        /// </param>
        /// <param name="indexedFields">
        /// The list of field names to include in the search index.
        /// Corresponds to Configuration.cs field lists (e.g. AccountSearchIndexFields).
        /// </param>
        public void RegenSearchField(string entityName, object record, List<string> indexedFields)
        {
            // Extract the record ID from the record object.
            // EntityRecord inherits from Expando which implements IDictionary<string, object>.
            // Handle null records and missing "id" keys gracefully.
            var recordId = Guid.Empty;
            if (record is IDictionary<string, object> dict && dict.ContainsKey("id"))
            {
                var idValue = dict["id"];
                if (idValue is Guid guidValue)
                {
                    recordId = guidValue;
                }
                else if (idValue != null)
                {
                    // Attempt to parse string representation of Guid
                    Guid parsedGuid;
                    if (Guid.TryParse(idValue.ToString(), out parsedGuid))
                    {
                        recordId = parsedGuid;
                    }
                }
            }

            // Record the call in a thread-safe manner
            lock (_lock)
            {
                _calls.Add(new SearchServiceCall(
                    entityName,
                    recordId,
                    indexedFields,
                    DateTime.UtcNow));
            }

            // Execute configured behavior after recording (so the call is always recorded
            // even if the behavior throws an exception — matches real-world error tracking patterns)
            if (_shouldThrow && _exceptionToThrow != null)
            {
                throw _exceptionToThrow;
            }

            if (_customAction != null)
            {
                _customAction(entityName, record, indexedFields);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Assertion Helpers
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Asserts that <see cref="RegenSearchField"/> was called exactly once.
        /// </summary>
        /// <exception cref="Xunit.Sdk.XunitException">
        /// When the actual call count does not equal 1.
        /// </exception>
        public void VerifyCalledOnce()
        {
            int count = CallCount;
            Assert.True(count == 1,
                $"MockSearchService.RegenSearchField was expected to be called exactly once, " +
                $"but was called {count} time(s).");
        }

        /// <summary>
        /// Asserts that <see cref="RegenSearchField"/> was called exactly
        /// <paramref name="expectedCount"/> times.
        /// </summary>
        /// <param name="expectedCount">The expected number of invocations.</param>
        /// <exception cref="Xunit.Sdk.XunitException">
        /// When the actual call count does not match the expected count.
        /// </exception>
        public void VerifyCalledTimes(int expectedCount)
        {
            int actualCount = CallCount;
            Assert.Equal(expectedCount, actualCount);
        }

        /// <summary>
        /// Asserts that <see cref="RegenSearchField"/> was called at least once
        /// with the specified entity name.
        /// </summary>
        /// <param name="entityName">
        /// The entity name to search for in recorded calls (e.g. "account", "contact", "case").
        /// </param>
        /// <exception cref="Xunit.Sdk.XunitException">
        /// When no recorded call matches the specified entity name.
        /// </exception>
        public void VerifyCalledWith(string entityName)
        {
            var calls = Calls;
            bool found = calls.Any(c =>
                string.Equals(c.EntityName, entityName, StringComparison.Ordinal));

            Assert.True(found,
                $"MockSearchService.RegenSearchField was expected to be called with entityName='{entityName}', " +
                $"but no matching call was found. Recorded calls: [{FormatCallSummary(calls)}].");
        }

        /// <summary>
        /// Asserts that <see cref="RegenSearchField"/> was called at least once
        /// with the specified entity name and record ID combination.
        /// </summary>
        /// <param name="entityName">The entity name to search for.</param>
        /// <param name="recordId">The record ID to search for.</param>
        /// <exception cref="Xunit.Sdk.XunitException">
        /// When no recorded call matches both the entity name and record ID.
        /// </exception>
        public void VerifyCalledWith(string entityName, Guid recordId)
        {
            var calls = Calls;
            bool found = calls.Any(c =>
                string.Equals(c.EntityName, entityName, StringComparison.Ordinal) &&
                c.RecordId == recordId);

            Assert.True(found,
                $"MockSearchService.RegenSearchField was expected to be called with " +
                $"entityName='{entityName}' and recordId='{recordId}', " +
                $"but no matching call was found. Recorded calls: [{FormatCallSummary(calls)}].");
        }

        /// <summary>
        /// Asserts that <see cref="RegenSearchField"/> was never called.
        /// </summary>
        /// <exception cref="Xunit.Sdk.XunitException">
        /// When one or more calls were recorded.
        /// </exception>
        public void VerifyNeverCalled()
        {
            int count = CallCount;
            Assert.True(count == 0,
                $"MockSearchService.RegenSearchField was expected to never be called, " +
                $"but was called {count} time(s).");
        }

        /// <summary>
        /// Asserts that <see cref="RegenSearchField"/> was called at least once with
        /// the specified entity name and the exact set of indexed fields.
        /// Field order is significant — the lists must match element-by-element.
        /// </summary>
        /// <param name="entityName">The entity name to search for.</param>
        /// <param name="expectedFields">
        /// The expected indexed field names, in order. Corresponds to the search index
        /// field definitions from Configuration.cs (e.g. AccountSearchIndexFields).
        /// </param>
        /// <exception cref="Xunit.Sdk.XunitException">
        /// When no recorded call matches the entity name with the exact indexed fields.
        /// </exception>
        public void VerifyCalledWithFields(string entityName, List<string> expectedFields)
        {
            var calls = Calls;
            bool found = calls.Any(c =>
                string.Equals(c.EntityName, entityName, StringComparison.Ordinal) &&
                FieldListsAreEqual(c.IndexedFields, expectedFields));

            Assert.True(found,
                $"MockSearchService.RegenSearchField was expected to be called with " +
                $"entityName='{entityName}' and indexedFields=[{FormatFieldList(expectedFields)}], " +
                $"but no matching call was found. Recorded calls: [{FormatCallSummary(calls)}].");
        }

        // ──────────────────────────────────────────────────────────────
        //  Reset
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Clears all recorded calls and resets configurable behavior to the default
        /// (succeed silently). Call this between test scenarios when reusing the same
        /// mock instance across multiple test methods.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _calls.Clear();
            }

            _shouldThrow = false;
            _exceptionToThrow = null;
            _customAction = null;
        }

        // ──────────────────────────────────────────────────────────────
        //  Private Helpers
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Compares two string lists for element-by-element equality.
        /// Both lists must have the same count and each element must match
        /// at the corresponding index (ordinal comparison).
        /// </summary>
        private static bool FieldListsAreEqual(List<string> actual, List<string> expected)
        {
            if (actual == null && expected == null)
            {
                return true;
            }

            if (actual == null || expected == null)
            {
                return false;
            }

            if (actual.Count != expected.Count)
            {
                return false;
            }

            for (int i = 0; i < actual.Count; i++)
            {
                if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Formats a list of field names into a comma-separated string for
        /// inclusion in assertion failure messages.
        /// </summary>
        private static string FormatFieldList(List<string> fields)
        {
            if (fields == null || fields.Count == 0)
            {
                return "<empty>";
            }

            return string.Join(", ", fields.Select(f => $"\"{f}\""));
        }

        /// <summary>
        /// Formats a summary of all recorded calls for inclusion in assertion
        /// failure messages, providing full context about what was actually called.
        /// </summary>
        private static string FormatCallSummary(IReadOnlyList<SearchServiceCall> calls)
        {
            if (calls == null || calls.Count == 0)
            {
                return "<no calls recorded>";
            }

            return string.Join("; ", calls.Select(c =>
                $"(entity='{c.EntityName}', recordId='{c.RecordId}', fields=[{FormatFieldList(c.IndexedFields)}])"));
        }
    }
}
