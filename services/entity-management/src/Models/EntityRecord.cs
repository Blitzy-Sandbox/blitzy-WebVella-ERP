using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Represents a dynamic entity record as a key-value dictionary.
    /// <para>
    /// In the original WebVella ERP monolith, <c>EntityRecord</c> extended
    /// <c>WebVella.Erp.Utilities.Dynamic.Expando</c> — a custom
    /// <see cref="System.Dynamic.DynamicObject"/> wrapper backed by a
    /// <c>PropertyBag</c> dictionary. For the Entity Management microservice,
    /// the class inherits directly from <see cref="Dictionary{TKey, TValue}"/>
    /// (<c>Dictionary&lt;string, object?&gt;</c>), which provides:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     Indexer access via <c>record["fieldName"]</c> — identical to the
    ///     original Expando property-bag pattern.
    ///   </description></item>
    ///   <item><description>
    ///     Flat JSON serialization — <c>System.Text.Json</c> serializes
    ///     <c>Dictionary&lt;string, object?&gt;</c> as a flat JSON object
    ///     <c>{ "field1": value1, "field2": value2 }</c>, matching the
    ///     Expando-based serialization contract consumed by API clients.
    ///   </description></item>
    ///   <item><description>
    ///     AOT compatibility — no reflection-heavy <c>DynamicObject</c>
    ///     overrides, which is critical for .NET 9 Native AOT Lambda
    ///     cold-start performance (&lt; 1 second target).
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Key differences from the monolith's Expando-based EntityRecord:
    /// <list type="number">
    ///   <item><description>
    ///     No <c>dynamic</c> member resolution — consumers must use
    ///     <c>record["key"]</c> indexer syntax instead of
    ///     <c>((dynamic)record).key</c>. This is intentional because
    ///     <c>DynamicObject</c> is not supported under Native AOT trimming.
    ///   </description></item>
    ///   <item><description>
    ///     No instance wrapping — the original Expando could wrap an
    ///     existing object and reflect its properties. The microservice
    ///     version stores all values in the dictionary directly.
    ///   </description></item>
    ///   <item><description>
    ///     Nullable value type — <c>object?</c> values allow explicit
    ///     <c>null</c> field storage, matching DynamoDB's attribute model
    ///     where attributes can be absent or null.
    ///   </description></item>
    /// </list>
    /// </remarks>
    [Serializable]
    public class EntityRecord : Dictionary<string, object?>
    {
        /// <summary>
        /// Initializes a new, empty <see cref="EntityRecord"/>.
        /// </summary>
        public EntityRecord()
            : base()
        {
        }

        /// <summary>
        /// Initializes a new <see cref="EntityRecord"/> with the specified initial capacity.
        /// Useful when the number of fields is known in advance to avoid dictionary resizing.
        /// </summary>
        /// <param name="capacity">
        /// The initial number of elements the internal storage can contain without resizing.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="capacity"/> is less than zero.
        /// </exception>
        public EntityRecord(int capacity)
            : base(capacity)
        {
        }

        /// <summary>
        /// Initializes a new <see cref="EntityRecord"/> by copying all key-value pairs
        /// from an existing dictionary. Useful for hydrating a record from a DynamoDB
        /// attribute map or from deserialized JSON.
        /// </summary>
        /// <param name="dictionary">
        /// The source dictionary whose elements are copied to the new record.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="dictionary"/> is <c>null</c>.
        /// </exception>
        public EntityRecord(IDictionary<string, object?> dictionary)
            : base(dictionary)
        {
        }

        /// <summary>
        /// Initializes a new <see cref="EntityRecord"/> with the specified
        /// <see cref="IEqualityComparer{T}"/> for key comparison.
        /// Use <see cref="StringComparer.OrdinalIgnoreCase"/> when
        /// case-insensitive field name matching is required.
        /// </summary>
        /// <param name="comparer">
        /// The <see cref="IEqualityComparer{T}"/> implementation to use when
        /// comparing keys, or <c>null</c> to use the default comparer.
        /// </param>
        public EntityRecord(IEqualityComparer<string>? comparer)
            : base(comparer)
        {
        }

        /// <summary>
        /// Initializes a new <see cref="EntityRecord"/> by copying all key-value pairs
        /// from an existing dictionary using the specified key comparer.
        /// </summary>
        /// <param name="dictionary">
        /// The source dictionary whose elements are copied to the new record.
        /// </param>
        /// <param name="comparer">
        /// The <see cref="IEqualityComparer{T}"/> implementation to use when
        /// comparing keys, or <c>null</c> to use the default comparer.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="dictionary"/> is <c>null</c>.
        /// </exception>
        public EntityRecord(IDictionary<string, object?> dictionary, IEqualityComparer<string>? comparer)
            : base(dictionary, comparer)
        {
        }

        /// <summary>
        /// Gets the value associated with the specified key, or returns <c>null</c>
        /// if the key does not exist in the record. This mirrors the Expando behavior
        /// where accessing a non-existent property returned <c>null</c> rather than
        /// throwing <see cref="KeyNotFoundException"/>.
        /// </summary>
        /// <param name="key">The field name to look up.</param>
        /// <returns>
        /// The value associated with <paramref name="key"/> if found;
        /// otherwise, <c>null</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="key"/> is <c>null</c>.
        /// </exception>
        public object? GetValue(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return TryGetValue(key, out var value) ? value : null;
        }

        /// <summary>
        /// Gets the value associated with the specified key, cast to type
        /// <typeparamref name="T"/>, or returns <paramref name="defaultValue"/>
        /// if the key does not exist or the value cannot be cast.
        /// </summary>
        /// <typeparam name="T">The expected type of the field value.</typeparam>
        /// <param name="key">The field name to look up.</param>
        /// <param name="defaultValue">
        /// The value to return if the key is not found or the value is not
        /// of type <typeparamref name="T"/>. Defaults to <c>default(T)</c>.
        /// </param>
        /// <returns>
        /// The value cast to <typeparamref name="T"/> if found and compatible;
        /// otherwise, <paramref name="defaultValue"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="key"/> is <c>null</c>.
        /// </exception>
        public T? GetValue<T>(string key, T? defaultValue = default)
        {
            ArgumentNullException.ThrowIfNull(key);

            if (TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }

            return defaultValue;
        }
    }

    /// <summary>
    /// Represents a paginated list of <see cref="EntityRecord"/> instances with a
    /// total count for pagination support.
    /// <para>
    /// Source: <c>WebVella.Erp/Api/Models/EntityRecordList.cs</c> — migrated to
    /// use <c>System.Text.Json</c> attributes for AOT-compatible serialization.
    /// The <see cref="TotalCount"/> property serializes as <c>"total_count"</c>
    /// in JSON for backward API compatibility.
    /// </para>
    /// </summary>
    [Serializable]
    public class EntityRecordList : List<EntityRecord>
    {
        /// <summary>
        /// Gets or sets the total number of records matching the query criteria,
        /// regardless of pagination (skip/limit). This value is used by API consumers
        /// to render pagination controls.
        /// </summary>
        /// <remarks>
        /// JSON key: <c>"total_count"</c> (backward-compatible with the monolith's
        /// <c>[JsonProperty(PropertyName = "total_count")]</c> Newtonsoft annotation).
        /// </remarks>
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; } = 0;

        /// <summary>
        /// Initializes a new, empty <see cref="EntityRecordList"/> with
        /// <see cref="TotalCount"/> set to zero.
        /// </summary>
        public EntityRecordList()
            : base()
        {
        }

        /// <summary>
        /// Initializes a new <see cref="EntityRecordList"/> with the specified
        /// initial capacity.
        /// </summary>
        /// <param name="capacity">
        /// The number of elements that the new list can initially store without resizing.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="capacity"/> is less than zero.
        /// </exception>
        public EntityRecordList(int capacity)
            : base(capacity)
        {
        }

        /// <summary>
        /// Initializes a new <see cref="EntityRecordList"/> from an existing
        /// collection of <see cref="EntityRecord"/> instances.
        /// </summary>
        /// <param name="collection">
        /// The collection whose elements are copied to the new list.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="collection"/> is <c>null</c>.
        /// </exception>
        public EntityRecordList(IEnumerable<EntityRecord> collection)
            : base(collection)
        {
        }
    }
}
